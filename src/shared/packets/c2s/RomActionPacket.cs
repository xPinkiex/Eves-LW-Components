using LogicAPI.Data;
using LogicAPI.Networking.Packets;
using MessagePack;

namespace EVE.Components.Shared.Packets.C2S
{
    public enum RomActionType
    {
        Save = 0,
        Restore = 1,
    }

    [MessagePackObject]
    public class RomActionPacket : Packet
    {
        [Key(0)] public ComponentAddress ComponentAddress;
        [Key(1)] public RomActionType Action;
    }
}