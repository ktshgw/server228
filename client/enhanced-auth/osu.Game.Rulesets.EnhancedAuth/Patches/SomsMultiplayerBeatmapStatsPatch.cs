#nullable enable
using System.Linq;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.OnlinePlay;
using osu.Game.Screens.OnlinePlay.Multiplayer;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

/// <summary>
/// Keeps the freestyle difficulty card in sync with the exact mod combination
/// stored in the multiplayer screen's gameplay state.
/// </summary>
public static class SomsMultiplayerBeatmapStats
{
    private static readonly AccessTools.FieldRef<DrawableRoomPlaylistItem, Mod[]> requiredMods =
        AccessTools.FieldRefAccess<DrawableRoomPlaylistItem, Mod[]>("requiredMods");

    public static MultiplayerMatchSubScreen? FindScreen(DrawableRoomPlaylistItem display)
    {
        for (Drawable? parent = display.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is MultiplayerMatchSubScreen screen)
                return screen;
        }

        return null;
    }

    public static void Apply(DrawableRoomPlaylistItem display)
    {
        MultiplayerMatchSubScreen? screen = FindScreen(display);
        if (screen == null)
            return;

        var container = Traverse.Create(screen)
            .Field("userStyleDisplayContainer")
            .GetValue<Container<DrawableRoomPlaylistItem>>();
        if (container?.SingleOrDefault() != display)
            return;

        // updateGameplayState has already resolved local freestyle mods, their
        // settings, and required room mods into this bindable. Reusing these
        // instances also preserves Difficulty Adjust values.
        requiredMods(display) = screen.Mods.Value.ToArray();
    }

    public static void ApplyStarRating(DrawableRoomPlaylistItem display)
    {
        MultiplayerMatchSubScreen? screen = FindScreen(display);
        var icons = Traverse.Create(display).Field("difficultyIconContainer").GetValue<FillFlowContainer>();
        DifficultyIcon? icon = icons?.Children.OfType<DifficultyIcon>().FirstOrDefault();
        var rulesetInstance = screen?.Ruleset.Value?.CreateInstance();
        var workingBeatmap = screen?.Beatmap.Value;

        if (icon == null || rulesetInstance == null || workingBeatmap == null)
            return;

        try
        {
            var attributes = rulesetInstance.CreateDifficultyCalculator(workingBeatmap).Calculate(requiredMods(display));
            icon.Current.Value = new StarDifficulty(attributes.StarRating, attributes.MaxCombo);
        }
        catch
        {
            // Keep the original rating if a local map revision cannot be calculated.
        }
    }
}

// The first refresh happens only after the drawable has a parent. Patching load()
// is too early because background-loaded drawables have no screen ancestor yet.
[HarmonyPatch(typeof(DrawableRoomPlaylistItem), "refresh")]
public static class SomsMultiplayerBeatmapStatsRefreshPatch
{
    static void Prefix(DrawableRoomPlaylistItem __instance) => SomsMultiplayerBeatmapStats.Apply(__instance);

    static void Postfix(DrawableRoomPlaylistItem __instance) => SomsMultiplayerBeatmapStats.ApplyStarRating(__instance);
}

[HarmonyPatch(typeof(MultiplayerMatchSubScreen), "updateGameplayState")]
public static class SomsMultiplayerBeatmapStatsUpdatePatch
{
    static void Postfix(MultiplayerMatchSubScreen __instance)
    {
        var container = Traverse.Create(__instance)
            .Field("userStyleDisplayContainer")
            .GetValue<Container<DrawableRoomPlaylistItem>>();
        var display = container?.SingleOrDefault();
        if (display == null)
            return;

        SomsMultiplayerBeatmapStats.Apply(display);
        AccessTools.Method(typeof(DrawableRoomPlaylistItem), "refresh").Invoke(display, null);
    }
}
