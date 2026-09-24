#nullable enable
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Overlays.Mods;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Screens.Play;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch]
public static class SomsLegacyOverlayPatch
{
    private static readonly ConditionalWeakTable<CompositeDrawable, object> attached = new();
    private static readonly MethodInfo addInternal = AccessTools.Method(typeof(CompositeDrawable), "AddInternal");

    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(GameplayMenuOverlay), "LoadComplete");
        yield return AccessTools.DeclaredMethod(typeof(ModSelectOverlay), "LoadComplete");
    }

    static void Postfix(CompositeDrawable __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance)) return;
        Drawable? layer = __instance switch
        {
            GameplayMenuOverlay gameplay => new SomsLegacyGameplayMenu(gameplay),
            ModSelectOverlay mods => new SomsLegacyMods(mods),
            _ => null,
        };
        if (layer == null) return;

        lock (attached)
        {
            if (attached.TryGetValue(__instance, out _))
            {
                layer.Dispose();
                return;
            }
            attached.Add(__instance, new object());
            layer.Depth = -1000;
            try { addInternal.Invoke(__instance, new object[] { layer }); }
            catch { attached.Remove(__instance); layer.Dispose(); throw; }
        }
    }
}
