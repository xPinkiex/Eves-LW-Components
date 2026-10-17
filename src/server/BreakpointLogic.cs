using EccsLogicWorldAPI.Server;
using LogicAPI.Server.Components;
using LogicLog;
using LogicWorld.SharedCode.ExtraData;

namespace EVE.Components.Server
{
    public class BreakpointLogic : LogicComponent
    {
        private bool _prevInput;
        private static ILogicLogger _logger;
        private static ExtraDataAccessor<bool> _pauseAccessor;

        protected override void Initialize()
        {
            _logger = LogicLogger.For<BreakpointLogic>();

            if (_pauseAccessor == null)
            {
                var extraData = ServiceGetter.getService<ExtraData>();
                _pauseAccessor = extraData.GetDataAccessor<bool>("MHG.SimulationPaused", false);
            }
        }

        protected override void DoLogicUpdate()
        {
            bool currentInput = Inputs[0].On;
            if (currentInput && !_prevInput)
            {
                _pauseAccessor?.SetData(true);
                _logger?.Info("Breakpoint hit! Simulation paused.");
            }
            _prevInput = currentInput;
        }

        public override bool InputAtIndexShouldTriggerComponentLogicUpdates(int inputIndex)
        {
            return inputIndex == 0;
        }
    }
}