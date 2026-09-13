using System;
using FancyInput;

namespace EVE.Components.Client.Keybindings
{
    public static class TriggerSupport
    {
        public static bool DownThisFrame(this RamTrigger trigger) => CustomInput.DownThisFrame((InputTrigger)(Enum)trigger);
        public static bool UpThisFrame(this RamTrigger trigger) => CustomInput.UpThisFrame((InputTrigger)(Enum)trigger);
        public static bool Held(this RamTrigger trigger) => CustomInput.Held((InputTrigger)(Enum)trigger);
    }
}