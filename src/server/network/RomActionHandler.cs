using EVE.Components.Server;
using EVE.Components.Shared.Packets.C2S;
using LogicWorld.SharedCode.Networking;

namespace EVE.Components.Server.Network
{
    public class RomActionHandler : PacketHandler<RomActionPacket>
    {
        private readonly RamServer _server;

        public RomActionHandler(RamServer server)
        {
            _server = server;
        }

        public override void Handle(RomActionPacket packet, HandlerContext context)
        {
            _server.HandleAction(packet.ComponentAddress, packet.Action);
        }
    }
}