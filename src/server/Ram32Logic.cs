using LogicAPI.Server.Components;

namespace EVE.Components.Server
{
    /// <summary>
    /// RAM with a 32-bit byte address and a 32-bit data bus. Every access is a dword:
    /// the output is always the 4 bytes starting at the address (little-endian), at any
    /// alignment, and a write replaces those 4 bytes. Backing memory is the same 64 KiB
    /// as <see cref="RamLogic"/> (the editor limit); bytes beyond it read as 0 and
    /// writes to them are ignored. Decoding the high address bits is left to the circuit.
    ///
    /// Inputs: 0-31 address bits 0-31, 32-63 write data bits 0-31, 64 write enable.
    /// Outputs: 0-31 data bits 0-31.
    /// </summary>
    public class Ram32Logic : RamLogic
    {
        public new const int WEIndex = 64;
        public new const int DataInBase = 32;

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
                    uint a = address + (uint)k;     // wraps at 2^32 like the CPU's address bus
                    if (a < 65536)
                    {
                        WriteByte((int)a, b);
                    }
                }
            }
            _prevWriteEnable = writeEnable;

            uint value = 0;
            for (int k = 0; k < 4; k++)
            {
                uint a = address + (uint)k;
                if (a < (uint)_romData.Length)
                {
                    value |= (uint)_romData[a] << (8 * k);
                }
            }

            for (int i = 0; i < 32; i++)
            {
                base.Outputs[i].On = (value & (1u << i)) != 0;
            }
        }
    }
}
