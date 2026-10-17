using System;
using System.Text;
using EVE.Components.Shared;
using LogicAPI.Server.Components;
using LogicLog;

namespace EVE.Components.Server
{
    public class RamLogic : LogicComponent
    {
        private byte[] _romData = new byte[0];
        private byte[] _originalData = new byte[0];
        private string _sourceText = null;
        private string _originalSourceText = null;
        private bool _skipNextDeserialize;

        private bool _prevWriteEnable;

        public const int InputCount = 25;
        public const int WEIndex = 16;       // write enable
        public const int DataInBase = 17;    // write data bits 0-7

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
                if (base.Inputs[i].On)
                {
                    address |= (1 << i);
                }
            }

            // Write: rising edge of write-enable latches data-in at addressed byte.
            bool writeEnable = base.Inputs[WEIndex].On;
            if (writeEnable && !_prevWriteEnable)
            {
                byte dataIn = 0;
                for (int i = 0; i < 8; i++)
                {
                    if (base.Inputs[DataInBase + i].On)
                    {
                        dataIn |= (byte)(1 << i);
                    }
                }
                WriteByte(address, dataIn);
            }
            _prevWriteEnable = writeEnable;

            // Read: always show the byte at the current address.
            byte value = 0;
            if (address < _romData.Length)
            {
                value = _romData[address];
            }

            for (int i = 0; i < 8; i++)
            {
                base.Outputs[i].On = (value & (1 << i)) != 0;
            }
        }

        private void WriteByte(int address, byte value)
        {
            if (address >= 65536)
            {
                return;
            }
            if (_romData.Length <= address)
            {
                // Grow geometrically so sequential writes stay cheap; cap at 64KB.
                int newLength = Math.Max(address + 1, Math.Min(_romData.Length * 2, 65536));
                newLength = Math.Max(newLength, address + 1);
                Array.Resize(ref _romData, newLength);
            }
            _romData[address] = value;
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
            RamServer.Logger?.Info($"EVE_Components: Loaded ROM from world data, {_romData.Length} bytes");
            QueueLogicUpdate();
        }

        protected override void SavePersistentValuesToCustomData()
        {
            _skipNextDeserialize = true;
        }
    }
}