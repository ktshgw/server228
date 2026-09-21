#nullable enable
using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Threading;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(DrawableGameplayLeaderboard), "updateState")]
public static class SomsLeaderboardCollapsePatch
{
    public static bool ResolveExpanded(bool useSkinCollapse, bool skinExpanded) => !useSkinCollapse || skinExpanded;

    static void Postfix(Bindable<bool> ___expanded)
    {
        if (!SomsClientPreferences.Enabled)
            return;

        ___expanded.Value = ResolveExpanded(SomsClientPreferences.Instance.UseSkinLeaderboardCollapse.Value, ___expanded.Value);
    }
}

[HarmonyPatch(typeof(DrawableGameplayLeaderboard), "LoadComplete")]
public static class SomsLeaderboardPreferenceBindingPatch
{
    // Bindables use weak references between bound copies. Keep each copy alive only
    // as long as its leaderboard, so loading many skins/replays does not leak drawables.
    private static readonly ConditionalWeakTable<DrawableGameplayLeaderboard, PreferenceBinding> bindings = new();

    static void Postfix(DrawableGameplayLeaderboard __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance))
            return;

        lock (bindings)
        {
            if (bindings.TryGetValue(__instance, out _))
                return;

            var registration = new PreferenceBinding(__instance);
            bindings.Add(__instance, registration);
            SomsDrawableLifecycle.OnDispose(__instance, () => release(__instance));
        }
    }

    private static void release(DrawableGameplayLeaderboard leaderboard)
    {
        lock (bindings)
        {
            if (!bindings.TryGetValue(leaderboard, out var registration))
                return;
            bindings.Remove(leaderboard);
            registration.Dispose();
        }
    }

    private sealed class PreferenceBinding : IDisposable
    {
        private readonly DrawableGameplayLeaderboard leaderboard;
        private readonly Bindable<bool> preference;
        private ScheduledDelegate? pendingUpdate;
        private volatile bool disposed;

        public PreferenceBinding(DrawableGameplayLeaderboard leaderboard)
        {
            this.leaderboard = leaderboard;
            preference = SomsClientPreferences.Instance.UseSkinLeaderboardCollapse.GetBoundCopy();
            preference.BindValueChanged(_ => scheduleUpdate());
        }

        private void scheduleUpdate()
        {
            if (disposed || SomsDrawableLifecycle.IsDisposed(leaderboard))
                return;

            pendingUpdate?.Cancel();
            pendingUpdate = ScheduleAccess.ScheduleDelegate(leaderboard, () =>
            {
                pendingUpdate = null;
                if (!disposed && !SomsDrawableLifecycle.IsDisposed(leaderboard))
                    AccessTools.Method(typeof(DrawableGameplayLeaderboard), "updateState").Invoke(leaderboard, null);
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
