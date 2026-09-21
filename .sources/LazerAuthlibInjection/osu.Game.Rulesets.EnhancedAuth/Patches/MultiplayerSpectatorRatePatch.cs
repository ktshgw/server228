using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Timing;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Scoring;
using osu.Game.Screens.OnlinePlay.Multiplayer.Spectate;
using osu.Game.Screens.Play;
using osu.Game.Utils;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Multi-spectating normally assumes all players have the same song rate. Keep
// replay time per player while sharing the selected audio source's wall clock.
internal static class MultiplayerSpectatorRates
{
    internal sealed class Rate
    {
        public double Value = 1;
        public long Frame;
        public long LastProcessedFrame = -1;
        public bool ManagedFrame;
        public long SeekRevision;
        public long ObservedSeekRevision;
        public double LastSourceSeek;
    }
    internal static readonly ConditionalWeakTable<object, Rate> Rates = new();
    internal static bool Enabled => GlobalConfigManager.Patched && !GlobalConfigManager.Config.NonG0V0Server;
    internal static double Get(object owner) => Rates.GetOrCreateValue(owner).Value;
    internal static void BeginFrame(SpectatorPlayerClock clock)
    {
        var state = Rates.GetOrCreateValue(clock);
        state.ManagedFrame = true;
        state.Frame++;
    }
    internal static IFrameBasedClock Master(SpectatorPlayerClock clock)
        => (IFrameBasedClock)AccessTools.Field(typeof(SpectatorPlayerClock), "masterClock").GetValue(clock);
}

[HarmonyPatch(typeof(PlayerArea), nameof(PlayerArea.LoadScore))]
public static class MultiplayerSpectatorScoreRatePatch
{
    static void Postfix(PlayerArea __instance, Score score)
    {
        if (MultiplayerSpectatorRates.Enabled)
            MultiplayerSpectatorRates.Rates.GetOrCreateValue(__instance.SpectatorPlayerClock).Value = ModUtils.CalculateRateWithMods(score.ScoreInfo.Mods);
    }
}

[HarmonyPatch(typeof(MultiSpectatorScreen), "bindAudioAdjustments")]
public static class MultiplayerSpectatorAudioRatePatch
{
    static void Postfix(MultiSpectatorScreen __instance, PlayerArea first)
    {
        if (!MultiplayerSpectatorRates.Enabled)
            return;
        var master = (GameplayClockContainer)AccessTools.Field(typeof(MultiSpectatorScreen), "masterClockContainer").GetValue(__instance);
        double rate = MultiplayerSpectatorRates.Get(first.SpectatorPlayerClock);
        if (Math.Abs(MultiplayerSpectatorRates.Get(master) - rate) > 0.0001)
        {
            // Changing the audible player also changes which replay time is
            // represented by the shared clock.
            master.Seek(first.SpectatorPlayerClock.CurrentTime);
            var reference = MultiplayerSpectatorRates.Rates.GetOrCreateValue(master);
            reference.Value = rate;
            reference.LastSourceSeek = master.CurrentTime;
            reference.SeekRevision++;
        }
    }
}

[HarmonyPatch(typeof(SpectatorPlayerClock), nameof(SpectatorPlayerClock.ProcessFrame))]
public static class MultiplayerSpectatorClockRatePatch
{
    static bool Prefix(SpectatorPlayerClock __instance)
    {
        if (!MultiplayerSpectatorRates.Enabled)
            return true;
        var frame = MultiplayerSpectatorRates.Rates.GetOrCreateValue(__instance);
        if (frame.ManagedFrame && frame.Frame == frame.LastProcessedFrame)
        {
            AccessTools.PropertySetter(typeof(SpectatorPlayerClock), nameof(SpectatorPlayerClock.ElapsedFrameTime)).Invoke(__instance, [0d]);
            return false;
        }
        frame.LastProcessedFrame = frame.Frame;
        IFrameBasedClock master = MultiplayerSpectatorRates.Master(__instance);
        var lastSeen = AccessTools.Field(typeof(SpectatorPlayerClock), "lastSeenMasterTime");
        var reference = MultiplayerSpectatorRates.Rates.GetOrCreateValue(master);
        if (frame.ObservedSeekRevision != reference.SeekRevision)
        {
            lastSeen.SetValue(__instance, reference.LastSourceSeek);
            frame.ObservedSeekRevision = reference.SeekRevision;
        }
        double masterDelta = master.CurrentTime - (double)lastSeen.GetValue(__instance);
        lastSeen.SetValue(__instance, master.CurrentTime);
        double playerRate = MultiplayerSpectatorRates.Get(__instance);
        double sourceRate = MultiplayerSpectatorRates.Get(master);
        double elapsed = 0;
        if (__instance.IsRunning)
        {
            double targetTime = master.CurrentTime / sourceRate * playerRate;
            double delta = master.ElapsedFrameTime != 0
                ? masterDelta / sourceRate * playerRate
                : Math.Clamp(targetTime - __instance.CurrentTime, 0, 16 * playerRate);
            elapsed = delta * (__instance.IsCatchingUp ? SpectatorPlayerClock.CATCHUP_RATE : 1);
            AccessTools.PropertySetter(typeof(SpectatorPlayerClock), nameof(SpectatorPlayerClock.CurrentTime)).Invoke(__instance, [__instance.CurrentTime + elapsed]);
        }
        AccessTools.PropertySetter(typeof(SpectatorPlayerClock), nameof(SpectatorPlayerClock.ElapsedFrameTime)).Invoke(__instance, [elapsed]);
        AccessTools.PropertySetter(typeof(SpectatorPlayerClock), nameof(SpectatorPlayerClock.FramesPerSecond)).Invoke(__instance, [__instance.IsRunning ? master.FramesPerSecond : 0]);
        return false;
    }
}

[HarmonyPatch(typeof(SpectatorPlayerClock), nameof(SpectatorPlayerClock.Rate), MethodType.Getter)]
public static class MultiplayerSpectatorClockReportedRatePatch
{
    static void Postfix(SpectatorPlayerClock __instance, ref double __result)
    {
        if (MultiplayerSpectatorRates.Enabled)
            __result *= MultiplayerSpectatorRates.Get(__instance) / MultiplayerSpectatorRates.Get(MultiplayerSpectatorRates.Master(__instance));
    }
}

[HarmonyPatch(typeof(SpectatorSyncManager), "updatePlayerCatchup")]
public static class MultiplayerSpectatorCatchupPatch
{
    static bool Prefix(SpectatorSyncManager __instance)
    {
        if (!MultiplayerSpectatorRates.Enabled)
            return true;
        var master = (GameplayClockContainer)AccessTools.Field(typeof(SpectatorSyncManager), "masterClock").GetValue(__instance);
        var clocks = (List<SpectatorPlayerClock>)AccessTools.Field(typeof(SpectatorSyncManager), "playerClocks").GetValue(__instance);
        double sourceRate = MultiplayerSpectatorRates.Get(master);
        foreach (var clock in clocks)
        {
            MultiplayerSpectatorRates.BeginFrame(clock);
            double delta = master.CurrentTime / sourceRate - clock.CurrentTime / MultiplayerSpectatorRates.Get(clock);
            if (delta < -SpectatorSyncManager.SYNC_TARGET)
            {
                clock.IsCatchingUp = false;
                clock.IsRunning = false;
                continue;
            }
            clock.IsRunning = !clock.WaitingOnFrames;
            if (clock.IsCatchingUp && delta <= SpectatorSyncManager.SYNC_TARGET)
                clock.IsCatchingUp = false;
            else if (!clock.IsCatchingUp && delta > SpectatorSyncManager.MAX_SYNC_OFFSET)
                clock.IsCatchingUp = true;
        }
        return false;
    }
}
