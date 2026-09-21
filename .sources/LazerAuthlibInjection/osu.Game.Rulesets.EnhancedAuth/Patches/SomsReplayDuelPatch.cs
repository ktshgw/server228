#nullable enable
using System.Linq;
using System.Reflection;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Screens;
using osu.Game.Graphics.UserInterface;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Screens.Select;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Extend the score's native menu; right-click itself keeps its normal behaviour.
[HarmonyPatch]
public static class SomsReplayDuelPatch
{
    static MethodBase TargetMethod() => typeof(BeatmapLeaderboardScore).GetInterfaceMap(typeof(IHasContextMenu)).TargetMethods
        .Single(m => m.Name.EndsWith("get_ContextMenuItems"));

    public static bool Enabled => SomsClientPreferences.Enabled && !SomsClientPreferences.Instance.LegacyInterface.Value;

    static void Postfix(BeatmapLeaderboardScore __instance, ref MenuItem[] __result)
    {
        if (!Enabled || __instance.FindClosestParent<SoloSongSelect>() is not { } select || !select.IsCurrentScreen()) return;
        var score = __instance.Score.DeepClone();
        __result = __result.Append(new OsuMenuItem("Сыграть 1 на 1 с реплеем", MenuItemType.Highlighted, () =>
        {
            // Menu actions may outlive their screen or the interface setting.
            if (Enabled && select.IsCurrentScreen()) select.Push(new SomsReplayDuelScreen(score));
        })).ToArray();
    }
}

// Keep both live scores readable during the duel without changing the skin's
// saved collapse preference or affecting ordinary solo gameplay.
[HarmonyPatch(typeof(DrawableGameplayLeaderboard), "updateState")]
public static class SomsReplayDuelLeaderboardPatch
{
    private static readonly PropertyInfo player = AccessTools.Property(typeof(DrawableGameplayLeaderboard), "player");
    static void Postfix(DrawableGameplayLeaderboard __instance, Bindable<bool> ___expanded)
    {
        if (player.GetValue(__instance) is SomsReplayDuelPlayer) ___expanded.Value = true;
    }
}
