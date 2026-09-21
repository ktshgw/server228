#nullable enable
using HarmonyLib;
using System.Reflection;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Screens.OnlinePlay.Multiplayer;
using osu.Game.Screens.Play;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

/// <summary>Keeps the multiplayer Team VS score bar in sync with the native gameplay leaderboard toggle.</summary>
[HarmonyPatch(typeof(CompositeDrawable), nameof(CompositeDrawable.UpdateSubTree))]
public static class SomsTeamLeaderboardPatch
{
    private static readonly PropertyInfo bindingsProperty = AccessTools.Property(typeof(Bindable<long>), "Bindings");

    static void Prefix(CompositeDrawable __instance)
    {
        // Run before the framework checks IsPresent. UpdateAfterChildren stops
        // running at Alpha = 0, preventing a hidden bar from returning on Tab.
        if (__instance is not GameplayMatchScoreDisplay display || !SomsClientPreferences.Enabled)
            return;

        // MultiplayerPlayer binds both counters only after detecting team
        // scores. Inspect the bindings themselves, so Team VS is recognised
        // even when gameplay starts with the leaderboard or HUD already hidden.
        bool hasTeamScores = isBound(display.Team1Score) && isBound(display.Team2Score);
        bool? leaderboardVisible = findLeaderboardVisibility(display);

        display.Alpha = ResolveAlpha(
            SomsClientPreferences.Instance.TeamVsInLeaderboards.Value,
            hasTeamScores,
            display.Alpha,
            leaderboardVisible);
    }

    private static bool? findLeaderboardVisibility(GameplayMatchScoreDisplay display)
    {
        var hud = display.FindClosestParent<HUDOverlay>();
        if (hud == null)
            return null;

        // This is the exact binding changed by ToggleInGameLeaderboard.
        return Traverse.Create(hud).Field("configLeaderboardVisibility").GetValue<Bindable<bool>>()?.Value;
    }

    private static bool isBound(BindableLong score) => bindingsProperty.GetValue(score) != null;

    internal static float ResolveAlpha(bool followLeaderboard, bool hasTeamScores, float currentAlpha, bool? leaderboardVisible)
    {
        if (!hasTeamScores)
            return currentAlpha;
        if (!followLeaderboard)
            return 1;

        return leaderboardVisible.HasValue ? leaderboardVisible.Value ? 1 : 0 : currentAlpha;
    }
}
