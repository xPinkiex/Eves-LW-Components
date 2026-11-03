using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LogicLog;

namespace EVE.Components.Server
{
    /// <summary>
    /// Harmony patch on <c>SimulationManager.LogicWorld.Server.IServerTicker.Tick</c> that
    /// makes mid-batch pause requests honored within a single tick.
    ///
    /// The vanilla <c>Tick</c> method only checks <c>SimulationIsRunning</c> once at the top,
    /// then runs a catch-up loop:
    ///     while (TickCountdownSeconds &lt; 0) {
    ///         LogicManager.DoLogicUpdate();
    ///         TickCountdownSeconds += TickPeriodSeconds;
    ///         if (Stopwatch.Elapsed &gt;= MaxAllowedTimeStep) break;
    ///     }
    /// A pause request issued inside one of those <c>DoLogicUpdate()</c> calls (e.g. by
    /// <see cref="BreakpointLogic"/>) flips <c>SimulationPaused</c>, which synchronously sets
    /// <c>_RunSimulation_PlayerConfigured = false</c> via the manager's data-update callback.
    /// But the running loop never re-checks it, so the sim keeps ticking until either the
    /// time-budget expires or <c>TickCountdownSeconds</c> catches up. At high TPS more ticks
    /// fit inside the wall-clock budget, so breakpoint latency scales with TPS.
    ///
    /// This transpiler inserts a <c>get_SimulationIsRunning()</c> check immediately after every
    /// <c>DoLogicUpdate()</c> call and branches to <c>ret</c> if the sim has been paused,
    /// aborting the batch right after the tick that requested the pause.
    /// </summary>
    public static class BreakpointPausePatch
    {
        private const string HarmonyId = "EVE.Components.BreakpointPause";

        private static ILogicLogger _logger;
        private static bool _applied;

        public static void Initialize(ILogicLogger logger)
        {
            _logger = logger;
            if (_applied)
            {
                return;
            }

            try
            {
                var simManagerType = AccessTools.TypeByName("LogicWorld.Server.HostServices.SimulationManager");
                if (simManagerType == null)
                {
                    _logger?.Warn("BreakpointPausePatch: SimulationManager type not found; patch skipped (breakpoint will have vanilla latency).");
                    return;
                }

                // The explicit interface implementation is named "LogicWorld.Server.IServerTicker.Tick".
                var tickMethod = simManagerType.GetMethod(
                    "LogicWorld.Server.IServerTicker.Tick",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (tickMethod == null)
                {
                    _logger?.Warn("BreakpointPausePatch: SimulationManager.Tick not found; patch skipped (breakpoint will have vanilla latency).");
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(tickMethod, transpiler: new HarmonyMethod(typeof(BreakpointPausePatch), nameof(TranspileTick)));
                _applied = true;
                _logger?.Info("BreakpointPausePatch: applied; pause requests will be honored within 1 tick at any TPS.");
            }
            catch (Exception e)
            {
                _logger?.Warn("BreakpointPausePatch: failed to apply patch: " + e);
            }
        }

        public static IEnumerable<CodeInstruction> TranspileTick(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = instructions.ToList();

            var doLogicUpdateCall = AccessTools.Method("LogicAPI.Server.Managers.ILogicManager:DoLogicUpdate");
            var isRunningGetter = AccessTools.Method("LogicWorld.Server.HostServices.SimulationManager:get_SimulationIsRunning");

            // Locate the method's final 'ret' and attach a label we can branch to.
            CodeInstruction retInstr = null;
            for (int i = codes.Count - 1; i >= 0; i--)
            {
                if (codes[i].opcode == OpCodes.Ret)
                {
                    retInstr = codes[i];
                    break;
                }
            }

            if (doLogicUpdateCall == null || isRunningGetter == null || retInstr == null)
            {
                _logger?.Warn("BreakpointPausePatch: transpiler could not locate required members (DoLogicUpdate/SimulationIsRunning/ret); leaving Tick unmodified.");
                return codes;
            }

            var retLabel = generator.DefineLabel();
            retInstr.labels.Add(retLabel);

            var insert = new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, isRunningGetter),
                new CodeInstruction(OpCodes.Brfalse, retLabel),
            };

            var newCodes = new List<CodeInstruction>(codes.Count + insert.Length);
            int patchedCalls = 0;
            foreach (var c in codes)
            {
                newCodes.Add(c);
                if (c.opcode == OpCodes.Callvirt && c.operand is MethodInfo mi && mi == doLogicUpdateCall)
                {
                    newCodes.AddRange(insert);
                    patchedCalls++;
                }
            }

            if (patchedCalls == 0)
            {
                _logger?.Warn("BreakpointPausePatch: transpiler found no DoLogicUpdate callvirt inside Tick; leaving Tick unmodified.");
                return codes;
            }

            return newCodes;
        }
    }
}