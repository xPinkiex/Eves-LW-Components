using System;
using System.IO;
using System.Text;
using EVE.Components.Shared;
using LogicAPI.Server.Components;

namespace EVE.Components.Server
{
    /// <summary>
    /// RAM with a 32-bit byte address and a 32-bit data bus. Every access is a dword: the
    /// output is always the 4 bytes starting at the address (little-endian, any alignment)
    /// and a write replaces those 4 bytes. Decoding the address is left to the circuit.
    ///
    /// The whole 4 GiB address space is backed, but memory is only reserved in 512 KiB pages
    /// when something is first written to them; untouched pages read as 0 and cost nothing.
    ///
    /// Inputs: 0-31 address bits 0-31, 32-63 write data bits 0-31, 64 write enable.
    /// Outputs: 0-31 data bits 0-31.
    /// </summary>
    public class Ram32Logic : LogicComponent, IRomHost
    {
        public const int PageShift = 19;                       // 512 KiB
        public const int PageSize = 1 << PageShift;
        public const int PageCount = 1 << (32 - PageShift);    // 8192
        public const int WEIndex = 64;
        public const int DataInBase = 32;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EVERAM32\n");

        private byte[][] _pages = new byte[PageCount][];
        private byte[][] _originalPages = new byte[PageCount][];
        private string _sourceText = null;
        private string _originalSourceText = null;
        private bool _skipNextDeserialize;
        private bool _prevWriteEnable;

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
            uint address = 0;
            for (int i = 0; i < 32; i++)
            {
                if (base.Inputs[i].On)
                {
                    address |= 1u << i;
                }
            }

            bool writeEnable = base.Inputs[WEIndex].On;
            if (writeEnable && !_prevWriteEnable)
            {
                for (int k = 0; k < 4; k++)
                {
                    byte b = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if (base.Inputs[DataInBase + 8 * k + bit].On)
                        {
                            b |= (byte)(1 << bit);
                        }
                    }
                    WriteByte(address + (uint)k, b);    // wraps at 2^32 like the address bus
                }
            }
            _prevWriteEnable = writeEnable;

            uint value = 0;
            for (int k = 0; k < 4; k++)
            {
                value |= (uint)ReadByte(address + (uint)k) << (8 * k);
            }

            for (int i = 0; i < 32; i++)
            {
                base.Outputs[i].On = (value & (1u << i)) != 0;
            }
        }

        // ---- persistence ----
        // With editor source text: the text (runtime writes are dropped, as in Eve's RAM).
        // Without: "EVERAM32\n", then for each page that is not all zero a uint32 page index
        // (little-endian) followed by the 512 KiB of the page.

        protected override byte[] SerializeCustomData()
        {
            if (_sourceText != null)
            {
                return Encoding.UTF8.GetBytes(_sourceText);
            }
            using (var stream = new MemoryStream())
            {
                stream.Write(Magic, 0, Magic.Length);
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

            if (data != null && StartsWithMagic(data))
            {
                int pos = Magic.Length;
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
