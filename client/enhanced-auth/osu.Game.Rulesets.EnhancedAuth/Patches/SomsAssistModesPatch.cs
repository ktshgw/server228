#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.Leaderboards;
using osu.Game.Overlays;
using osu.Game.Overlays.Toolbar;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Extensions;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Select;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Users;
using osuTK;
using osuTK.Input;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// RX/AP are leaderboard partitions of osu!, never new conversion/gameplay engines.
public static class SomsAssistModes
{
    public static readonly Bindable<RulesetInfo> Presented = new();
    private static Bindable<RulesetInfo>? actual;
    private static Bindable<IReadOnlyList<Mod>>? mods;
    private static bool syncing;

    public static bool IsAssist(IRulesetInfo? mode) => mode?.ShortName is "osurx" or "osuap";
    public static RulesetInfo ModeFor(RulesetInfo mode, IEnumerable<Mod> selected)
    {
        if (mode.ShortName != "osu") return mode;
        var acronyms = selected.Select(m => m.Acronym).ToHashSet();
        if (acronyms.Contains("AP")) return mode.CreateSpecialRuleset("osuap", 5);
        if (acronyms.Contains("RX")) return mode.CreateSpecialRuleset("osurx", 4);
        return mode;
    }

    public static void Ensure()
    {
        if (!SomsClientPreferences.Enabled || GlobalConfigManager.GameBase == null) return;
        var game = Traverse.Create(GlobalConfigManager.GameBase);
        var source = game.Field("Ruleset").GetValue<Bindable<RulesetInfo>>();
        if (source == null || ReferenceEquals(source, actual)) return;
        if (actual != null) { actual.ValueChanged -= changed; actual.DisabledChanged -= disabled; }
        if (mods != null) { mods.ValueChanged -= modsChanged; mods.DisabledChanged -= disabled; }
        Presented.ValueChanged -= selected;
        actual = source;
        mods = game.Field("SelectedMods").GetValue<Bindable<IReadOnlyList<Mod>>>();
        actual.ValueChanged += changed;
        actual.DisabledChanged += disabled;
        mods.ValueChanged += modsChanged;
        mods.DisabledChanged += disabled;
        sync();
        Presented.ValueChanged += selected;
    }

    private static void changed(ValueChangedEvent<RulesetInfo> _) => sync();
    private static void modsChanged(ValueChangedEvent<IReadOnlyList<Mod>> _) => sync();
    private static void disabled(bool _) => sync();
    private static void sync()
    {
        if (syncing || actual?.Value == null || mods == null) return;
        syncing = true;
        try
        {
            Presented.Disabled = false;
            Presented.Value = ModeFor(actual.Value, mods.Value);
            Presented.Disabled = actual.Disabled || mods.Disabled;
        }
        finally { syncing = false; }
    }

    private static void selected(ValueChangedEvent<RulesetInfo> e)
    {
        if (syncing || actual == null || mods == null || actual.Disabled || mods.Disabled) return;
        syncing = true;
        try
        {
            RulesetInfo target = IsAssist(e.NewValue) ? e.NewValue.CreateNormalRuleset() : e.NewValue;
            actual.Value = target;
            if (target.ShortName == "osu")
            {
                var selectedMods = mods.Value.Where(m => m.Acronym is not ("RX" or "AP")).ToList();
                if (IsAssist(e.NewValue))
                {
                    var assist = new APIMod { Acronym = e.NewValue.ShortName == "osurx" ? "RX" : "AP" }.ToMod(target.CreateInstance());
                    selectedMods.RemoveAll(m => m.IncompatibleMods.Any(t => t.IsInstanceOfType(assist)) || assist.IncompatibleMods.Any(t => t.IsInstanceOfType(m)));
                    selectedMods.Add(assist);
                }
                mods.Value = selectedMods;
            }
        }
        finally { syncing = false; sync(); }
    }

    public static RulesetInfo ForCurrent(RulesetInfo mode)
    {
        Ensure();
        return mode.ShortName == "osu" && actual?.Value?.ShortName == "osu" && mods != null ? ModeFor(mode, mods.Value) : mode;
    }
}

[HarmonyPatch(typeof(Toolbar), "LoadComplete")]
public static class SomsAssistToolbarPatch
{
    static void Postfix(Toolbar __instance)
    {
        if (!SomsClientPreferences.Enabled) return;
        SomsAssistModes.Ensure();
        var selector = Traverse.Create(__instance).Field("rulesetSelector").GetValue<ToolbarRulesetSelector>();
        var actual = Traverse.Create(__instance).Property("ruleset").GetValue<Bindable<RulesetInfo>>();
        selector.Current.UnbindFrom(actual);
        selector.Current.BindTo(SomsAssistModes.Presented);
    }
}

[HarmonyPatch(typeof(ToolbarRulesetSelector), "playRulesetSelectionSample")]
public static class SomsAssistSamplePatch
{
    static bool Prefix(ToolbarRulesetSelector __instance, ValueChangedEvent<RulesetInfo> r)
    {
        if (!SomsClientPreferences.Enabled || !SomsAssistModes.IsAssist(r.NewValue)) return true;
        if (r.OldValue != null) Traverse.Create(__instance).Field("defaultSelectSample").GetValue<Sample>()?.Play();
        return false;
    }
}

[HarmonyPatch(typeof(ToolbarRulesetSelector), "OnKeyDown")]
public static class SomsAssistHotkeyPatch
{
    static bool Prefix(ToolbarRulesetSelector __instance, KeyDownEvent e, ref bool __result)
    {
        if (!SomsClientPreferences.Enabled || !e.ControlPressed || e.Repeat || e.Key < Key.Number1 || e.Key > Key.Number9)
            return true;

        // The installed ruleset list also includes the hidden auth module. Shortcuts
        // must index the displayed tabs, just as a mouse click does (5 = RX, 6 = AP).
        __result = true;
        if (__instance.Current.Disabled) return false;
        var requested = __instance.Items.ElementAtOrDefault(e.Key - Key.Number1);
        if (requested != null) __instance.SelectItem(requested);
        return false;
    }
}

[HarmonyPatch(typeof(ToolbarRulesetTabButton), MethodType.Constructor, typeof(RulesetInfo))]
public static class SomsAssistIconPatch
{
    static void Postfix(ToolbarRulesetTabButton __instance, RulesetInfo value)
    {
        if (!SomsClientPreferences.Enabled || !SomsAssistModes.IsAssist(value)) return;
        var button = Traverse.Create(__instance).Field("ruleset").GetValue<ToolbarButton>();
        button.TooltipMain = value.ShortName == "osurx" ? "osu! Relax" : "osu! Autopilot";
        button.TooltipSub = "Отдельный рейтинг SOMS!";
        button.SetIcon(CreateIcon(value));
    }

    internal static Drawable CreateIcon(RulesetInfo value) => new Container
    {
        Size = new Vector2(30),
        Children = new Drawable[]
        {
            new Container
            {
                RelativeSizeAxes = Axes.Both, Masking = true, CornerRadius = 15, BorderThickness = 2, BorderColour = osuTK.Graphics.Color4.White,
                Child = new osu.Framework.Graphics.Shapes.Box { RelativeSizeAxes = Axes.Both, Alpha = .001f, AlwaysPresent = true },
            },
            new OsuSpriteText { Text = value.ShortName == "osurx" ? "RX" : "AP", Font = OsuFont.GetFont(size: 18, weight: FontWeight.Bold), Anchor = Anchor.Centre, Origin = Anchor.Centre },
        },
    };
}

[HarmonyPatch(typeof(LeaderboardManager), nameof(LeaderboardManager.FetchWithCriteria))]
public static class SomsAssistScoresPatch
{
    static void Prefix(ref LeaderboardCriteria newCriteria)
    {
        if (SomsClientPreferences.Enabled && newCriteria.Ruleset is { } info && newCriteria.Scope != BeatmapLeaderboardScope.Local)
            newCriteria = newCriteria with { Ruleset = SomsAssistModes.ForCurrent(info) };
    }
}

[HarmonyPatch(typeof(LeaderboardManager), "localScoresChanged")]
public static class SomsAssistLocalScoresPatch
{
    static void Postfix(LeaderboardManager __instance)
    {
        if (!SomsClientPreferences.Enabled || __instance.CurrentCriteria?.Ruleset is not { ShortName: "osu" } info) return;
        string mode = SomsAssistModes.ForCurrent(info).ShortName;
        var scores = Traverse.Create(__instance).Field("scores").GetValue<Bindable<LeaderboardScores?>>();
        if (scores.Value == null) return;
        var filtered = scores.Value.TopScores.Where(s => SomsAssistModes.ModeFor(info, s.Mods).ShortName == mode).ToArray();
        scores.Value = LeaderboardScores.Success(filtered, filtered.Length, filtered.Length, null);
    }
}

[HarmonyPatch(typeof(OverlayRulesetTabItem), MethodType.Constructor, typeof(RulesetInfo))]
public static class SomsAssistOverlayIconPatch
{
    static void Postfix(OverlayRulesetTabItem __instance, RulesetInfo value)
    {
        if (!SomsClientPreferences.Enabled || !SomsAssistModes.IsAssist(value)) return;
        var content = SomsLegacyInterfacePatch.Children(__instance).OfType<FillFlowContainer>().Single();
        // Replace the icon inside the existing centred container. Adding a top-left sibling
        // to this horizontal flow throws when the profile/leaderboard overlay is laid out.
        content.Children.OfType<osu.Game.Graphics.Containers.ConstrainedIconContainer>().Single().Icon = SomsAssistIconPatch.CreateIcon(value);
    }
}

[HarmonyPatch(typeof(UserProfileOverlay), nameof(UserProfileOverlay.ShowUser))]
public static class SomsAssistProfilePatch
{
    static void Prefix(ref IRulesetInfo? userRuleset)
    {
        if (!SomsClientPreferences.Enabled || userRuleset != null) return;
        SomsAssistModes.Ensure();
        userRuleset = SomsAssistModes.Presented.Value;
    }
}

[HarmonyPatch(typeof(BeatmapLeaderboardWedge), "refetchScoresFromMods")]
public static class SomsAssistRefreshPatch
{
    static bool Prefix(BeatmapLeaderboardWedge __instance)
    {
        if (!SomsClientPreferences.Enabled) return true;
        __instance.RefetchScores();
        return false;
    }
}

[HarmonyPatch(typeof(UserRankPanel), "LoadComplete")]
public static class SomsAssistUserPanelPatch
{
    static void Prefix(UserRankPanel __instance)
    {
        if (!SomsClientPreferences.Enabled) return;
        SomsAssistModes.Ensure();
        Traverse.Create(__instance).Property("ruleset").SetValue(SomsAssistModes.Presented.GetBoundCopy());
    }
}

// Ranked/score multipliers are independent of native performance calculations.
[HarmonyPatch]
public static class SomsAssistRankedPatch
{
    static IEnumerable<MethodBase> TargetMethods() => new[] { "OsuModRelax", "OsuModAutopilot" }
        .Select(name => Assembly.Load("osu.Game.Rulesets.Osu").GetType("osu.Game.Rulesets.Osu.Mods." + name))
        .Select(type => AccessTools.PropertyGetter(type, "Ranked")).Distinct();
    static void Postfix(Mod __instance, ref bool __result)
    {
        if (SomsClientPreferences.Enabled && __instance.GetType().FullName is "osu.Game.Rulesets.Osu.Mods.OsuModRelax" or "osu.Game.Rulesets.Osu.Mods.OsuModAutopilot") __result = true;
    }
}
