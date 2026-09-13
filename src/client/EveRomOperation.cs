using EVE.Components.Client.Keybindings;
using EVE.Components.Client.Windows;
using FancyInput;
using LogicAPI.Data;
using LogicWorld.Building.Overhaul;
using LogicWorld.GameStates;
using LogicWorld.Interfaces;

namespace EVE.Components.Client
{
    public class EveRomOperation : BuildingOperation
    {
        public override string IconHexCode => "f285";

        public override bool CanOperateOn(ComponentSelection selection)
        {
            if (!RamClient.ServerHasMod)
            {
                RamClient.Logger.Error("Server does not support EVE_Components mod");
                return false;
            }
            ComponentType ramType = Instances.MainWorld.ComponentTypes.GetComponentType("EVE_Components.ProgramRAM");
            ComponentType microType = Instances.MainWorld.ComponentTypes.GetComponentType("EVE_Components.MicrocodeROM");
            foreach (ComponentAddress componentAddress in selection)
            {
                ComponentType type = Instances.MainWorld.Data.Lookup(componentAddress).Data.Type;
                if (type != ramType && type != microType)
                {
                    return false;
                }
            }
            return true;
        }

        public override void BeginOperationOn(ComponentSelection selection)
        {
            foreach (ComponentAddress address in selection)
            {
                RamEditorWindow.openFor(address);
                break;
            }
        }

        public override InputTrigger OperationStarter => RamTrigger.EditProgram;
    }
}