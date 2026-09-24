#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using HarmonyLib;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Graphics.Containers;
using osu.Game.Input.Bindings;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings.Sections.Input;
using osu.Game.Overlays.SkinEditor;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Skinning;
using Realms;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Reserved IDs, intentionally far outside the append-only native GlobalAction enum.
public enum SomsSkinAction
{
    [Description("Выбрать скин 1")]
    Slot1 = 0x534f0001,
    [Description("Выбрать скин 2")]
    Slot2,
    [Description("Выбрать скин 3")]
    Slot3,
    [Description("Выбрать скин 4")]
    Slot4,
    [Description("Выбрать скин 5")]
    Slot5,

    [Description("Переключить разрешение сворачивания таблицы рекордов")]
    ToggleLeaderboardCollapse = 0x534f0100,
    [Description("Перемотка: удерживать клавишу и нажать на шкалу (тренировка)")]
    GameplaySeek = 0x534f0101,
}

public static class SomsSkinHotkeys
{
    // This sentinel is internal and did not exist in all supported lazer builds.
    private static readonly Guid? randomSkinId = AccessTools.Field(typeof(SkinInfo), "RANDOM_SKIN")?.GetValue(null) as Guid?;

    public static IEnumerable<KeyBinding> Defaults => Enum.GetValues<SomsSkinAction>()
        .Select(action => new KeyBinding(InputKey.None, action));

    public static int SlotIndex(int action) => action - (int)SomsSkinAction.Slot1;
    public static bool IsSkinAction(int action) => SlotIndex(action) is >= 0 and < SomsClientPreferences.SkinSlotCount;
    public static bool IsSomsAction(int action) => IsSkinAction(action) || action is (int)SomsSkinAction.ToggleLeaderboardCollapse or (int)SomsSkinAction.GameplaySeek;
    public static bool IsRandomSkin(Guid id) => randomSkinId.HasValue && randomSkinId.Value == id;
}

[HarmonyPatch(typeof(GlobalActionContainer), nameof(GlobalActionContainer.DefaultKeyBindings), MethodType.Getter)]
public static class SomsSkinDefaultBindingsPatch
{
    static void Postfix(ref IEnumerable<IKeyBinding> __result)
    {
        if (SomsClientPreferences.Enabled)
            __result = __result.Where(binding => !SomsSkinHotkeys.IsSomsAction((int)binding.Action))
                .Concat(SomsSkinHotkeys.Defaults.Select(binding => new KeyBinding(binding.KeyCombination, (GlobalAction)(int)binding.Action)));
    }
}

[HarmonyPatch(typeof(RealmKeyBinding), nameof(RealmKeyBinding.GetAction))]
public static class SomsSkinActionDescriptionPatch
{
    static bool Prefix(RealmKeyBinding __instance, ref object __result)
    {
        if (!SomsClientPreferences.Enabled || !string.IsNullOrEmpty(__instance.RulesetName)
            || !SomsSkinHotkeys.IsSomsAction(__instance.ActionInt))
            return true;

        __result = (SomsSkinAction)__instance.ActionInt;
        return false;
    }
}

[HarmonyPatch(typeof(GlobalKeyBindingsSubsection), "GetKeyBindings")]
public static class SomsSkinNativeBindingConflictsPatch
{
    static void Postfix(Realm realm, ref IEnumerable<RealmKeyBinding> __result)
    {
        if (!SomsClientPreferences.Enabled)
            return;

        // Native subsections only query actions from their own category. Include
        // skin slots in conflict detection when the user edits a native shortcut
        // too; visible rows still come exclusively from that subsection's Defaults.
        int first = (int)SomsSkinAction.Slot1;
        int last = (int)SomsSkinAction.Slot5;
        int collapse = (int)SomsSkinAction.ToggleLeaderboardCollapse;
        int seek = (int)SomsSkinAction.GameplaySeek;
        __result = __result.Concat(realm.All<RealmKeyBinding>().Where(binding => binding.RulesetName == null
            && binding.Variant == null && ((binding.ActionInt >= first && binding.ActionInt <= last) || binding.ActionInt == collapse || binding.ActionInt == seek)))
            .DistinctBy(binding => binding.ID);
    }
}

[HarmonyPatch(typeof(KeyBindingPanel), "load")]
public static class SomsSkinHotkeySettingsPatch
{
    static void Postfix(KeyBindingPanel __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance))
            return;

        var sections = (List<osu.Game.Overlays.Settings.SettingsSection>)AccessTools.Field(typeof(SettingsPanel), "loadableSections").GetValue(__instance)!;
        if (!sections.Any(section => section is SomsSkinHotkeysSection))
            AccessTools.Method(typeof(SettingsPanel), "AddSection").Invoke(__instance, [new SomsSkinHotkeysSection()]);
    }
}

[HarmonyPatch(typeof(OsuGame), nameof(OsuGame.OnPressed), typeof(KeyBindingPressEvent<GlobalAction>))]
public static class SomsSkinSelectionPatch
{
    static bool Prefix(OsuGame __instance, KeyBindingPressEvent<GlobalAction> e, ref bool __result)
    {
        if ((int)e.Action == (int)SomsSkinAction.GameplaySeek) return true;
        if (!SomsClientPreferences.Enabled || !SomsSkinHotkeys.IsSomsAction((int)e.Action))
            return true;

        if ((int)e.Action == (int)SomsSkinAction.ToggleLeaderboardCollapse)
        {
            // Use the same persisted bindable as the skin settings checkbox.
            // Live leaderboards already observe it, including during replays.
            if (!e.Repeat)
            {
                var collapse = SomsClientPreferences.Instance.UseSkinLeaderboardCollapse;
                collapse.Value = !collapse.Value;
            }
            __result = true;
            return false;
        }

        __result = false;
        if (e.Repeat)
            return false;

        var skinEditor = Traverse.Create(__instance).Field("skinEditor").GetValue<SkinEditorOverlay>();
        var skins = Traverse.Create(__instance).Property("SkinManager").GetValue<SkinManager>();
        // Match native PreviousSkin / NextSkin behaviour while editing or mounting a skin.
        if (skins == null || skinEditor?.State.Value == Visibility.Visible || skins.CurrentSkinInfo.Disabled)
            return false;

        Guid skinId = SomsClientPreferences.Instance.SkinSlots[SomsSkinHotkeys.SlotIndex((int)e.Action)].Value;
        var skin = skins.GetAllUsableSkins().FirstOrDefault(candidate => candidate.ID == skinId);
        if (skin != null)
        {
            if (SomsSkinHotkeys.IsRandomSkin(skin.ID))
                skins.SelectRandomSkin();
            else
                skins.CurrentSkinInfo.Value = skin;
            __result = true;
        }
        return false;
    }
}
