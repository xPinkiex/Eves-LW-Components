using System;
using System.Text;
using EVE.Components.Shared;
using LogicAPI.Server.Components;
using LogicLog;

namespace EVE.Components.Server
{
    public class MicrocodeRomLogic : LogicComponent
    {
        private byte[] _romData = new byte[0];
        private byte[] _originalData = new byte[0];
        private string _sourceText = null;
        private string _originalSourceText = null;
        private bool _skipNextDeserialize;

        private bool _prevWriteEnable;

        public const int InputCount = 49;
        public const int WEIndex = 0;        // write enable
        public const int AddrBase = 1;       // address bits 15-0
        public const int DataInBase = 17;    // write data bits 31-0

        public override bool HasPersistentValues => true;

        public void SetRomData(byte[] data, string sourceText)
        {
            _romData = data ?? new byte[0];
            _originalData = (byte[])_romData.Clone();
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
            _romData = (byte[])_originalData.Clone();
            _sourceText = _originalSourceText;
            _skipNextDeserialize = true;
            RamServer.RequestPersistence(this);
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
                    address |= (1 << (15 - i));
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
                        dataIn |= (1u << (31 - i));
                    }
                }
                WriteDword(byteOffset, dataIn);
            }
            _prevWriteEnable = writeEnable;

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
                base.Outputs[i].On = (value & (1u << (31 - i))) != 0;
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
            if (_sourceText != null)
            {
                return Encoding.UTF8.GetBytes(_sourceText);
            }
            return _romData;
        }

        protected override void DeserializeData(byte[] data)
        {
            if (_skipNextDeserialize)
            {
                _skipNextDeserialize = false;
                return;
            }

            _romData = data ?? new byte[0];
            _sourceText = null;
            _originalSourceText = null;

            if (data != null && data.Length > 0
                && RamParser.TryDecodeUtf8(data, out string text))
            {
                var result = RamParser.Parse(text);
                if (result.Success && result.ByteCount > 0)
                {
                    _sourceText = text;
                    _romData = result.Data;
                }
            }

            _originalData = (byte[])_romData.Clone();
            _originalSourceText = _sourceText;
            RamServer.Logger?.Info($"EVE_Components: Loaded MicrocodeROM from world data, {_romData.Length} bytes");
            QueueLogicUpdate();
        }

        protected override void SavePersistentValuesToCustomData()
        {
            _skipNextDeserialize = true;
        }
    }
}