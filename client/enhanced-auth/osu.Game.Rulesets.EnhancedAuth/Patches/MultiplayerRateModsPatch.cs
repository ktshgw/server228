using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using osu.Game.Online.API;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.Mods;
using osu.Game.Utils;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// The normal user-mod path serialises settings, refreshes the local clock and
// submits the selected mods. Keep the room's required-mod compatibility checks.
[HarmonyPatch(typeof(ModUtils), nameof(ModUtils.IsValidModForMatch))]
public static class MultiplayerRateModsPatch
{
    static void Postfix(Mod mod, bool required, MatchType matchType, ref bool __result)
    {
        if (!GlobalConfigManager.Patched || GlobalConfigManager.Config.DisableServerExtensions || required)
            return;

        if (matchType is MatchType.HeadToHead or MatchType.TeamVersus
            && mod is ModDoubleTime or ModNightcore
            && mod.UserPlayable && mod.HasImplementation)
            __result = true;
    }
}

[HarmonyPatch(typeof(ModUtils), nameof(ModUtils.EnumerateUserSelectableFreeMods))]
public static class MultiplayerSelectableRateModsPatch
{
    static void Postfix(MatchType matchType, IEnumerable<APIMod> requiredMods, IEnumerable<APIMod> allowedMods,
                        bool freestyle, Ruleset userRuleset, ref Mod[] __result)
    {
        if (!GlobalConfigManager.Patched || GlobalConfigManager.Config.DisableServerExtensions
            || matchType is not (MatchType.HeadToHead or MatchType.TeamVersus)
            || (!freestyle && !allowedMods.Any()))
            return;

        Mod[] required = requiredMods.Select(m => m.ToMod(userRuleset)).ToArray();
        __result = __result.Concat(userRuleset.AllMods.OfType<Mod>()
            .Where(m => m is ModDoubleTime or ModNightcore)
            .Where(m => ModUtils.CheckCompatibleSet(required.Append(m))))
            .DistinctBy(m => m.GetType()).ToArray();
    }
}
