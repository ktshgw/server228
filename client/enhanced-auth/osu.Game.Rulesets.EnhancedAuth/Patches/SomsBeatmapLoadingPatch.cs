#nullable enable
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Configuration;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(BeatmapSetOverlay), "performFetch")]
public static class SomsBeatmapLoadingPatch
{
    private static readonly ConditionalWeakTable<BeatmapSetOverlay, FetchState> states = new();
    private static readonly AccessTools.FieldRef<BeatmapSetOverlay, Bindable<APIBeatmapSet>> model =
        AccessTools.FieldRefAccess<BeatmapSetOverlay, Bindable<APIBeatmapSet>>("beatmapSet");
    private static readonly Func<BeatmapSetOverlay, IAPIProvider> provider = AccessTools.MethodDelegate<Func<BeatmapSetOverlay, IAPIProvider>>(
        AccessTools.PropertyGetter(typeof(BeatmapSetOverlay), "api"));

    internal static void CancelFor(BeatmapSetOverlay overlay)
    {
        if (states.TryGetValue(overlay, out var state)) state.Cancel();
    }

    static bool Prefix(BeatmapSetOverlay __instance, (BeatmapSetLookupType type, int id)? ___lastLookup)
    {
        if (!SomsClientPreferences.Enabled) return true;
        var api = provider(__instance);
        if (___lastLookup == null || !api.IsLoggedIn || SomsDrawableLifecycle.IsDisposed(__instance)) return false;
        states.GetValue(__instance, overlay => new FetchState(overlay)).Start(api, ___lastLookup.Value);
        return false;
    }

    private sealed class FetchState
    {
        private readonly BeatmapSetOverlay overlay;
        private GetBeatmapSetRequest? request;
        private ScheduledDelegate? timeout;
        private FormButton? retry;
        private int generation;
        private bool disposed;

        public FetchState(BeatmapSetOverlay overlay)
        {
            this.overlay = overlay;
            SomsDrawableLifecycle.OnDispose(overlay, () => { disposed = true; Cancel(); });
            overlay.State.BindValueChanged(value => { if (value.NewValue == Visibility.Hidden) Cancel(); });
        }

        internal void Cancel()
        {
            generation++;
            timeout?.Cancel();
            request?.Cancel();
            request = null;
            if (retry != null && !disposed) retry.Alpha = 0;
        }

        public void Start(IAPIProvider api, (BeatmapSetLookupType type, int id) lookup, int attempt = 0)
        {
            Cancel();
            if (disposed) return;
            if (retry != null) retry.Alpha = 0;
            int current = generation;
            var pending = request = new GetBeatmapSetRequest(lookup.id, lookup.type);
            bool IsCurrent() => !disposed && current == generation;
            void Fail(Exception error)
            {
                if (!IsCurrent()) return;
                Cancel();
                if (attempt == 0 && error is not OperationCanceledException)
                {
                    Start(api, lookup, 1);
                    return;
                }
                Logger.Error(error, $"SOMS!: beatmap page {lookup.type}/{lookup.id} could not be loaded.");
                if (retry == null)
                {
                    retry = new FormButton
                    {
                        Name = "soms-beatmap-retry", Caption = "Не удалось загрузить карту",
                        ButtonText = "Повторить", RelativeSizeAxes = Axes.X,
                        Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Y = 10,
                        Depth = float.MinValue,
                    };
                    AccessTools.Method(typeof(CompositeDrawable), "AddInternal", [typeof(Drawable)]).Invoke(overlay, [retry]);
                }
                retry.Action = () => Start(api, lookup);
                retry.Alpha = 1;
            }
            pending.Success += result =>
            {
                if (!IsCurrent()) return;
                timeout?.Cancel();
                request = null;
                model(overlay).Value = result;
                if (lookup.type == BeatmapSetLookupType.BeatmapId)
                {
                    var selected = result.Beatmaps.FirstOrDefault(map => map.OnlineID == lookup.id);
                    if (selected != null) overlay.Header.HeaderContent.Picker.Beatmap.Value = selected;
                }
            };
            pending.Failure += Fail;
            var scheduler = (Scheduler)AccessTools.PropertyGetter(typeof(Drawable), "Scheduler").Invoke(overlay, null)!;
            timeout = scheduler.AddDelayed(() => Fail(new TimeoutException("Beatmap page request timed out.")), 15000);
            api.Queue(pending);
        }
    }
}

[HarmonyPatch(typeof(BeatmapSetOverlay), nameof(BeatmapSetOverlay.ShowBeatmapSet))]
public static class SomsBeatmapDirectDisplayPatch
{
    static void Prefix(BeatmapSetOverlay __instance) => SomsBeatmapLoadingPatch.CancelFor(__instance);
}
