#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics;
using osu.Game.Online;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays.Profile.Header.Components;
using osu.Game.Overlays.Settings.Sections;
using osu.Game.Overlays.Toolbar;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Users;
using osu.Game.Users.Drawables;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(LocalUserStatisticsProvider), "LoadComplete")]
public static class SomsStealthAttachPatch
{
    private static readonly ConditionalWeakTable<OsuGameBase, SomsStealthSession> attached = new();
    static void Postfix(LocalUserStatisticsProvider __instance)
    {
        if (!SomsClientPreferences.Enabled) return;
        var game = GlobalConfigManager.GameBase;
        if (!ReferenceEquals(game.Dependencies.Get<LocalUserStatisticsProvider>(), __instance)
            || attached.TryGetValue(game, out _)) return;

        // OsuGame loads its statistics provider asynchronously. Attach once that
        // dependency is ready, through Game's public content API: AddInternal on
        // Game is deliberately sealed to throw "Use Add or Content instead".
        var session = new SomsStealthSession();
        game.Add(session);
        attached.Add(game, session);
    }
}

[HarmonyPatch(typeof(DebugSection), MethodType.Constructor)]
public static class SomsStealthSettingsPatch
{
    static void Postfix(DebugSection __instance)
    {
        if (SomsClientPreferences.Enabled) __instance.Add(new SomsStealthSettings());
    }
}

// All substitutions below are on drawables or detached display statistics, never the
// account model, request payload, Realm, authentication configuration or server state.
public static class SomsStealthPresentation
{
    private sealed class TextState { public LocalisableString Original; public bool IsName; public IUser? RankUser; }
    private static readonly ConditionalWeakTable<SpriteText, TextState> texts = new();
    private static readonly MethodInfo clone = AccessTools.Method(typeof(object), "MemberwiseClone");
    [ThreadStatic] private static bool updatingText;

    internal static bool IsLocal(IUser? user) => user != null && SomsStealthSession.Current?.RealUser is { } own
        && own.OnlineID > 0 && own.OnlineID == user.OnlineID && !SomsMapperProfiles.IsOfficial(user);

    internal static UserStatistics? Statistics(UserStatistics? value)
    {
        if (value == null || SomsStealthSession.Current is not { Active: true, DisplayRank: { } rank }) return value;
        var copy = (UserStatistics)clone.Invoke(value, null)!;
        copy.GlobalRank = rank;
        copy.CountryRank = SomsStealthSession.Current.DisplayCountryRank;
        copy.GlobalRankPercent = null;
        copy.Variants = null;
        return copy;
    }

    internal static void SetText(SpriteText drawable, ref LocalisableString value)
    {
        if (updatingText || SomsStealthSession.Current is not { } session) return;
        string original = value.ToString();
        bool match = !string.IsNullOrEmpty(session.RealUser?.Username) && original == session.RealUser.Username;
        if (match || texts.TryGetValue(drawable, out _))
        {
            var state = texts.GetOrCreateValue(drawable);
            state.Original = value;
            state.IsName = match;
            if (match && session.Active) value = session.Identity!.Username;
        }
    }

    internal static void TrackRank(SpriteText drawable, IUser user)
    {
        if (!IsLocal(user)) return;
        var state = texts.GetOrCreateValue(drawable);
        state.Original = drawable.Text;
        state.RankUser = user;
        renderText(drawable, state);
    }

    internal static APIUser? ProfileUser(IUser? original)
    {
        if (IsLocal(original) && SomsStealthSession.Current is { Active: true, Identity: { } identity })
        {
            var user = new APIUser { Id = identity.Id, Username = identity.Username, AvatarUrl = identity.AvatarUrl,
                CountryCode = SomsStealthCountryPresentation.CountryFor(original!, original is APIUser apiUser ? apiUser.CountryCode : CountryCode.Unknown) };
            SomsMapperProfiles.Mark(user);
            return user;
        }
        return null;
    }

    private static void renderText(SpriteText text, TextState state)
    {
        if (SomsDrawableLifecycle.IsDisposed(text)) return;
        var session = SomsStealthSession.Current;
        LocalisableString value = state.Original;
        if (session?.Active == true)
        {
            if (state.IsName) value = session.Identity!.Username;
            else if (IsLocal(state.RankUser)) value = $"#{session.DisplayRank:N0}";
        }
        updatingText = true;
        try { text.Text = value; }
        finally { updatingText = false; }
    }

    public static void Refresh()
    {
        if (SomsStealthSession.Current == null) return;
        SomsStealthSession.Current.Enqueue(() =>
        {
            if (SomsStealthSession.Current == null) return;
            visit(GlobalConfigManager.GameBase);
        });
    }

    private static void visit(Drawable drawable)
    {
        if (SomsDrawableLifecycle.IsDisposed(drawable) || !drawable.IsLoaded) return;
        if (drawable is SpriteText text)
        {
            if (texts.TryGetValue(text, out var state)) renderText(text, state);
            else { var value = text.Text; SetText(text, ref value); if (value != text.Text) { updatingText = true; try { text.Text = value; } finally { updatingText = false; } } }
        }
        if (drawable is DrawableAvatar avatar) SomsAvatarPatch.Refresh(avatar);
        if (drawable is UpdateableFlag flag) SomsStealthCountryPresentation.Refresh(flag);
        if (drawable is osu.Game.Overlays.Profile.Header.TopHeaderContainer header) SomsStealthCountryPresentation.RefreshHeader(header);
        if (drawable is UserRankPanel rankPanel)
            AccessTools.Method(typeof(UserRankPanel), "updateDisplay").Invoke(rankPanel, null);
        if (drawable is SomsLegacyUserPanel)
            AccessTools.Method(typeof(SomsLegacyUserPanel), "updateStatistics").Invoke(drawable, null);
        if (drawable is MainDetails details)
            AccessTools.Method(typeof(MainDetails), "updateDisplay").Invoke(details, new object?[] { details.User.Value });
        if (drawable is CompositeDrawable composite)
            foreach (var child in SomsLegacyInterfacePatch.Children(composite).ToArray()) visit(child);
    }
}

[HarmonyPatch(typeof(SpriteText), nameof(SpriteText.Text), MethodType.Setter)]
public static class SomsStealthTextPatch
{
    static void Prefix(SpriteText __instance, ref LocalisableString value) => SomsStealthPresentation.SetText(__instance, ref value);
}

[HarmonyPatch(typeof(DrawableAvatar), MethodType.Constructor, typeof(IUser))]
public static class SomsStealthAvatarConstructorPatch
{
    static void Postfix(DrawableAvatar __instance, IUser? user)
    {
        if (SomsClientPreferences.Enabled) SomsAvatarPatch.Track(__instance, user);
    }
}

[HarmonyPatch(typeof(DrawableAvatar), "LoadComplete")]
public static class SomsStealthAvatarPatch
{
    static void Postfix(DrawableAvatar __instance)
    {
        if (SomsClientPreferences.Enabled) SomsAvatarPatch.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(OsuGame), nameof(OsuGame.ShowUser))]
public static class SomsStealthOpenProfilePatch
{
    static bool Prefix(OsuGame __instance, IUser user)
    {
        if (!SomsClientPreferences.Enabled || SomsStealthPresentation.ProfileUser(user) is not { } official) return true;
        SomsOfficialProfileOverlay.Open(__instance, official);
        return false;
    }
}

[HarmonyPatch(typeof(LocalUserStatisticsProvider), nameof(LocalUserStatisticsProvider.GetStatisticsFor))]
public static class SomsStealthStatisticsPatch
{
    static void Postfix(ref UserStatistics? __result) => __result = SomsStealthPresentation.Statistics(__result);
}

[HarmonyPatch(typeof(UserPanel), "CreateRank")]
public static class SomsStealthCardRankPatch
{
    static void Postfix(UserPanel __instance, SpriteText __result) => SomsStealthPresentation.TrackRank(__result, __instance.User);
}

[HarmonyPatch(typeof(MainDetails), "updateDisplay")]
public static class SomsStealthProfileRankPatch
{
    static void Postfix(MainDetails __instance)
    {
        if (!SomsClientPreferences.Enabled) return;
        bool active = SomsStealthPresentation.IsLocal(__instance.User.Value?.User) && SomsStealthSession.Current?.Active == true;
        var graph = (Drawable)AccessTools.Field(typeof(MainDetails), "rankGraph").GetValue(__instance)!;
        graph.Alpha = active ? 0 : 1;
        if (!active) return;
        var display = (GlobalRankDisplay)AccessTools.Field(typeof(MainDetails), "detailGlobalRank").GetValue(__instance)!;
        display.HighestRank.Value = null;
        display.UserStatistics.Value = SomsStealthPresentation.Statistics(__instance.User.Value!.User.Statistics);
        var country = (ProfileValueDisplay)AccessTools.Field(typeof(MainDetails), "detailCountryRank").GetValue(__instance)!;
        country.Content.Text = SomsStealthSession.Current?.DisplayCountryRank is { } rank ? $"#{rank:N0}" : "—";
        country.Content.TooltipText = SomsStealthSession.Current?.CountryRankEstimated == true ? "Приблизительное место в рейтинге страны" : "";
    }
}

[HarmonyPatch]
public static class SomsStealthRankNotificationPatch
{
    static IEnumerable<MethodBase> TargetMethods() => typeof(TransientUserStatisticsUpdateDisplay)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .Where(m => m.Name.Contains("<LoadComplete>") && m.GetParameters().Length == 1
            && m.GetParameters()[0].ParameterType == typeof(ValueChangedEvent<ScoreBasedUserStatisticsUpdate>));

    static void Prefix(ref ValueChangedEvent<ScoreBasedUserStatisticsUpdate?> __0)
    {
        if (SomsStealthSession.Current?.Active != true || __0.NewValue is not { } update) return;
        __0 = new ValueChangedEvent<ScoreBasedUserStatisticsUpdate?>(__0.OldValue,
            new ScoreBasedUserStatisticsUpdate(update.Score, SomsStealthPresentation.Statistics(update.Before)!,
                SomsStealthPresentation.Statistics(update.After)!));
    }
}
