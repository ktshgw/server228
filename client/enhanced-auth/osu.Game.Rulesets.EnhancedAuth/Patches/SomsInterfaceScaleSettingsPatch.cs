#nullable enable
using System.Linq;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Localisation;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections.Graphics;
using osu.Game.Rulesets.EnhancedAuth.Configuration;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(LayoutSettings), "load")]
public static class SomsInterfaceScaleSettingsPatch
{
    static void Postfix(LayoutSettings __instance, FillFlowContainer ___FlowContent)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance)
            || __instance.Children.Any(child => child.Name == "soms-interface-scale-advanced"))
            return;

        var native = __instance.Children.OfType<SettingsItemV2>().FirstOrDefault(item =>
            item.Control is FormSliderBar<float> slider && slider.Caption.Equals(GraphicsSettingsStrings.UIScaling));
        if (native == null)
            return;

        var preferences = SomsClientPreferences.Instance;
        var advanced = new SettingsItemV2(new FormCheckBox
        {
            Caption = "Расширенное масштабирование интерфейса",
            HintText = "Отдельный масштаб меню и игрового интерфейса. При выключении используется общий масштаб выше.",
            Current = preferences.SeparateInterfaceScales.GetBoundCopy(),
        }) { Name = "soms-interface-scale-advanced", Keywords = new[] { "SOMS", "масштаб", "scale" } };
        var menu = slider("menu", "Масштаб интерфейса в меню", preferences.MenuInterfaceScale);
        var gameplay = slider("gameplay", "Масштаб интерфейса в игре", preferences.GameplayInterfaceScale);

        // FlowContent owns the actual layout; the outer SettingsSubsection only
        // forwards Add/Children. Assign positions without detaching native items.
        var existing = ___FlowContent.Children.ToArray();
        for (int i = 0; i < existing.Length; i++)
            ___FlowContent.SetLayoutPosition(existing[i], i * 4);
        float position = ___FlowContent.GetLayoutPosition(native);
        foreach (var item in new[] { advanced, menu, gameplay })
        {
            __instance.Add(item);
            ___FlowContent.SetLayoutPosition(item, ++position);
        }

        SettingsItemV2 slider(string key, string caption, BindableFloat current) => new(new FormSliderBar<float>
        {
            Caption = caption,
            HintText = "От 0,1× до 2×. Не меняет размер игровых объектов. Работает при включённом расширенном масштабировании.",
            Current = current.GetBoundCopy(),
            TransferValueOnCommit = true,
            KeyboardStep = 0.01f,
            LabelFormat = value => $"{value:0.##}x",
        })
        {
            Name = "soms-interface-scale-" + key,
            CanBeShown = { BindTarget = preferences.SeparateInterfaceScales },
            Keywords = new[] { "SOMS", "масштаб", "scale", "меню", "игра" },
        };
    }
}
