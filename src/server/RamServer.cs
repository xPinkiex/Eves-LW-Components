using System;
using EVE.Components.Server.Network;
using EccsLogicWorldAPI.Server;
using EccsLogicWorldAPI.Server.Injectors;
using LogicAPI.Data;
using LogicAPI.Server;
using LogicAPI.Server.Components;
using LogicAPI.Server.Managers;
using LogicLog;
using LogicWorld.LogicCode;
using LogicWorld.Server.Circuitry;
using EVE.Components.Shared.Packets.C2S;

namespace EVE.Components.Server
{
    public class RamServer : ServerMod
    {
        public new static ILogicLogger Logger;
        private static ICircuitryManager _circuits;
        private static IWorldMutationManager _mutations;

        protected override void Initialize()
        {
            Logger = base.Logger;
            Logger.Info("EVE_Components server mod loading");

            RawPacketHandlerInjector.addPacketHandler(new RamUpdateHandler(this));
            RawPacketHandlerInjector.addPacketHandler(new RomActionHandler(this));

            _circuits = ServiceGetter.getService<ICircuitryManager>();
            _mutations = ServiceGetter.getService<IWorldMutationManager>();
            Logger.Info("EVE_Components server mod loaded");
        }

        public void UpdateRom(ComponentAddress address, byte[] data, string sourceText)
        {
            Logger.Info($"EVE_Components: UpdateRom, address={address}, length={data?.Length ?? 0}");
            LogicComponent component = _circuits.LookupComponent(address);
            if (component == null)
            {
                Logger.Error($"EVE_Components: Component not found at address {address}");
                return;
            }

            if (component is RamLogic rom)
            {
                rom.SetRomData(data, sourceText);
                _mutations.ForceDataRefresh(rom);
            }
            else if (component is MicrocodeRomLogic microRom)
            {
                microRom.SetRomData(data, sourceText);
                _mutations.ForceDataRefresh(microRom);
            }
            else
            {
                Logger.Error($"EVE_Components: Component at {address} is not a RamLogic or MicrocodeRomLogic (actual type: {component.GetType().Name})");
            }
        }

        public static void RequestPersistence(LogicComponent rom)
        {
            try
            {
                _mutations?.ForceDataRefresh(rom);
                Logger?.Info("EVE_Components: Persisted RAM contents to world data (save trigger)");
            }
            catch (Exception e)
            {
                Logger?.Error($"EVE_Components: Failed to persist RAM contents: {e}");
            }
        }

        public void HandleAction(ComponentAddress address, RomActionType action)
        {
            LogicComponent component = _circuits.LookupComponent(address);
            if (component == null)
            {
                Logger.Error($"EVE_Components: Component not found at address {address}");
                return;
            }

            if (component is RamLogic ram)
            {
                if (action == RomActionType.Save) ram.Save();
                else if (action == RomActionType.Restore) ram.Restore();
            }
            else if (component is MicrocodeRomLogic microRom)
            {
                if (action == RomActionType.Save) microRom.Save();
                else if (action == RomActionType.Restore) microRom.Restore();
            }
            else
            {
                Logger.Error($"EVE_Components: Component at {address} is not a RamLogic or MicrocodeRomLogic (actual type: {component.GetType().Name})");
            }
        }
    }
}