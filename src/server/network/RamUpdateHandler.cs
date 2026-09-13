using EVE.Components.Server;
using EVE.Components.Shared.Packets.C2S;
using LogicWorld.SharedCode.Networking;

namespace EVE.Components.Server.Network
{
    public class RamUpdateHandler : PacketHandler<RamUpdatePacket>
    {
        private readonly RamServer _server;

        public RamUpdateHandler(RamServer server)
        {
            _server = server;
        }

        public override void Handle(RamUpdatePacket packet, HandlerContext context)
        {
            _server.UpdateRom(packet.ComponentAddress, packet.RomData);
        }
    }
}