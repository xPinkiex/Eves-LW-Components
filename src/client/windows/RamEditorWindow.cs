using System;
using System.Collections.Generic;
using EVE.Components.Client.Keybindings;
using EVE.Components.Shared.Packets.C2S;
using EccsGuiBuilder.Client.Layouts.Elements;
using EccsGuiBuilder.Client.Layouts.Helper;
using EccsGuiBuilder.Client.Wrappers;
using EccsGuiBuilder.Client.Wrappers.AutoAssign;
using EccsLogicWorldAPI.Client.AccessHelpers;
using FancyInput;
using LogicAPI.Data;
using LogicInitializable;
using LogicUI;
using LogicUI.MenuParts;
using LogicUI.MenuTypes;
using LogicUI.MenuTypes.ConfigurableMenus;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using LogicWorld.Interfaces;

namespace EVE.Components.Client.Windows
{
    public class RamEditorWindow : ToggleableSingletonMenu<RamEditorWindow>, IAssignMyFields
    {
        private static ComponentAddress _targetAddress;
        private static RamEditorWindow _instance;
        private static Dictionary<ComponentAddress, string> _textCache = new Dictionary<ComponentAddress, string>();

        public static void initOnce()
        {
            OnMenuShown += OverlayActions.getOverlayShownAction();
            OnMenuHidden += OverlayActions.getOverlayHidingAction();
        }

        public static void init()
        {
            var window = WS.window("ROM Editor")
                .setLocalizedTitle("EVE_Components.RamEditorTitle")
                .setDefaultSize(960, 720)
                .setResizeable(true)
                .setMinSize(480, 360);

            var content = window.contentWrapper
                .layoutGrowElement(spacing: 10, padding: new RectOffset(20, 20, 20, 20), expandChildThickness: true);

            content.add(WS.inputArea
                .setPlaceholderLocalizationKey("EVE_Components.Placeholder")
                .fixTextAreaColor()
                .configure(tmp => {
                    tmp.characterLimit = 0;               // 0 = unlimited input length
                    tmp.lineLimit = 0;                    // 0 = unlimited lines
                })
                .addAndConfigure<LayoutElement>(le => {
                    le.minHeight = 200;
                    le.preferredHeight = 200;
                    le.flexibleHeight = 1;
                })
                .injectionKey(nameof(inputField))
            );

            content.add(WS.textLine
                .removeLocalization()
                .injectionKey(nameof(statusLabel))
                .configureTMP(tmp => {
                    tmp.fontSize = 30;
                    tmp.horizontalAlignment = HorizontalAlignmentOptions.Left;
                })
            );

            content.addContainer("ButtonRow")
                .layoutHorizontalInner(spacing: 10, expandChildThickness: true)
                .add(WS.button
                    .setLocalizationKey("EVE_Components.Apply")
                    .add<ButtonLayout>()
                    .injectionKey(nameof(applyButton))
                )
                .add(WS.button
                    .setLocalizationKey("EVE_Components.Clear")
                    .add<ButtonLayout>()
                    .injectionKey(nameof(clearButton))
                )
                .add(WS.button
                    .setLocalizationKey("EVE_Components.Save")
                    .add<ButtonLayout>()
                    .injectionKey(nameof(saveButton))
                )
                .add(WS.button
                    .setLocalizationKey("EVE_Components.Restore")
                    .add<ButtonLayout>()
                    .injectionKey(nameof(restoreButton))
                );

            window.add<RamEditorWindow>()
                .addOnCloseAction(HideMenu)
                .add<CloseWindowOnKey>()
                .build();
        }

        public static void openFor(ComponentAddress address)
        {
            _targetAddress = address;
            SetMenuVisible(true);
            _instance?.UpdateTitle(address);
            if (_textCache.TryGetValue(address, out string cached))
            {
                _instance?.SetInputText(cached);
            }
            else
            {
                _instance?.PreFillFromRom(address);
            }
        }

        private void SetInputText(string text)
        {
            if (inputField != null)
            {
                inputField.text = text;
            }
        }

        private void UpdateTitle(ComponentAddress address)
        {
            try
            {
                var component = Instances.MainWorld.Data.Lookup(address);
                if (component == null) return;
                ComponentType microType = Instances.MainWorld.ComponentTypes.GetComponentType("EVE_Components.MicrocodeROM");
                string key = component.Data.Type == microType
                    ? "EVE_Components.MicrocodeEditorTitle"
                    : "EVE_Components.RamEditorTitle";
                var utilities = GetComponent<ConfigurableMenuUtility>();
                if (utilities != null && utilities.TitleLocalizor != null)
                {
                    utilities.TitleLocalizor.SetLocalizationKey(key);
                }
            }
            catch
            {
            }
        }

        private void PreFillFromRom(ComponentAddress address)
        {
            if (inputField == null) return;
            try
            {
                var component = Instances.MainWorld.Data.Lookup(address);
                if (component == null) return;
                byte[] data = component.Data.CustomData;
                if (data == null || data.Length == 0)
                {
                    inputField.text = "";
                    return;
                }
                var sb = new System.Text.StringBuilder(data.Length * 5);
                for (int i = 0; i < data.Length; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append("0x");
                    sb.Append(data[i].ToString("X2"));
                }
                inputField.text = sb.ToString();
            }
            catch
            {
            }
        }

        [AssignMe]
        public TMP_InputField inputField;

        [AssignMe]
        public TextMeshProUGUI statusLabel;

        [AssignMe]
        public HoverButton applyButton;

        [AssignMe]
        public HoverButton clearButton;

        [AssignMe]
        public HoverButton saveButton;

        [AssignMe]
        public HoverButton restoreButton;

        public override void Initialize()
        {
            base.Initialize();
            _instance = this;
            applyButton.OnClickEnd -= OnApply;
            applyButton.OnClickEnd += OnApply;
            clearButton.OnClickEnd -= OnClear;
            clearButton.OnClickEnd += OnClear;
            saveButton.OnClickEnd -= OnSave;
            saveButton.OnClickEnd += OnSave;
            restoreButton.OnClickEnd -= OnRestore;
            restoreButton.OnClickEnd += OnRestore;
        }

        private void OnApply()
        {
            if (inputField == null) return;

            string text = inputField.text;
            var result = RamParser.Parse(text);

            if (!result.Success)
            {
                if (statusLabel != null)
                {
                    statusLabel.text = $"Error: {result.Error}";
                }
                return;
            }

            _textCache[_targetAddress] = text;

            if (statusLabel != null)
            {
                statusLabel.text = $"Bytes: {result.ByteCount}";
            }

            Instances.SendData.Send(new RamUpdatePacket()
            {
                ComponentAddress = _targetAddress,
                RomData = result.Data,
            });
        }

        private void OnClear()
        {
            if (inputField != null)
            {
                inputField.text = "";
            }
            if (statusLabel != null)
            {
                statusLabel.text = "";
            }

            _textCache.Remove(_targetAddress);

            Instances.SendData.Send(new RamUpdatePacket()
            {
                ComponentAddress = _targetAddress,
                RomData = new byte[0],
            });
        }

        private void OnSave()
        {
            if (statusLabel != null)
            {
                statusLabel.text = "Saved to world data";
            }

            Instances.SendData.Send(new RomActionPacket()
            {
                ComponentAddress = _targetAddress,
                Action = RomActionType.Save,
            });
        }

        private void OnRestore()
        {
            if (statusLabel != null)
            {
                statusLabel.text = "Restored from original";
            }

            _textCache.Remove(_targetAddress);

            Instances.SendData.Send(new RomActionPacket()
            {
                ComponentAddress = _targetAddress,
                Action = RomActionType.Restore,
            });

            PreFillFromRom(_targetAddress);
        }
    }

    public class CloseWindowOnKey : MonoBehaviour
    {
        private void Update()
        {
            if (UITrigger.Back.DownThisFrame())
            {
                RamEditorWindow.HideMenu();
            }
        }
    }
}