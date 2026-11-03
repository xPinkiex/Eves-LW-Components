using LogicAPI.Data;
using LogicAPI.Networking.Packets;
using MessagePack;

namespace EVE.Components.Shared.Packets.S2C
{
    /// <summary>
    /// The server's answer to a RamUpdatePacket whose text used @file (the server reads those
    /// files, from the save's "bins" folder): how many bytes were loaded, or why it failed.
    /// </summary>
    [MessagePackObject]
    public class RamResultPacket : Packet
    {
        [Key(0)] public ComponentAddress ComponentAddress;
        [Key(1)] public bool Success;
        [Key(2)] public int ByteCount;
        [Key(3)] public string Error;
    }
}
