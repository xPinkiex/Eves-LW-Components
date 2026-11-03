using EVE.Components.Client.Windows;
using EVE.Components.Shared.Packets.S2C;
using LogicWorld.SharedCode.Networking;

namespace EVE.Components.Client.Network
{
    public class RamResultHandler : PacketHandler<RamResultPacket>
    {
        public override void Handle(RamResultPacket packet, HandlerContext context)
        {
            RamEditorWindow.ShowServerResult(packet);
        }
    }
}
