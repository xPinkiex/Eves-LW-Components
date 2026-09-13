using LogicAPI.Data;
using LogicAPI.Networking.Packets;
using MessagePack;

namespace EVE.Components.Shared.Packets.C2S
{
    [MessagePackObject]
    public class RamUpdatePacket : Packet
    {
        [Key(0)] public ComponentAddress ComponentAddress;
        [Key(1)] public byte[] RomData;
    }
}