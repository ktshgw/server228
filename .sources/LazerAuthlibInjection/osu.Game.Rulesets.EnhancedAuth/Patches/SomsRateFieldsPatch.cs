using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Game.Overlays.Mods;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Rulesets.Mods;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(ModCustomisationSection), MethodType.Constructor, typeof(Mod), typeof(IReadOnlyList<Drawable>))]
public static class SomsRateFieldsPatch
{
    static void Prefix(Mod mod, ref IReadOnlyList<Drawable> settings)
    {
        if (SomsClientPreferences.Enabled && mod is ModDoubleTime speed)
            settings = settings.Append(new SomsRateFields(speed)).ToArray();
    }
}
