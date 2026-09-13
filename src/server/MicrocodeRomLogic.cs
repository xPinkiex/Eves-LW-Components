using System;
using LogicAPI.Server.Components;
using LogicLog;

namespace EVE.Components.Server
{
    public class MicrocodeRomLogic : LogicComponent
    {
        private byte[] _romData = new byte[0];
        private byte[] _originalData = new byte[0];

        private bool _prevWriteEnable;
        private bool _prevSave;
        private bool _prevRestore;

        public const int InputCount = 51;
        public const int AddrBase = 0;       // address bits 15-0
        public const int WEIndex = 16;       // write enable
        public const int SaveIndex = 17;     // save trigger
        public const int RestoreIndex = 18;  // restore trigger
        public const int DataInBase = 19;    // write data bits 31-0

        public override bool HasPersistentValues => true;

        public void SetRomData(byte[] data)
        {
            _romData = data ?? new byte[0];
            _originalData = (byte[])_romData.Clone();
            QueueLogicUpdate();
        }

        public override bool InputAtIndexShouldTriggerComponentLogicUpdates(int inputIndex)
        {
            return true;
        }

        protected override void DoLogicUpdate()
        {
            int address = 0;
            for (int i = 0; i < 16; i++)
            {
                if (base.Inputs[AddrBase + i].On)
                {
                    address |= (1 << i);
                }
            }

            int byteOffset = address * 4;

            bool writeEnable = base.Inputs[WEIndex].On;
            if (writeEnable && !_prevWriteEnable)
            {
                uint dataIn = 0;
                for (int i = 0; i < 32; i++)
                {
                    if (base.Inputs[DataInBase + i].On)
                    {
                        dataIn |= (1u << i);
                    }
                }
                WriteDword(byteOffset, dataIn);
            }
            _prevWriteEnable = writeEnable;

            bool save = base.Inputs[SaveIndex].On;
            if (save && !_prevSave)
            {
                RamServer.RequestPersistence(this);
            }
            _prevSave = save;

            bool restore = base.Inputs[RestoreIndex].On;
            if (restore && !_prevRestore)
            {
                _romData = (byte[])_originalData.Clone();
                QueueLogicUpdate();
            }
            _prevRestore = restore;

            uint value = 0;
            if (byteOffset + 3 < _romData.Length)
            {
                value = (uint)(_romData[byteOffset]
                    | (_romData[byteOffset + 1] << 8)
                    | (_romData[byteOffset + 2] << 16)
                    | (_romData[byteOffset + 3] << 24));
            }

            for (int i = 0; i < 32; i++)
            {
                base.Outputs[i].On = (value & (1u << i)) != 0;
            }
        }

        private void WriteDword(int byteOffset, uint value)
        {
            int requiredLength = byteOffset + 4;
            if (requiredLength > 262144)
            {
                return;
            }
            if (_romData.Length < requiredLength)
            {
                int newLength = Math.Max(requiredLength, Math.Min(_romData.Length * 2, 262144));
                newLength = Math.Max(newLength, requiredLength);
                Array.Resize(ref _romData, newLength);
            }
            _romData[byteOffset] = (byte)(value & 0xFF);
            _romData[byteOffset + 1] = (byte)((value >> 8) & 0xFF);
            _romData[byteOffset + 2] = (byte)((value >> 16) & 0xFF);
            _romData[byteOffset + 3] = (byte)((value >> 24) & 0xFF);
        }

        protected override byte[] SerializeCustomData()
        {
            return _romData;
        }

        protected override void DeserializeData(byte[] data)
        {
            _romData = data ?? new byte[0];
            _originalData = (byte[])_romData.Clone();
            RamServer.Logger?.Info($"EVE_Components: Loaded MicrocodeROM from world data, {_romData.Length} bytes");
            QueueLogicUpdate();
        }

        protected override void SavePersistentValuesToCustomData()
        {
        }
    }
}