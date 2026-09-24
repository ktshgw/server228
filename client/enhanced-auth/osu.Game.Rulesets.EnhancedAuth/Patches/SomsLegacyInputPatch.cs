using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Framework.Input;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

/// <summary>Hidden native backends may update, but must never intercept input or retain text focus.</summary>
[HarmonyPatch]
public static class SomsLegacyInputPatch
{
    private sealed class Counter { public int Value; public Counter() { } }
    private static readonly ConditionalWeakTable<Drawable, Counter> blocked = new();

    public static void Block(Drawable drawable) => blocked.GetOrCreateValue(drawable).Value++;
    public static void Restore(Drawable drawable)
    {
        if (blocked.TryGetValue(drawable, out var counter) && --counter.Value <= 0) blocked.Remove(drawable);
    }

    public static bool IsBlocked(Drawable drawable)
    {
        for (Drawable current = drawable; current != null; current = current.Parent)
            if (blocked.TryGetValue(current, out var counter) && counter.Value > 0) return true;
        return false;
    }

    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertyGetter(typeof(Drawable), nameof(Drawable.PropagateNonPositionalInputSubTree));
        yield return AccessTools.PropertyGetter(typeof(Drawable), nameof(Drawable.PropagatePositionalInputSubTree));
    }

    static bool Prefix(Drawable __instance, ref bool __result)
    {
        if (!blocked.TryGetValue(__instance, out var counter) || counter.Value <= 0) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(InputManager), "isDrawableValidForFocus")]
public static class SomsLegacyFocusPatch
{
    static bool Prefix(Drawable drawable, ref bool __result)
    {
        if (!SomsLegacyInputPatch.IsBlocked(drawable)) return true;
        __result = false;
        return false;
    }
}
