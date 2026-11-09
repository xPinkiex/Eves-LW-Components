using System;
using System.IO;
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
using EVE.Components.Shared;
using EVE.Components.Shared.Packets.C2S;
using EVE.Components.Shared.Packets.S2C;
using LogicAPI.Networking;
using LogicAPI.Server.Networking;
using LogicWorld.Server;
using LogicWorld.Server.Saving;

namespace EVE.Components.Server
{
    public class RamServer : ServerMod
    {
        public new static ILogicLogger Logger;
        private static ICircuitryManager _circuits;
        private static IWorldMutationManager _mutations;
        private static NetworkServer _network;
        private static ISaveManager _saves;

        private static ISaveManager TryGetSaveManager()
        {
            try { return Program.Get<ISaveManager>(); } catch { }
            try { return Program.Get<SaveManager>(); } catch { }
            return null;
        }

        /// <summary>saves/&lt;current save&gt;/bins, created if needed; null if no save is active.</summary>
        private static string SaveBinsFolder()
        {
            string dir = _saves?.ActiveSaveDirectory;
            if (string.IsNullOrEmpty(dir)) return null;
            string bins = Path.Combine(dir, "bins");
            Directory.CreateDirectory(bins);
            return bins;
        }

        protected override void Initialize()
        {
            Logger = base.Logger;
            Logger.Info("EVE_Components server mod loading");
            RamParser.InitFileRoot(Files.Path);
            _network = ServiceGetter.getService<NetworkServer>();
            _saves = TryGetSaveManager();
            if (_saves != null)
            {
                RamParser.FileRootProvider = SaveBinsFolder;
                Logger.Info("EVE_Components: @file paths are relative to <current save>/bins");
            }
            else
            {
                Logger.Warn($"EVE_Components: save manager not found; @file paths are relative to {RamParser.FileRoot}");
            }

            RawPacketHandlerInjector.addPacketHandler(new RamUpdateHandler(this));
            RawPacketHandlerInjector.addPacketHandler(new RomActionHandler(this));

            _circuits = ServiceGetter.getService<ICircuitryManager>();
            _mutations = ServiceGetter.getService<IWorldMutationManager>();

            // Apply the Breakpoint's pause-timing patch (Harmony). Gracefully no-ops
            // and logs a warning if the SimulationManager shape changes in a future
            // Logic World update — the mod still loads, breakpoint keeps vanilla latency.
            BreakpointPausePatch.Initialize(Logger);

            // The LW32 emulator core for the CPU block (native/liblw32core.so). For now
            // only a loading test: the log says whether it loaded. Never stops the mod.
            Lw32Native.Load(Files.Path, Logger);

            Logger.Info("EVE_Components server mod loaded");
        }

        public void UpdateRom(ComponentAddress address, byte[] data, string sourceText, Connection sender)
        {
            // Text with @file comes unparsed (data == null): only the server reads those files.
            if (data == null && RamParser.HasFileDirective(sourceText))
            {
                var result = RamParser.Parse(sourceText);
                Reply(sender, new RamResultPacket
                {
                    ComponentAddress = address,
                    Success = result.Success,
                    ByteCount = result.ByteCount,
                    Error = result.Error,
                });
                if (!result.Success)
                {
                    Logger.Info($"EVE_Components: @file load failed: {result.Error}");
                    return;
                }
                data = result.Data;
            }

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

        private static void Reply(Connection sender, RamResultPacket packet)
        {
            try
            {
                if (sender != null) _network?.Send(sender, packet);
            }
            catch (Exception e)
            {
                Logger?.Error($"EVE_Components: could not send the load result: {e.Message}");
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