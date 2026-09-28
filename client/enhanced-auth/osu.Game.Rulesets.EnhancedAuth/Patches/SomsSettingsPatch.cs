#nullable enable
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Graphics;
using osu.Game.Input.Bindings;
using osu.Game.Localisation;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Overlays.Mods.Input;
using osu.Game.Overlays.Mods;
using osu.Game.Overlays.Settings.Sections.Audio;
using osu.Game.Overlays.Settings.Sections.Graphics;
using osu.Game.Overlays.Settings.Sections.Input;
using osu.Game.Overlays.Settings.Sections.UserInterface;
using osu.Game.Overlays.Settings.Sections;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.SkinEditor;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Play;
using osu.Game.Screens.Select;
using osu.Game.Skinning;
using osuTK.Input;
using Realms;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Merged from SomsGameplayOptionsPatch.cs
[HarmonyPatch]
public static class SomsGameplayOptionsPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(Assembly.Load("osu.Game.Rulesets.Osu").GetType("osu.Game.Rulesets.Osu.UI.OsuSettingsSubsection", true), "load");
        yield return AccessTools.Method(typeof(SongSelectSettings), "load");
        yield return AccessTools.Method(typeof(AudioDevicesSettings), "load");
        yield return AccessTools.Method(typeof(osu.Game.Overlays.Settings.Sections.Gameplay.HUDSettings), "load");
    }

    static void Postfix(SettingsSubsection __instance, FillFlowContainer ___FlowContent)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance)) return;
        var prefs = SomsClientPreferences.Instance;
        if (__instance is SongSelectSettings)
        {
            add("ignore-recommended-difficulty", "Ignore recommended difficulty", "Always select the last difficulty when browsing beatmap sets.", prefs.IgnoreRecommendedDifficulty);
            add("map-pp", "Show pp for a map", "FC pp estimates at 95%, 98%, 99%, and 100% accuracy with selected mods.", prefs.ShowMapPP);
        }
        else if (__instance is AudioDevicesSettings)
            add("volume", "Better Volume Changing", "Alt + scroll changes volume from anywhere on screen, including multiplayer and spectating.", prefs.EnhancedVolume);
        else if (__instance is osu.Game.Overlays.Settings.Sections.Gameplay.HUDSettings)
            add("team-leaderboard", "Teamvs in Leaderboards", "Hide team scores along with the player list in Team VS.", prefs.TeamVsInLeaderboards);
        else
        {
            add("smooth-trail", "Force Smooth Cursor Trail", "Force smooth cursor trail on any skin.", prefs.ForceSmoothCursorTrail);
            add("sd-restart", "Sudden death restart on miss", "Automatically restart the map after failing with Sudden Death in singleplayer.", prefs.SuddenDeathRestart);
        }
        void add(string key, string label, string hint, Bindable<bool> preference)
        {
            string name = "soms-gameplay-" + key;
            if (__instance.Children.Any(item => item.Name == name)) return;
            var item = new SettingsItemV2(new FormCheckBox
            {
                Caption = label, HintText = hint, Current = preference.GetBoundCopy(),
            }) { Name = name, Keywords = new[] { "SOMS", key, label } };
            var existing = ___FlowContent.Children.ToArray();
            __instance.Add(item);
            if (__instance is AudioDevicesSettings)
            {
                for (int i = 0; i < existing.Length; i++) ___FlowContent.SetLayoutPosition(existing[i], i * 2);
                ___FlowContent.SetLayoutPosition(item, 1);
            }
            else if (key == "ignore-recommended-difficulty")
            {
                int nativeIndex = Array.FindIndex(existing, child => child is SettingsItemV2 native
                    && native.Control is FormEnumDropdown<RandomSelectAlgorithm>);
                if (nativeIndex >= 0)
                {
                    for (int i = 0; i < existing.Length; i++) ___FlowContent.SetLayoutPosition(existing[i], i * 2);
                    ___FlowContent.SetLayoutPosition(item, nativeIndex * 2 + 1);
                }
            }
        }
    }
}


[HarmonyPatch(typeof(SongSelect), "requestRecommendedSelection")]
public static class SomsIgnoreRecommendedDifficultyPatch
{
    static bool Prefix(SongSelect __instance, IEnumerable<GroupedBeatmap> groupedBeatmaps)
    {
        if (!SomsClientPreferences.Enabled || !SomsClientPreferences.Instance.IgnoreRecommendedDifficulty.Value)
            return true;

        GroupedBeatmap? last = groupedBeatmaps.LastOrDefault();
        if (last == null)
            return true;

        AccessTools.Method(typeof(SongSelect), "queueBeatmapSelection").Invoke(__instance, new object[] { last });
        return false;
    }
}

[HarmonyPatch]
public static class SomsSmoothTrailPatch
{
    static MethodBase TargetMethod() => AccessTools.PropertyGetter(Assembly.Load("osu.Game.Rulesets.Osu").GetType("osu.Game.Rulesets.Osu.Skinning.Legacy.LegacyCursorTrail", true), "DisjointTrail");
    static void Postfix(ref bool __result)
    {
        if (SomsClientPreferences.Enabled && SomsClientPreferences.Instance.ForceSmoothCursorTrail.Value)
            __result = false;
    }
}

[HarmonyPatch(typeof(ModFailCondition), "get_RestartOnFail")]
public static class SomsSuddenDeathRestartPatch
{
    static void Postfix(ModFailCondition __instance, ref bool __result)
    {
        if (SomsClientPreferences.Enabled && __instance is ModSuddenDeath && SomsClientPreferences.Instance.SuddenDeathRestart.Value
            && SomsGameplaySeek.Players.Any(pair => pair.Key is osu.Game.Screens.Play.SoloPlayer
                && pair.Key.IsCurrentScreen() && pair.Key.GameplayState?.Ruleset.ShortName == "osu"))
            __result = true;
    }
}

// Merged from SomsInterfaceScaleSettingsPatch.cs
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
            Caption = "Advanced UI Scaling",
            HintText = "Separate menu and gameplay UI scaling. When disabled, uses the global scale above.",
            Current = preferences.SeparateInterfaceScales.GetBoundCopy(),
        }) { Name = "soms-interface-scale-advanced", Keywords = new[] { "SOMS", "scale" } };
        var menu = slider("menu", "Menu UI scale", preferences.MenuInterfaceScale);
        var gameplay = slider("gameplay", "Gameplay UI scale", preferences.GameplayInterfaceScale);

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
            HintText = "0.1× to 2×. Does not affect gameplay object sizes. Requires Advanced UI Scaling.",
            Current = current.GetBoundCopy(),
            TransferValueOnCommit = true,
            KeyboardStep = 0.01f,
            LabelFormat = value => $"{value:0.##}x",
        })
        {
            Name = "soms-interface-scale-" + key,
            CanBeShown = { BindTarget = preferences.SeparateInterfaceScales },
            Keywords = new[] { "SOMS", "scaling", "scale", "menu", "gameplay" },
        };
    }
}

// Merged from SomsSkinElementSettingsPatch.cs
[HarmonyPatch(typeof(SkinSection), "load")]
public static class SomsSkinElementSettingsPatch
{
    static void Postfix(SkinSection __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance))
            return;

        var preferences = SomsClientPreferences.Instance;
        add("always-random", "Always Random Skin",
            "Always choose a random skin on map start, including restarts.", preferences.AlwaysRandomSkinEnabled);
        if (!__instance.Children.Any(child => child is SomsModSkinSettings))
            __instance.Add(new SomsModSkinSettings());

        if (!__instance.Children.Any(child => child.Name == "soms-skin-sliderendmiss"))
            __instance.Add(new SettingsItemV2(new FormDropdown<SomsSliderMissDisplay>
            {
                Caption = "Slider Judgement",
                HintText = "No — hides sliderendmiss and slidertickmiss. 50/100 — shows hit50/hit100 instead. Default — skin's option.",
                Items = System.Enum.GetValues<SomsSliderMissDisplay>(),
                Current = preferences.SliderMissDisplay.GetBoundCopy(),
            }) { Name = "soms-skin-sliderendmiss", Keywords = new[] { "sliderendmiss", "slidertickmiss", "slider", "miss" } });
        add("followpoints", "Draw Followpoints", "Draw followpoints from the skin.", preferences.DrawFollowPoints);
        add("sliderfollowcircle", "Draw sliderfollowcircle",
            "Draw sliderfollowcircle from the skin.", preferences.ShowSliderFollowCircle);
        add("leaderboard-collapse", "Allow leaderboard collapse",
            "On - skin's option. Off - never collapse leaderboard", preferences.UseSkinLeaderboardCollapse);

        void add(string key, string caption, string hint, Bindable<bool> current)
        {
            string name = "soms-skin-" + key;
            if (__instance.Children.Any(child => child.Name == name))
                return;

            __instance.Add(new SettingsItemV2(new FormCheckBox
            {
                Caption = caption,
                HintText = hint,
                Current = current.GetBoundCopy(),
            })
            {
                Name = name,
                Keywords = new[] { "SOMS", "skin", key, "slider", "board", "leader" },
            });
        }
    }
}

// Merged from SomsSkinHotkeysPatch.cs
// Reserved IDs, intentionally far outside the append-only native GlobalAction enum.
public enum SomsSkinAction
{
    [Description("Skin 1")]
    Slot1 = 0x534f0001,
    [Description("Skin 2")]
    Slot2,
    [Description("Skin 3")]
    Slot3,
    [Description("Skin 4")]
    Slot4,
    [Description("Skin 5")]
    Slot5,

    [Description("Toggle leaderboard collapse")]
    ToggleLeaderboardCollapse = 0x534f0100,
    [Description("Rewind: hold the key and click the timeline (Practice)")]
    GameplaySeek = 0x534f0101,
}

public static class SomsSkinHotkeys
{
    // This sentinel is internal and did not exist in all supported lazer builds.
    private static readonly Guid? randomSkinId = AccessTools.Field(typeof(SkinInfo), "RANDOM_SKIN")?.GetValue(null) as Guid?;

    public static IEnumerable<KeyBinding> Defaults => Enum.GetValues<SomsSkinAction>()
        .Select(action => new KeyBinding(InputKey.None, action));

    public static int SlotIndex(int action) => action - (int)SomsSkinAction.Slot1;
    public static bool IsSkinAction(int action) => SlotIndex(action) is >= 0 and < SomsClientPreferences.SKIN_SLOT_COUNT;
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

// Merged from SomsModHotkeysPatch.cs
internal static partial class SomsModHotkeys
{
    internal const string Scope = "soms-mod-hotkeys";
    // Stable acronym encoding: introducing a new mod never shifts saved IDs.
    internal static int ActionId(string acronym) => acronym.Length is > 0 and <= 3
        ? 0x54000000 | (acronym[0] << 16) | (acronym.Length > 1 ? acronym[1] << 8 : 0) | (acronym.Length > 2 ? acronym[2] : 0)
        : throw new ArgumentException("Unsupported mod acronym", nameof(acronym));
    internal static bool IsAction(int action) => (action & unchecked((int)0xff000000)) == 0x54000000;
    internal static string Acronym(int action) => new(new[] { (char)((action >> 16) & 255), (char)((action >> 8) & 255), (char)(action & 255) }.Where(c => c != 0).ToArray());
    internal static readonly ConditionalWeakTable<ModSelectOverlay, BindingObserver> Observers = new();

    internal partial class BindingObserver : osu.Framework.Graphics.Component
    {
        internal volatile Dictionary<int, Dictionary<int, KeyCombination>> Bindings = new();
        internal Dictionary<int, KeyCombination> ForRuleset(int mode)
        {
            var snapshot = Bindings;
            var result = snapshot.TryGetValue(-1, out var legacy) ? new Dictionary<int, KeyCombination>(legacy) : new();
            if (snapshot.TryGetValue(mode, out var overrides))
                foreach (var pair in overrides) result[pair.Key] = pair.Value;
            return result.Where(pair => pair.Value.Keys.Any(key => key != InputKey.None)).ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        private IDisposable? subscription;
        [BackgroundDependencyLoader]
        private void load(RealmAccess realm)
        {
            subscription = realm.RegisterForNotifications(r => r.All<RealmKeyBinding>().Where(b => b.RulesetName == Scope), (bindings, _) =>
            {
                // Copy values while Realm is valid; no managed records survive the callback.
                // Preserve empty per-mode assignments: clearing one must not revive a legacy binding.
                Bindings = bindings.GroupBy(b => b.Variant ?? -1).ToDictionary(mode => mode.Key,
                    mode => mode.GroupBy(b => b.ActionInt).ToDictionary(group => group.Key, group => group.First().KeyCombination));
            });
        }
        protected override void Dispose(bool isDisposing)
        {
            subscription?.Dispose();
            subscription = null;
            base.Dispose(isDisposing);
        }
    }
}

[HarmonyPatch(typeof(ModSelectOverlay), "load")]
public static class SomsModBindingObserverPatch
{
    static void Postfix(ModSelectOverlay __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsModHotkeys.Observers.TryGetValue(__instance, out _)) return;
        var observer = new SomsModHotkeys.BindingObserver();
        SomsModHotkeys.Observers.Add(__instance, observer);
        AccessTools.Method(typeof(CompositeDrawable), "AddInternal", [typeof(Drawable)]).Invoke(__instance, [observer]);
    }
}

[HarmonyPatch(typeof(ModColumn), "OnKeyDown")]
public static class SomsModKeyPressPatch
{
    static bool Prefix(ModColumn __instance, KeyDownEvent e, IModHotkeyHandler ___hotkeyHandler, ref bool __result)
    {
        if (!SomsClientPreferences.Enabled || e.Repeat) return true;
        ModSelectOverlay? overlay = null;
        for (var parent = __instance.Parent; parent != null; parent = parent.Parent)
            if (parent is ModSelectOverlay found) { overlay = found; break; }
        if (overlay == null || !SomsModHotkeys.Observers.TryGetValue(overlay, out var observer) || observer.Bindings.Count == 0) return true;
        var bindings = observer.ForRuleset(overlay.Ruleset.Value?.OnlineID ?? -1);
        if (bindings.Count == 0) return true;
        __result = Handle(e, overlay.AvailableMods.Value.Values.SelectMany(mods => mods), __instance.AvailableMods, ___hotkeyHandler, bindings);
        return false;
    }

    internal static bool Handle(KeyDownEvent e, IEnumerable<ModState> allMods, IEnumerable<ModState> columnMods,
        IModHotkeyHandler native, IReadOnlyDictionary<int, KeyCombination> bindings)
    {
        if (e.Repeat) return false;
        var pressed = KeyCombination.FromInputState(e.CurrentState);
        bool Assigned(ModState mod) => bindings.ContainsKey(SomsModHotkeys.ActionId(mod.Mod.Acronym));
        foreach (var mod in allMods.Where(mod => mod.Visible))
        {
            if (!bindings.TryGetValue(SomsModHotkeys.ActionId(mod.Mod.Acronym), out var combination)
                || !combination.IsPressed(pressed, e.CurrentState, KeyCombinationMatchingMode.Exact)) continue;
            mod.Active.Toggle();
            return true;
        }
        if (e.ControlPressed || e.AltPressed || e.SuperPressed) return false;
        if (native is SequentialModHotkeyHandler)
        {
            var keys = (Key[])AccessTools.Field(typeof(SequentialModHotkeyHandler), "toggleKeys").GetValue(native)!;
            int index = Array.IndexOf(keys, e.Key);
            var target = index < 0 ? null : columnMods.Where(mod => mod.Visible).ElementAtOrDefault(index);
            if (target == null || Assigned(target)) return false;
            target.Active.Toggle();
            return true;
        }
        // Classic matching is by mod type, so excluding overridden mods keeps
        // native cycles (DT/NC, SD/PF, etc.) intact for the remaining defaults.
        return native.HandleModHotkeyPressed(e, columnMods.Where(mod => !Assigned(mod)));
    }
}

[HarmonyPatch(typeof(KeyBindingPanel), "load")]
public static class SomsModHotkeySettingsPatch
{
    static void Postfix(KeyBindingPanel __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance)) return;
        var sections = (List<SettingsSection>)AccessTools.Field(typeof(SettingsPanel), "loadableSections").GetValue(__instance)!;
        for (int mode = 0; mode < 4; mode++)
            if (!sections.OfType<SomsModHotkeysSection>().Any(section => section.RulesetId == mode))
                AccessTools.Method(typeof(SettingsPanel), "AddSection").Invoke(__instance, [new SomsModHotkeysSection(mode)]);
    }
}

[HarmonyPatch(typeof(RealmKeyBinding), nameof(RealmKeyBinding.GetAction))]
public static class SomsModBindingDescriptionPatch
{
    static bool Prefix(RealmKeyBinding __instance, ref object __result)
    {
        if (__instance.RulesetName != SomsModHotkeys.Scope) return true;
        __result = LocalisableString.Interpolate($"Mod {SomsModHotkeys.Acronym(__instance.ActionInt)}");
        return false;
    }
}

[HarmonyPatch(typeof(KeyBindingRow), "load")]
public static class SomsModBindingCaptionPatch
{
    static void Postfix(KeyBindingRow __instance, FormFieldCaption ___caption)
    {
        if (__instance.Action is int action && SomsModHotkeys.IsAction(action))
            ___caption.Caption = $"{SomsModHotkeys.Acronym(action)} - toggle mod";
    }
}


// Merged from SomsModSkinsPatch.cs
public static class SomsModSkins
{
    // NM, HD, HR, DT/NC, EZ. Classic and unrelated mods do not change the group.
    public static int Category(IEnumerable<string> mods)
    {
        var selected = mods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Contains("EZ")) return selected.Contains("HD") ? 1 : 4;
        if (selected.Contains("DT") || selected.Contains("NC")) return 3;
        if (selected.Contains("HR")) return 2;
        if (selected.Contains("HD")) return 1;
        return 0;
    }

    public static void Apply(SkinManager skins, IReadOnlyList<Mod> mods)
    {
        if (!SomsClientPreferences.Enabled || !SomsClientPreferences.Instance.ModSkinsEnabled.Value || skins.CurrentSkinInfo.Disabled) return;
        Guid target = SomsClientPreferences.Instance.ModSkins[Category(mods.Select(mod => mod.Acronym))].Value;
        if (target == Guid.Empty || target == skins.CurrentSkinInfo.Value.ID) return;
        var skin = skins.GetAllUsableSkins().FirstOrDefault(item => item.ID == target);
        if (skin == null) return;
        if (SomsSkinHotkeys.IsRandomSkin(target)) skins.SelectRandomSkin();
        else skins.CurrentSkinInfo.Value = skin;
    }

    public static void ApplyForPlayer(SkinManager skins, IReadOnlyList<Mod> mods)
    {
        if (SomsClientPreferences.Enabled && SomsClientPreferences.Instance.AlwaysRandomSkinEnabled.Value)
        {
            if (!skins.CurrentSkinInfo.Disabled)
                skins.SelectRandomSkin();
            return;
        }

        Apply(skins, mods);
    }
}

// Observe menu mod changes, coalescing quick toggles into one skin load per frame.
public partial class SomsModSkinController : CompositeDrawable
{
    private IBindable<IReadOnlyList<Mod>> mods = null!;
    private Bindable<bool> enabled = null!;
    private Bindable<Guid>[] slots = Array.Empty<Bindable<Guid>>();
    [Resolved] private SkinManager skins { get; set; } = null!;

    [BackgroundDependencyLoader]
    private void load(IBindable<IReadOnlyList<Mod>> selected)
    {
        AlwaysPresent = true;
        mods = selected.GetBoundCopy();
        mods.BindValueChanged(_ => Scheduler.AddOnce(apply));
        enabled = SomsClientPreferences.Instance.ModSkinsEnabled.GetBoundCopy();
        enabled.BindValueChanged(_ => Scheduler.AddOnce(apply), true);
        slots = SomsClientPreferences.Instance.ModSkins.Select(slot => slot.GetBoundCopy()).ToArray();
        foreach (var slot in slots) slot.BindValueChanged(_ => Scheduler.AddOnce(apply));
    }

    private void apply() { if (!IsDisposed) SomsModSkins.Apply(skins, mods.Value); }

    protected override void Dispose(bool isDisposing)
    {
        mods?.UnbindAll();
        enabled?.UnbindAll();
        foreach (var slot in slots) slot.UnbindAll();
        base.Dispose(isDisposing);
    }
}

[HarmonyPatch(typeof(OsuGame), "LoadComplete")]
public static class SomsModSkinControllerPatch
{
    static void Postfix(OsuGame __instance)
    {
        if (SomsClientPreferences.Enabled) __instance.Add(new SomsModSkinController());
    }
}

// A Player's final mods may differ from menu mods (multiplayer, SOMSAI, retries).
// LoadComplete runs on the update thread, before the first gameplay frame.
[HarmonyPatch(typeof(Player), "LoadComplete")]
public static class SomsModSkinPlayerPatch
{
    static void Prefix(Player __instance)
    {
        if (!SomsClientPreferences.Enabled) return;
        var skins = Traverse.Create(GlobalConfigManager.GameBase).Property("SkinManager").GetValue<SkinManager>();
        if (skins != null) SomsModSkins.ApplyForPlayer(skins, __instance.Mods.Value);
    }
}


[HarmonyPatch(typeof(DebugSection), MethodType.Constructor)]
public static class SomsDebugSettingsPatch
{
    static void Postfix(DebugSection __instance)
    {
        if (!SomsClientPreferences.Enabled)
            return;

        if (!__instance.Children.Any(child => child is SomsMirrorDebugSettings))
            __instance.Add(new SomsMirrorDebugSettings());
        if (!__instance.Children.Any(child => child is SomsSkinDebugSettings))
            __instance.Add(new SomsSkinDebugSettings());
        if (!__instance.Children.Any(child => child is SomsStealthSettings))
            __instance.Add(new SomsStealthSettings());
    }
}

[HarmonyPatch(typeof(BeatmapModelDownloader), "CreateDownloadRequest")]
public static class SomsBeatmapMirrorPatch
{
    static bool Prefix(IBeatmapSetInfo set, bool minimiseDownloadSize, ref ArchiveDownloadRequest<IBeatmapSetInfo> __result)
    {
        if (!SomsClientPreferences.Enabled) return true;
        SomsBeatmapMirror mirror = SomsClientPreferences.Instance.BeatmapMirror.Value;
        if (mirror == SomsBeatmapMirror.Default) return true;
        __result = new MirrorBeatmapDownloadRequest(set, minimiseDownloadSize, mirror);
        return false;
    }

    private sealed class MirrorBeatmapDownloadRequest : DownloadBeatmapSetRequest
    {
        private readonly SomsBeatmapMirror mirror;
        private readonly bool noVideo;
        protected override string Target => $"beatmapsets/{Model.OnlineID}/download?mirror={mirror.ToString().ToLowerInvariant()}{(noVideo ? "&noVideo=1" : "")}";

        public MirrorBeatmapDownloadRequest(IBeatmapSetInfo beatmapSetInfo, bool minimiseDownloadSize, SomsBeatmapMirror mirror)
            : base(beatmapSetInfo, minimiseDownloadSize)
        {
            this.mirror = mirror;
            noVideo = minimiseDownloadSize;
        }
    }
}
