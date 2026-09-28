#nullable enable
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

internal static class SomsAiScoreMultiplierContext
{
    internal static bool Active { get; set; }
}

[HarmonyPatch(typeof(ScoreMultiplierCalculator), nameof(ScoreMultiplierCalculator.CalculateFor))]
public static class SomsAiScoreMultiplierPatch
{
    private static void Postfix(IEnumerable<Mod> mods, ref double __result)
    {
        if (SomsAiScoreMultiplierContext.Active && mods.Any(mod => mod.Acronym == "EZ"))
            __result *= 1.2 / 0.8;
    }
}
