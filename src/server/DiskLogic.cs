using System;
using System.IO;
using System.Text;
using EVE.Components.Shared;
using LogicAPI.Server.Components;

namespace EVE.Components.Server
{
    /// <summary>
    /// A disk on the LW32 CPU block's bus: 16 MiB kept in a file, disk.bin in the current
    /// save's "bins" folder (made, all zeros, if missing). The same pins and handshake as Eve's
    /// RAM (32-bit): READ or WRITE for one tick with the address and the size (BYTE, WORD or
    /// DWORD; none: DWORD), and the data for a write; DATA READY the next tick, a read's value
    /// the tick after, for that one tick. The address counts modulo the disk's size, so the
    /// circuit decodes the disk's window (gate READ and WRITE) and passes the address as it is.
    ///
    /// The file is the disk: every write goes to it in order, so it holds what the circuit
    /// wrote even after a crash of the game, and it is not part of the world save (only the
    /// handshake is). A world reloaded from an older save keeps the newer disk, like a PC's
    /// hard disk. One disk block per world: a second one logs an error, reads 0 and drops
    /// writes. A bigger file than 16 MiB is used up to 16 MiB; a smaller one is extended.
    ///
    /// Inputs: 0-31 address, 32-63 write data, 64 READ, 65 WRITE, 66 BYTE, 67 WORD, 68 DWORD.
    /// Outputs: 0-31 data (only in a read's data tick), 32 DATA READY.
    /// </summary>
    public class DiskLogic : LogicComponent
    {
        public const int Size = 16 << 20;                      // 16 MiB
        public const string FileName = "disk.bin";
        public const int DataInBase = 32;
        public const int Read = 64, Write = 65, Byte = 66, Word = 67, DWord = 68;   // inputs
        public const int DataOut = 0, Ready = 32;                                   // outputs

        private const int Idle = 0, ReadyThenData = 1, ReadyThenIdle = 2, DataThenIdle = 3;
        private int _phase;
        private uint _answer;
        private bool _prevRead, _prevWrite;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EVEDISK1\n");

        // The world's disk: the one block that holds the file.
        private static readonly object Lock = new object();
        private static DiskLogic _owner;

        private byte[] _data;                                  // the file's bytes, null until opened
        private FileStream _file;
        private bool _failed;                                  // the file can't be opened
        private bool _warned;                                  // said that another block has the file
        private int _unflushed;                                // writes since the last flush to the OS

        public override bool HasPersistentValues => true;

        public override bool InputAtIndexShouldTriggerComponentLogicUpdates(int inputIndex)
        {
            return true;
        }

        /// <summary>The file this block uses: disk.bin in the save's bins folder (or Logic World/roms).</summary>
        public static string FilePath()
        {
            string dir = null;
            try { dir = RamParser.FileRootProvider?.Invoke(); } catch { }
            if (string.IsNullOrEmpty(dir)) dir = RamParser.FileRoot;
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, FileName);
        }

        // ---- the file ----

        /// <summary>Opens the file on the first access (the save's folder is known by then).</summary>
        private bool Open()
        {
            if (_data != null) return true;
            if (_failed) return false;
            lock (Lock)
            {
                if (_owner != null && _owner != this)
                {
                    // Another block has the file: this one reads 0 until that one is gone
                    // (a second disk, or a reload where the old block isn't disposed yet).
                    if (!_warned)
                        RamServer.Logger?.Error("EVE_Components: a second Eve's Disk in this world; only one is used, this one reads 0");
                    _warned = true;
                    return false;
                }
                string path = FilePath();
                try
                {
                    if (path == null) throw new IOException("no folder for disk.bin");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096);
                    var data = new byte[Size];
                    int n = 0, got;
                    while (n < Size && (got = _file.Read(data, n, Size - n)) > 0) n += got;
                    if (_file.Length < Size) _file.SetLength(Size);   // zeros after what was there
                    _data = data;
                    _owner = this;
                    RamServer.Logger?.Info($"EVE_Components: Eve's Disk uses {path} ({n} bytes read)");
                    return true;
                }
                catch (Exception e)
                {
                    _failed = true;
                    _file?.Dispose();
                    _file = null;
                    RamServer.Logger?.Error($"EVE_Components: Eve's Disk can't use {path}: {e.Message}");
                    return false;
                }
            }
        }

        private void Store(uint address, uint value, int size)
        {
            int at = (int)(address % Size);
            for (int k = 0; k < size; k++)
                _data[(at + k) % Size] = (byte)(value >> (8 * k));
            lock (Lock)
            {
                if (_file == null) return;
                try
                {
                    if (at + size <= Size)
                    {
                        _file.Seek(at, SeekOrigin.Begin);
                        _file.Write(_data, at, size);
                    }
                    else
                    {
                        for (int k = 0; k < size; k++)
                        {
                            int a = (at + k) % Size;
                            _file.Seek(a, SeekOrigin.Begin);
                            _file.WriteByte(_data[a]);
                        }
                    }
                    if (++_unflushed >= 4096) Flush();
                }
                catch (Exception e)
                {
                    RamServer.Logger?.Error($"EVE_Components: Eve's Disk write failed: {e.Message}");
                }
            }
        }

        /// <summary>Hands what was written to the OS (in order; the game's crash can't lose it then).</summary>
        private void Flush()
        {
            try { _file?.Flush(false); } catch { }
            _unflushed = 0;
        }

        private uint Load(uint address, int size)
        {
            int at = (int)(address % Size);
            uint v = 0;
            for (int k = 0; k < size; k++)
                v |= (uint)_data[(at + k) % Size] << (8 * k);
            return v;
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
                bool open = Open();
                if (newRead)
                {
                    _answer = open ? Load(address, size) : 0;
                    _phase = ReadyThenData;
                }
                else
                {
                    if (open) Store(address, value, size);
                    _phase = ReadyThenIdle;
                }
                ready = true;
            }
            else if (_phase == ReadyThenData)
            {
                data = _answer;
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
                QueueLogicUpdate();
        }

        // ---- persistence: "EVEDISK1\n" and the handshake; the contents are the file's ----

        protected override byte[] SerializeCustomData()
        {
            lock (Lock) Flush();
            var bytes = new byte[Magic.Length + 6];
            Buffer.BlockCopy(Magic, 0, bytes, 0, Magic.Length);
            bytes[Magic.Length] = (byte)_phase;
            Buffer.BlockCopy(BitConverter.GetBytes(_answer), 0, bytes, Magic.Length + 1, 4);
            bytes[Magic.Length + 5] = (byte)((_prevRead ? 1 : 0) | (_prevWrite ? 2 : 0));
            return bytes;
        }

        protected override void DeserializeData(byte[] data)
        {
            _phase = Idle;
            _answer = 0;
            _prevRead = _prevWrite = false;
            if (data == null || data.Length < Magic.Length + 6) return;
            for (int i = 0; i < Magic.Length; i++)
                if (data[i] != Magic[i]) return;
            int pos = Magic.Length;
            _phase = data[pos] <= DataThenIdle ? data[pos] : Idle;
            _answer = BitConverter.ToUInt32(data, pos + 1);
            _prevRead = (data[pos + 5] & 1) != 0;
            _prevWrite = (data[pos + 5] & 2) != 0;
            QueueLogicUpdate();
        }

        public override void Dispose()
        {
            lock (Lock)
            {
                Flush();
                _file?.Dispose();
                _file = null;
                _data = null;
                if (_owner == this) _owner = null;
            }
            base.Dispose();
        }
    }
}
