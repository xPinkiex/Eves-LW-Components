using System;
using EVE.Components.Client.Keybindings;
using EVE.Components.Client.Windows;
using EVE.Components.Shared.Packets.C2S;
using EccsLogicWorldAPI.Client.Hooks;
using FancyInput;
using LogicAPI.Client;
using LogicLog;
using LogicWorld.SharedCode.Networking;

namespace EVE.Components.Client
{
    public class RamClient : ClientMod
    {
        public new static ILogicLogger Logger;
        private static bool? _serverHasMod;

        public static bool ServerHasMod
        {
            get
            {
                _serverHasMod ??= PacketManager.TryGetCodeFromType(typeof(RamUpdatePacket), out ushort _);
                return _serverHasMod.Value;
            }
        }

        protected override void Initialize()
        {
            Logger = base.Logger;
            Logger.Info("EVE_Components client mod loading");

            CustomInput.Register<RamContext, RamTrigger>("EVE_Components");

            RamEditorWindow.initOnce();
            WorldHook.worldLoading += () => {
                try
                {
                    RamEditorWindow.init();
                }
                catch (Exception e)
                {
                    Logger.Error($"Failed to initialize EVE_Components GUI: {e}");
                }
            };

            Logger.Info("EVE_Components client mod loaded");
        }
    }
}