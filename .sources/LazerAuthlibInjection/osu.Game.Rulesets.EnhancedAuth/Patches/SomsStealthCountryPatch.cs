#nullable enable
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Extensions;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.API;
using osu.Game.Online.Leaderboards;
using osu.Game.Overlays.BeatmapSet.Scores;
using osu.Game.Overlays.Profile.Header;
using osu.Game.Overlays.Rankings.Tables;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.OnlinePlay.Multiplayer.Participants;
using osu.Game.Screens.Select;
using osu.Game.Users;
using osu.Game.Users.Drawables;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Bind flags to their user, not to a country code shared by unrelated players.
// APIUser.CountryCode and all serialized account/score data stay unchanged.
public static class SomsStealthCountryPresentation
{
    private sealed class FlagState { public IUser? User; public CountryCode Original; }
    private static readonly ConditionalWeakTable<UpdateableFlag, FlagState> flags = new();
    [ThreadStatic] private static bool applying;

    internal static CountryCode CountryFor(IUser user, CountryCode original) =>
        SomsStealthPresentation.IsLocal(user) && SomsStealthSession.Current is { Active: true, Identity: { } identity }
        && Enum.TryParse(identity.CountryCode, true, out CountryCode country) && country != CountryCode.Unknown
            ? country : original;

    internal static void Track(UpdateableFlag flag, IUser? user)
    {
        if (!SomsClientPreferences.Enabled) return;
        var state = flags.GetOrCreateValue(flag);
        state.User = user;
        state.Original = (user as APIUser)?.CountryCode ?? CountryCode.Unknown;
        Refresh(flag);
    }

    internal static void Changing(UpdateableFlag flag, ref CountryCode value)
    {
        if (applying || !SomsClientPreferences.Enabled || !flags.TryGetValue(flag, out var state)) return;
        state.Original = value;
        if (state.User != null) value = CountryFor(state.User, value);
    }

    internal static void Refresh(UpdateableFlag flag)
    {
        if (!flags.TryGetValue(flag, out var state) || SomsDrawableLifecycle.IsDisposed(flag)) return;
        var desired = state.User == null ? state.Original : CountryFor(state.User, state.Original);
        if (flag.CountryCode == desired) return;
        applying = true;
        try { flag.CountryCode = desired; }
        finally { applying = false; }
    }

    internal static void Loaded(UpdateableFlag flag)
    {
        if (!SomsClientPreferences.Enabled || flags.TryGetValue(flag, out _)) return;
        for (Drawable? parent = flag.Parent; parent != null; parent = parent.Parent)
        {
            switch (parent)
            {
                case UserPanel panel: Track(flag, panel.User); return;
                case TopHeaderContainer header: Track(flag, header.User.Value?.User); return;
                case BeatmapLeaderboardScore score: Track(flag, score.Score.User); return;
                case LeaderboardScore score: Track(flag, score.Score.User); return;
                case PlayerFlag player:
                    var state = (GameplayState?)AccessTools.Property(player.GetType(), "gameplayState").GetValue(player);
                    Track(flag, state?.Score.ScoreInfo.User ?? ((IAPIProvider)AccessTools.Property(player.GetType(), "api").GetValue(player)!).LocalUser.Value);
                    return;
            }
        }
    }

    internal static void RefreshHeader(TopHeaderContainer header)
    {
        if (!SomsClientPreferences.Enabled) return;
        var user = header.User.Value?.User;
        var flag = (UpdateableFlag)AccessTools.Field(typeof(TopHeaderContainer), "userFlag").GetValue(header)!;
        Track(flag, user);
        var country = user == null ? CountryCode.Unknown : CountryFor(user, user.CountryCode);
        ((SpriteText)AccessTools.Field(typeof(TopHeaderContainer), "userCountryText").GetValue(header)!).Text = country.GetDescription();
        var button = (osu.Framework.Graphics.Containers.ClickableContainer)AccessTools.Field(typeof(TopHeaderContainer), "userCountryContainer").GetValue(header)!;
        button.Action = () => ((RankingsOverlay?)AccessTools.Property(typeof(TopHeaderContainer), "rankingsOverlay").GetValue(header))?.ShowCountry(country);
    }
}

[HarmonyPatch(typeof(ParticipantPanel), "updateUser")]
public static class SomsStealthParticipantCountryPatch
{
    static void Postfix(ParticipantPanel __instance)
    {
        var slot = __instance.Current.Value;
        SomsStealthCountryPresentation.Track((UpdateableFlag)AccessTools.Field(typeof(ParticipantPanel), "userFlag").GetValue(__instance)!,
            slot.IsEmpty ? null : slot.User.User);
    }
}

[HarmonyPatch(typeof(UserPanel), "CreateFlag")]
public static class SomsStealthUserFlagPatch
{
    static void Postfix(UserPanel __instance, UpdateableFlag __result) => SomsStealthCountryPresentation.Track(__result, __instance.User);
}

[HarmonyPatch(typeof(UpdateableFlag), nameof(UpdateableFlag.CountryCode), MethodType.Setter)]
public static class SomsStealthFlagValuePatch
{
    static void Prefix(UpdateableFlag __instance, ref CountryCode value) => SomsStealthCountryPresentation.Changing(__instance, ref value);
}

[HarmonyPatch(typeof(UpdateableFlag), "load")]
public static class SomsStealthFlagLoadPatch
{
    private static readonly System.Reflection.MethodInfo getScheduler = AccessTools.PropertyGetter(typeof(Drawable), "Scheduler");
    static void Postfix(UpdateableFlag __instance) =>
        ((osu.Framework.Threading.Scheduler)getScheduler.Invoke(__instance, null)!).Add(() => SomsStealthCountryPresentation.Loaded(__instance));
}

[HarmonyPatch(typeof(TopHeaderContainer), "updateUser")]
public static class SomsStealthHeaderCountryPatch
{
    static void Postfix(TopHeaderContainer __instance) => SomsStealthCountryPresentation.RefreshHeader(__instance);
}

[HarmonyPatch(typeof(TopScoreUserSection), nameof(TopScoreUserSection.Score), MethodType.Setter)]
public static class SomsStealthTopScoreCountryPatch
{
    static void Postfix(TopScoreUserSection __instance, ScoreInfo value) => SomsStealthCountryPresentation.Track(
        (UpdateableFlag)AccessTools.Field(typeof(TopScoreUserSection), "flag").GetValue(__instance)!, value.User);
}

[HarmonyPatch(typeof(ScoreTable), "createContent")]
public static class SomsStealthScoreTableCountryPatch
{
    static void Postfix(ScoreInfo score, Drawable[] __result)
    {
        foreach (var flag in __result.OfType<UpdateableFlag>()) SomsStealthCountryPresentation.Track(flag, score.User);
    }
}

[HarmonyPatch(typeof(UserBasedTable), "CreateRowContent")]
public static class SomsStealthRankingTableCountryPatch
{
    static void Postfix(UserStatistics item, Drawable[] __result)
    {
        foreach (var container in __result.OfType<CompositeDrawable>())
            foreach (var flag in SomsLegacyInterfacePatch.Children(container).OfType<UpdateableFlag>())
                SomsStealthCountryPresentation.Track(flag, item.User);
    }
}
