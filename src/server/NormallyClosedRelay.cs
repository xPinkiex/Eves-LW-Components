using LogicAPI.Server.Components;
using LogicWorld.Server.Circuitry;

namespace EVE.Components.Server
{
    public class NormallyClosedRelay : LogicComponent
    {
        private InputPeg _input1;
        private InputPeg _input2;
        private bool _previouslyClosed;

        protected override void Initialize()
        {
            _input1 = (InputPeg)Inputs[1];
            _input2 = (InputPeg)Inputs[2];
        }

        protected override void DoLogicUpdate()
        {
            bool closed = !Inputs[0].On;
            if (closed == _previouslyClosed)
            {
                return;
            }
            if (closed)
            {
                _input1.AddPhasicLinkWithUnsafe(_input2);
            }
            else
            {
                _input1.RemovePhasicLinkWithUnsafe(_input2);
            }
            _previouslyClosed = closed;
        }

        public override bool InputAtIndexShouldTriggerComponentLogicUpdates(int inputIndex)
        {
            return inputIndex == 0;
        }
    }
}
