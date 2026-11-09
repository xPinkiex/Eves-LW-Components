using System;
using System.IO;
using System.Text;
using EVE.Components.Shared;
using LogicAPI.Server.Components;

namespace EVE.Components.Server
{
    /// <summary>
    /// RAM with a 32-bit byte address on the LW32 CPU block's bus (lw-compiler's
    /// docs/cpu-block.md): an access is READ or WRITE for one tick with the address and
    /// the size (BYTE, WORD or DWORD; none: DWORD), and the data for a write. The RAM
    /// answers as fast as the bus allows: DATA READY the next tick, and on a read the
    /// value (the size's bytes from the address, little-endian, zero-extended) on the data
    /// outputs the tick after that, for that one tick; they are low otherwise, so they can
    /// share the bus's data lines. A write stores the size's low bytes. Decoding the
    /// address is left to the circuit: gate READ and WRITE (with the 2g map, A31 low).
    ///
    /// The whole 4 GiB address space is backed, but memory is only reserved in 512 KiB pages
    /// when something is first written to them; untouched pages read as 0 and cost nothing.
    ///
    /// Inputs: 0-31 address, 32-63 write data, 64 READ, 65 WRITE, 66 BYTE, 67 WORD, 68 DWORD.
    /// Outputs: 0-31 data (only in a read's data tick), 32 DATA READY.
    /// </summary>
    public class Ram32Logic : LogicComponent, IRomHost
    {
        public const int PageShift = 19;                       // 512 KiB
        public const int PageSize = 1 << PageShift;
        public const int PageCount = 1 << (32 - PageShift);    // 8192
        public const int DataInBase = 32;
        public const int Read = 64, Write = 65, Byte = 66, Word = 67, DWord = 68;   // inputs
        public const int DataOut = 0, Ready = 32;                                   // outputs

        // The bus handshake: what the next update puts on the outputs.
        private const int Idle = 0, ReadyThenData = 1, ReadyThenIdle = 2, DataThenIdle = 3;
        private int _phase;
        private uint _answer;                                  // a read's value, for its data tick
        private bool _prevRead, _prevWrite;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EVERAM32\n");

        private byte[][] _pages = new byte[PageCount][];
        private byte[][] _originalPages = new byte[PageCount][];
        private string _sourceText = null;
        private string _originalSourceText = null;
        private bool _skipNextDeserialize;

        public override bool HasPersistentValues => true;

        public override bool InputAtIndexShouldTriggerComponentLogicUpdates(int inputIndex)
        {
            return true;
        }

        // ---- storage ----

        private byte ReadByte(uint address)
        {
            byte[] page = _pages[address >> PageShift];
            return page == null ? (byte)0 : page[address & (PageSize - 1)];
        }

        private void WriteByte(uint address, byte value)
        {
            int index = (int)(address >> PageShift);
            byte[] page = _pages[index];
            if (page == null)
            {
                if (value == 0)
                {
                    return;             // writing 0 to untouched memory changes nothing
                }
                page = new byte[PageSize];
                _pages[index] = page;
            }
            page[address & (PageSize - 1)] = value;
        }

        private static byte[][] ClonePages(byte[][] source)
        {
            var copy = new byte[PageCount][];
            for (int i = 0; i < PageCount; i++)
            {
                if (source[i] != null)
                {
                    copy[i] = (byte[])source[i].Clone();
                }
            }
            return copy;
        }

        private static bool IsZero(byte[] page)
        {
            for (int i = 0; i < page.Length; i++)
            {
                if (page[i] != 0) return false;
            }
            return true;
        }

        private void LoadImage(byte[] data)
        {
            _pages = new byte[PageCount][];
            if (data == null) return;
            for (int i = 0; i < data.Length; i++)
            {
                WriteByte((uint)i, data[i]);
            }
        }

        // ---- editor interface ----

        public void SetRomData(byte[] data, string sourceText)
        {
            LoadImage(data);
            _originalPages = ClonePages(_pages);
            _sourceText = sourceText;
            _originalSourceText = sourceText;
            _skipNextDeserialize = true;
            QueueLogicUpdate();
        }

        public void Save()
        {
            RamServer.RequestPersistence(this);
        }

        public void Restore()
        {
            _pages = ClonePages(_originalPages);
            _sourceText = _originalSourceText;
            _skipNextDeserialize = true;
            RamServer.RequestPersistence(this);
            QueueLogicUpdate();
        }

        // ---- logic ----

        protected override void DoLogicUpdate()
        {
            bool read = Inputs[Read].On, write = Inputs[Write].On;
            bool newRead = read && !_prevRead, newWrite = write && !_prevWrite;
            _prevRead = read;
            _prevWrite = write;

            bool ready = false;
            uint data = 0;
            if (newRead || newWrite)
            {
                uint address = 0, value = 0;
                for (int i = 0; i < 32; i++)
                {
                    if (Inputs[i].On) address |= 1u << i;
                    if (Inputs[DataInBase + i].On) value |= 1u << i;
                }
                int size = Inputs[Byte].On ? 1 : Inputs[Word].On ? 2 : 4;
                if (newRead)
                {
                    _answer = 0;
                    for (int k = 0; k < size; k++)
                        _answer |= (uint)ReadByte(address + (uint)k) << (8 * k);
                    _phase = ReadyThenData;
                }
                else
                {
                    for (int k = 0; k < size; k++)
                        WriteByte(address + (uint)k, (byte)(value >> (8 * k)));   // wraps at 2^32
                    _phase = ReadyThenIdle;
                }
                ready = true;
            }
            else if (_phase == ReadyThenData)
            {
                data = _answer;                                // the tick after DATA READY
                _phase = DataThenIdle;
            }
            else if (_phase == ReadyThenIdle || _phase == DataThenIdle)
            {
                _phase = Idle;
            }

            Outputs[Ready].On = ready;
            for (int i = 0; i < 32; i++)
                Outputs[DataOut + i].On = (data & (1u << i)) != 0;
            if (_phase != Idle)
                QueueLogicUpdate();                            // the handshake's next step
        }

        // ---- persistence ----
        // "EVERAM32\n", the handshake (phase, the pending answer, the last READ and WRITE:
        // a save in the middle of an access answers it after the reload), then either 'T'
        // and the editor's source text (runtime writes are dropped, as in Eve's RAM), or 'P'
        // and, for each page that is not all zero, a uint32 page index (little-endian)
        // followed by the 512 KiB of the page.

        protected override byte[] SerializeCustomData()
        {
            using (var stream = new MemoryStream())
            {
                stream.Write(Magic, 0, Magic.Length);
                stream.WriteByte((byte)_phase);
                stream.Write(BitConverter.GetBytes(_answer), 0, 4);
                stream.WriteByte((byte)((_prevRead ? 1 : 0) | (_prevWrite ? 2 : 0)));
                if (_sourceText != null)
                {
                    stream.WriteByte((byte)'T');
                    byte[] text = Encoding.UTF8.GetBytes(_sourceText);
                    stream.Write(text, 0, text.Length);
                    return stream.ToArray();
                }
                stream.WriteByte((byte)'P');
                for (int i = 0; i < PageCount; i++)
                {
                    if (_pages[i] != null && !IsZero(_pages[i]))
                    {
                        stream.Write(BitConverter.GetBytes((uint)i), 0, 4);
                        stream.Write(_pages[i], 0, PageSize);
                    }
                }
                return stream.ToArray();
            }
        }

        protected override void DeserializeData(byte[] data)
        {
            if (_skipNextDeserialize)
            {
                _skipNextDeserialize = false;
                return;
            }

            _pages = new byte[PageCount][];
            _sourceText = null;
            _originalSourceText = null;

            _phase = Idle;
            _answer = 0;
            _prevRead = _prevWrite = false;
            if (data != null && StartsWithMagic(data) && data.Length >= Magic.Length + 7)
            {
                int pos = Magic.Length;
                _phase = data[pos] <= DataThenIdle ? data[pos] : Idle;
                _answer = BitConverter.ToUInt32(data, pos + 1);
                _prevRead = (data[pos + 5] & 1) != 0;
                _prevWrite = (data[pos + 5] & 2) != 0;
                byte kind = data[pos + 6];
                pos += 7;
                if (kind == (byte)'T')
                {
                    string saved = Encoding.UTF8.GetString(data, pos, data.Length - pos);
                    var parsed = RamParser.Parse(saved);
                    _sourceText = saved;
                    if (parsed.Success)
                        LoadImage(parsed.Data);
                    else
                        RamServer.Logger?.Error($"EVE_Components: RAM (32-bit) could not load its text: {parsed.Error}");
                    pos = data.Length;
                }
                while (pos + 4 + PageSize <= data.Length)
                {
                    uint index = BitConverter.ToUInt32(data, pos);
                    pos += 4;
                    if (index < PageCount)
                    {
                        var page = new byte[PageSize];
                        Buffer.BlockCopy(data, pos, page, 0, PageSize);
                        _pages[index] = page;
                    }
                    pos += PageSize;
                }
            }
            else if (data != null && data.Length > 0 && RamParser.TryDecodeUtf8(data, out string text))
            {
                var result = RamParser.Parse(text);
                if (result.Success && result.ByteCount > 0)
                {
                    _sourceText = text;
                    LoadImage(result.Data);
                }
            }

            _originalPages = ClonePages(_pages);
            _originalSourceText = _sourceText;
            RamServer.Logger?.Info("EVE_Components: Loaded RAM (32-bit) from world data");
            QueueLogicUpdate();
        }

        private static bool StartsWithMagic(byte[] data)
        {
            if (data.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++)
            {
                if (data[i] != Magic[i]) return false;
            }
            return true;
        }

        protected override void SavePersistentValuesToCustomData()
        {
            _skipNextDeserialize = true;
        }
    }
}
