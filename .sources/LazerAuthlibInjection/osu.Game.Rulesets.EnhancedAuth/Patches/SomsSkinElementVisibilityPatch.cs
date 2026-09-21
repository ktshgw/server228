#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Threading;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.Scoring;
using osu.Game.Skinning;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch]
public static class SomsSkinElementVisibilityPatch
{
    private static readonly ConditionalWeakTable<SkinnableDrawable, PreferenceBinding> bindings = new();

    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SkinnableDrawable), "SkinChanged");
        yield return AccessTools.Method(typeof(SkinnableDrawable), "Update");
    }

    static void Postfix(SkinnableDrawable __instance, ISkinComponentLookup ___ComponentLookup)
    {
        if (SomsDrawableLifecycle.IsDisposed(__instance))
            return;

        if (bindings.TryGetValue(__instance, out var existing))
        {
            existing.Apply();
            return;
        }

        if (!SomsClientPreferences.Enabled)
            return;

        Bindable<bool>? preference = null;
        if (___ComponentLookup is SkinComponentLookup<HitResult> { Component: HitResult.IgnoreMiss or HitResult.LargeTickMiss })
            preference = SomsClientPreferences.Instance.ShowSliderEndMiss;
        else if (___ComponentLookup.GetType().FullName == "osu.Game.Rulesets.Osu.OsuSkinComponentLookup")
            preference = AccessTools.Field(___ComponentLookup.GetType(), "Component").GetValue(___ComponentLookup)?.ToString() switch
            {
                "SliderFollowCircle" => SomsClientPreferences.Instance.ShowSliderFollowCircle,
                "FollowPoint" => SomsClientPreferences.Instance.DrawFollowPoints,
                _ => null,
            };

        if (preference == null)
            return;

        lock (bindings)
        {
            if (!bindings.TryGetValue(__instance, out var registration))
            {
                registration = new PreferenceBinding(__instance, preference);
                bindings.Add(__instance, registration);
                SomsDrawableLifecycle.OnDispose(__instance, () => release(__instance));
            }
            registration.Apply();
        }
    }

    private static void release(SkinnableDrawable drawable)
    {
        lock (bindings)
        {
            if (!bindings.TryGetValue(drawable, out var registration))
                return;
            bindings.Remove(drawable);
            registration.Dispose();
        }
    }

    private sealed class PreferenceBinding : IDisposable
    {
        private readonly SkinnableDrawable drawable;
        private readonly Bindable<bool> preference;
        private float originalAlpha;
        private bool originalAlwaysPresent;
        private bool hidden;
        private ScheduledDelegate? pendingUpdate;
        private volatile bool disposed;

        public PreferenceBinding(SkinnableDrawable drawable, Bindable<bool> preference)
        {
            this.drawable = drawable;
            this.preference = preference.GetBoundCopy();
            originalAlpha = drawable.Alpha;
            originalAlwaysPresent = drawable.AlwaysPresent;
            this.preference.BindValueChanged(_ => scheduleUpdate());
        }

        public void Apply()
        {
            if (disposed || SomsDrawableLifecycle.IsDisposed(drawable))
                return;

            // Enforce before children update, including objects whose first skin
            // load preceded patch activation. SkinChanged alone cannot guard
            // visibility after animation resets, pooling or replay seeking.
            // Keep the native animated child and its proxies intact.
            bool visible = !SomsClientPreferences.Enabled || preference.Value;
            if (visible)
            {
                if (hidden)
                {
                    drawable.AlwaysPresent = originalAlwaysPresent;
                    drawable.Alpha = originalAlpha;
                    hidden = false;
                }
                return;
            }

            if (!hidden)
            {
                originalAlpha = drawable.Alpha;
                originalAlwaysPresent = drawable.AlwaysPresent;
                hidden = true;
            }
            drawable.AlwaysPresent = true;
            drawable.Alpha = 0;
        }

        private void scheduleUpdate()
        {
            if (disposed || SomsDrawableLifecycle.IsDisposed(drawable))
                return;
            pendingUpdate?.Cancel();
            pendingUpdate = ScheduleAccess.ScheduleDelegate(drawable, () =>
            {
                pendingUpdate = null;
                Apply();
            });
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            preference.UnbindAll();
            pendingUpdate?.Cancel();
            pendingUpdate = null;
        }
    }
}
