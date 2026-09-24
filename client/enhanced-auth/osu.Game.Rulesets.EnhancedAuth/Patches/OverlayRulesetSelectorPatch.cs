using System.Linq;
using HarmonyLib;
using osu.Game.Extensions;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Extensions;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(RulesetSelector), "load")]
public class OverlayRulesetSelectorPatch
{
    private static void addRulesets(RulesetSelector rulesetSelector)
    {
        var rulesets = Traverse.Create(rulesetSelector).Property("Rulesets").GetValue<RulesetStore>();

        foreach (var ruleset in rulesets.AvailableRulesets.Where(r => r.IsLegacyRuleset()).OrderBy(r => r.OnlineID))
        {
            rulesetSelector.AddItemIfNonExist(ruleset);
        }
        if (rulesetSelector is not (OverlayRulesetSelector or osu.Game.Overlays.Toolbar.ToolbarRulesetSelector)) return;
        var standard = rulesets.AvailableRulesets.First(r => r.ShortName == RulesetInfoExtension.OSU_MODE_SHORTNAME);
        rulesetSelector.AddItemIfNonExist(standard.CreateSpecialRuleset(RulesetInfoExtension.OSU_RELAX_MODE_SHORTNAME, RulesetInfoExtension.OSU_RELAX_ONLINE_ID));
        rulesetSelector.AddItemIfNonExist(standard.CreateSpecialRuleset(RulesetInfoExtension.OSU_AUTOPILOT_MODE_SHORTNAME, RulesetInfoExtension.OSU_AUTOPILOT_ONLINE_ID));
    }

    static bool Prefix(RulesetSelector __instance)
    {
        if (!GlobalConfigManager.Patched || GlobalConfigManager.Config.DisableServerExtensions)
        {
            return true;
        }

        addRulesets(__instance);
        return false;
    }
}
