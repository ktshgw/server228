#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Game.Graphics.Sprites;
using osu.Game.Online.Matchmaking;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Screens.OnlinePlay.Matchmaking.Queue;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(ScreenQueue), "populateAvailablePools")]
public static class MatchmakingLoadingPatch
{
    static void Postfix(ScreenQueue __instance, ref Task __result)
    {
        if (GlobalConfigManager.Patched && !GlobalConfigManager.Config.DisableServerExtensions)
            __result = Observe(__instance, __result);
    }

    static async Task Observe(ScreenQueue screen, Task request)
    {
        try
        {
            await request.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (SomsDrawableLifecycle.IsDisposed(screen))
                return;

            Logger.Error(error, "SOMS!: failed to load ranked matchmaking pools.");
            ScheduleAccess.ScheduleDelegate(screen, () =>
            {
                if (SomsDrawableLifecycle.IsDisposed(screen))
                    return;

                var pools = (Bindable<MatchmakingPool[]?>)AccessTools.Field(typeof(ScreenQueue), "availablePools").GetValue(screen)!;
                pools.Value = [];
            });
        }
    }
}

[HarmonyPatch(typeof(PoolSelector), "LoadComplete")]
public static class MatchmakingEmptyPoolsPatch
{
    private const string message_name = "soms-matchmaking-empty-pools";

    static void Postfix(PoolSelector __instance)
    {
        if (!GlobalConfigManager.Patched || GlobalConfigManager.Config.DisableServerExtensions || SomsDrawableLifecycle.IsDisposed(__instance))
            return;

        var children = (IEnumerable<Drawable>)AccessTools.PropertyGetter(typeof(CompositeDrawable), "InternalChildren").Invoke(__instance, null)!;
        if (children.Any(child => child.Name == message_name))
            return;

        var message = new OsuSpriteText
        {
            Name = message_name,
            Text = "Нет доступных Ranked-пулов. Попробуйте зайти снова чуть позже.",
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Alpha = 0,
        };
        AccessTools.Method(typeof(CompositeDrawable), "AddInternal", [typeof(Drawable)]).Invoke(__instance, [message]);

        bool disposed = false;
        Action<ValueChangedEvent<MatchmakingPool[]?>> updateMessage = value =>
        {
            if (disposed || SomsDrawableLifecycle.IsDisposed(__instance) || SomsDrawableLifecycle.IsDisposed(message))
                return;

            message.Alpha = value.NewValue is { Length: 0 } ? 1 : 0;
        };
        void unsubscribe()
        {
            disposed = true;
            __instance.AvailablePools.ValueChanged -= updateMessage;
        }

        SomsDrawableLifecycle.OnDispose(__instance, unsubscribe);
        SomsDrawableLifecycle.OnDispose(message, unsubscribe);
        __instance.AvailablePools.BindValueChanged(updateMessage, true);
    }
}
