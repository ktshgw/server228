#nullable enable
using HarmonyLib;
using osu.Game.Online.Spectator;
using osu.Game.Rulesets.EnhancedAuth.Configuration;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Score headers without a timestamp cannot be put on the native spectator timeline.
// Ignore an empty packet before its scheduled First() call; valid packets follow
// the native path unchanged. The server also supplies heartbeat frames during gaps.
[HarmonyPatch(typeof(SpectatorScoreProcessor), "onNewFrames")]
public static class SomsSpectatorFramesPatch
{
    static bool Prefix(FrameDataBundle bundle) => !SomsClientPreferences.Enabled || bundle.Frames.Count > 0;
}
