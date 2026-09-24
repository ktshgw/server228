#nullable enable
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.Scoring;
using osu.Game.Skinning;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(SkinnableDrawable), "SkinChanged")]
public static class SomsSliderMissStylePatch
{
    private static readonly FieldInfo lookupField = AccessTools.Field(typeof(SkinnableDrawable), "ComponentLookup");

    [HarmonyPriority(Priority.First)]
    static void Prefix(SkinnableDrawable __instance, ISkinComponentLookup ___ComponentLookup, out ISkinComponentLookup? __state)
    {
        __state = null;
        if (SomsClientPreferences.Enabled && ___ComponentLookup is SkinComponentLookup<HitResult> { Component: HitResult.IgnoreMiss or HitResult.LargeTickMiss })
            SomsSliderMissReloadPatch.Record(__instance);
        if (!SomsClientPreferences.Enabled || SomsClientPreferences.Instance.SliderMissDisplay.Value != SomsSliderMissDisplay.Judgements)
            return;
        if (___ComponentLookup is not SkinComponentLookup<HitResult> result) return;
        HitResult? mapped = result.Component switch { HitResult.IgnoreMiss => HitResult.Ok, HitResult.LargeTickMiss => HitResult.Meh, _ => null };
        if (!mapped.HasValue) return;
        __state = ___ComponentLookup;
        lookupField.SetValue(__instance, new SkinComponentLookup<HitResult>(mapped.Value));
    }

    [HarmonyPriority(Priority.First)]
    static void Postfix(SkinnableDrawable __instance, ISkinComponentLookup? __state)
    {
        if (__state != null) lookupField.SetValue(__instance, __state);
    }

    static Exception? Finalizer(SkinnableDrawable __instance, ISkinComponentLookup? __state, Exception? __exception)
    {
        if (__state != null) lookupField.SetValue(__instance, __state);
        return __exception;
    }
}

[HarmonyPatch(typeof(SkinnableDrawable), "Update")]
public static class SomsSliderMissReloadPatch
{
    private sealed class Applied { public SomsSliderMissDisplay Mode = SomsClientPreferences.Instance.SliderMissDisplay.Value; }
    private static readonly ConditionalWeakTable<SkinnableDrawable, Applied> applied = new();
    private static readonly MethodInfo reload = AccessTools.Method(typeof(SkinReloadableDrawable), "onChange");
    private static readonly PropertyInfo currentSkin = AccessTools.Property(typeof(SkinReloadableDrawable), "CurrentSkin");
    internal static void Record(SkinnableDrawable drawable) => applied.GetOrCreateValue(drawable).Mode = SomsClientPreferences.Instance.SliderMissDisplay.Value;
    static void Postfix(SkinnableDrawable __instance, ISkinComponentLookup ___ComponentLookup)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance)
            || ___ComponentLookup is not SkinComponentLookup<HitResult> { Component: HitResult.IgnoreMiss or HitResult.LargeTickMiss }) return;
        var entry = applied.GetOrCreateValue(__instance);
        var mode = SomsClientPreferences.Instance.SliderMissDisplay.Value;
        if (entry.Mode == mode) return;
        bool rebuild = (entry.Mode == SomsSliderMissDisplay.Judgements) != (mode == SomsSliderMissDisplay.Judgements);
        entry.Mode = mode;
        if (!rebuild || currentSkin.GetValue(__instance) == null) return;
        // Use the native scheduled skin reload, including its OnSkinChanged notification to judgement proxies.
        reload.Invoke(__instance, null);
    }
}
