using System;
using LogicAPI.Server.Components;
using LogicLog;

namespace EVE.Components.Server
{
    /// Eve's LW32 CPU: the LW32 CPU (lw-compiler's emulator core, native/liblw32core.so)
    /// on the CPU's bus: it runs the same programs as the hardware CPU, with the RAM and
    /// the devices on the bus (docs/lw32-cpu.html; the bus and its timing:
    /// lw-compiler's docs/cpu-block.md).
    ///
    /// Inputs:  0-31 data in (the bus's data lines), 32 DATA READY, 33 RUN, 34 RESET.
    /// Outputs: 0-31 address, 32-63 data out (only in a write's tick, else all low),
    ///          64 READ, 65 WRITE, 66 BYTE, 67 WORD, 68 DWORD, 69 HALTED.
    /// An access is on the outputs for one tick; then the core waits for DATA READY,
    /// and on a read samples the data in the tick after it. All of that is the core's:
    /// this class only moves pins to and from it once a tick, and keeps its state in
    /// the world save (so a run goes on after a reload, in the middle of an access too).
    public class CpuLogic : LogicComponent
    {
        public const int DataIn = 0, Ready = 32, Run = 33, Reset = 34;
        public const int Address = 0, DataOut = 32, Read = 64, Write = 65, Byte = 66, Word = 67,
                         DWord = 68, Halted = 69;

        private static readonly ILogicLogger Log = LogicLogger.For<CpuLogic>();

        private readonly object _lock = new object();   // the core isn't thread-safe; saves come from elsewhere
        private IntPtr _core = IntPtr.Zero;
        private byte[] _restore;                        // a saved state to load once the core exists
        private bool _skipNextDeserialize;
        private bool _reportedHalt, _reportedMissing;

        public override bool HasPersistentValues => true;

        protected override void Initialize()
        {
            EnsureCore();
            QueueLogicUpdate();
        }

        // The data lines change all the time on a shared bus: the core reads them only
        // while it waits, when it runs every tick anyway.
        public override bool InputAtIndexShouldTriggerComponentLogicUpdates(int inputIndex)
        {
            return inputIndex >= Ready;
        }

        protected override void DoLogicUpdate()
        {
            lock (_lock)
            {
                if (!EnsureCore())
                    return;
                var input = new Lw32Native.In
                {
                    Run = Inputs[Run].On ? 1u : 0u,
                    Reset = Inputs[Reset].On ? 1u : 0u,
                    Ready = Inputs[Ready].On ? 1u : 0u,
                    Data = Word32(DataIn),
                };
                Lw32Native.Tick(_core, ref input, out Lw32Native.Out o);
                SetWord32(Address, o.Address);
                SetWord32(DataOut, o.Write != 0 ? o.Data : 0u);
                Outputs[Read].On = o.Read != 0;
                Outputs[Write].On = o.Write != 0;
                Outputs[Byte].On = o.Size == 1;
                Outputs[Word].On = o.Size == 2;
                Outputs[DWord].On = o.Size == 4;
                Outputs[Halted].On = o.Halted != 0;

                if (o.Halted != 0 && !_reportedHalt)
                {
                    _reportedHalt = true;
                    string why = Lw32Native.CoreError(_core);
                    var regs = new uint[22];
                    Lw32Native.Regs(_core, regs, out ulong n);
                    Log.Info($"LW32 CPU halted after {n} instructions, PC 0x{regs[16]:X}, R1 0x{regs[1]:X}" +
                             (why.Length > 0 ? $": {why}" : ""));
                }
                else if (o.Halted == 0)
                {
                    _reportedHalt = false;
                }

                // Running, or waiting for the bus: next tick too. (Halted, or stopped by
                // RUN between instructions: an input change wakes it.)
                if (Lw32Native.Busy(_core) != 0)
                    QueueLogicUpdate();
            }
        }

        private uint Word32(int first)
        {
            uint v = 0;
            for (int i = 0; i < 32; i++)
                if (Inputs[first + i].On) v |= 1u << i;
            return v;
        }

        private void SetWord32(int first, uint v)
        {
            for (int i = 0; i < 32; i++)
                Outputs[first + i].On = (v & (1u << i)) != 0;
        }

        /// The core exists (made at power-on, or from the saved state); false when the
        /// native library isn't loaded (logged once).
        private bool EnsureCore()
        {
            if (_core != IntPtr.Zero)
                return true;
            if (!Lw32Native.Loaded)
            {
                if (!_reportedMissing)
                {
                    _reportedMissing = true;
                    Log.Error($"LW32 CPU: no core, the native library isn't loaded: {Lw32Native.Error}");
                }
                return false;
            }
            _core = Lw32Native.New();
            if (_core == IntPtr.Zero)
                return false;
            if (_restore != null)
            {
                if (!Lw32Native.Restore(_core, _restore))
                    Log.Error("LW32 CPU: the saved state isn't one this core knows; starting at power-on");
                _restore = null;
            }
            return true;
        }

        protected override byte[] SerializeCustomData()
        {
            lock (_lock)
            {
                if (_core != IntPtr.Zero)
                    return Lw32Native.Save(_core);
                return _restore ?? new byte[0];
            }
        }

        protected override void DeserializeData(byte[] data)
        {
            if (_skipNextDeserialize)
            {
                _skipNextDeserialize = false;
                return;
            }
            lock (_lock)
            {
                if (data == null || data.Length == 0)
                    return;                      // a new block: power-on
                if (_core != IntPtr.Zero)
                {
                    if (!Lw32Native.Restore(_core, data))
                        Log.Error("LW32 CPU: the saved state isn't one this core knows; keeping the current one");
                }
                else
                {
                    _restore = data;             // loaded when the core is made
                }
            }
            QueueLogicUpdate();
        }

        protected override void SavePersistentValuesToCustomData()
        {
            _skipNextDeserialize = true;
        }

        public override void Dispose()
        {
            lock (_lock)
            {
                if (_core != IntPtr.Zero)
                {
                    Lw32Native.Free(_core);
                    _core = IntPtr.Zero;
                }
            }
            base.Dispose();
        }
    }
}
