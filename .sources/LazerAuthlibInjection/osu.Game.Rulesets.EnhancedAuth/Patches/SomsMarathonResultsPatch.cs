#nullable enable
using HarmonyLib;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.EnhancedAuth.Beatmaps;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Native results load timing graphs through BeatmapManager. The temporary compilation
// deliberately has no Realm entry and is kept alive until its results screen is closed.
[HarmonyPatch(typeof(BeatmapManager), nameof(BeatmapManager.GetWorkingBeatmap), typeof(BeatmapInfo), typeof(bool))]
public static class SomsMarathonResultsPatch
{
    static bool Prefix(BeatmapInfo? beatmapInfo, ref WorkingBeatmap __result)
    {
        if (SomsMarathonWorkingBeatmap.Active is not { } active || beatmapInfo?.MD5Hash != active.BeatmapInfo.MD5Hash) return true;
        __result = active;
        return false;
    }
}
