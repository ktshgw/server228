using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.Mods;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Keep lazer's ranked indicator consistent with SOMS!'s server PP policy.
// DA inherits Mod.Ranked, so its shared getter must only affect DA instances.
[HarmonyPatch]
public static class RankedModsPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertyGetter(typeof(ModDoubleTime), nameof(Mod.Ranked));
        yield return AccessTools.PropertyGetter(typeof(ModNightcore), nameof(Mod.Ranked));
        yield return AccessTools.PropertyGetter(typeof(ModClassic), nameof(Mod.Ranked));
        yield return AccessTools.PropertyGetter(typeof(Mod), nameof(Mod.Ranked));
    }

    static void Postfix(Mod __instance, ref bool __result)
    {
        if (!GlobalConfigManager.Patched || GlobalConfigManager.Config.NonG0V0Server)
            return;

        if (__instance is ModDifficultyAdjust)
            __result = false;
        else if (__instance is ModDoubleTime or ModNightcore
            || __instance is ModClassic && __instance.GetType().FullName == "osu.Game.Rulesets.Osu.Mods.OsuModClassic")
            __result = true;
    }
}
