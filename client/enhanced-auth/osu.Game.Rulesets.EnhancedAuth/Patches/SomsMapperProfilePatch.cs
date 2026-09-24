#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays.BeatmapSet;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Users;
using osu.Game.Users.Drawables;
using osu.Game.Screens.Select;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Graphics.Containers;
using osu.Game.Online;
using osu.Game.Online.Chat;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Keep origin on the actual user object, never on a numeric ID shared by two servers.
public static class SomsMapperProfiles
{
    private static readonly ConditionalWeakTable<IUser, object> official = new();
    internal static bool IsOfficial(IUser? user) => user != null && official.TryGetValue(user, out _);
    internal static APIUser FromMetadata(IUser author)
    {
        // Imported .osu files often use RealmUser's sentinel ID=1. Resolve such authors by name.
        // Detach the identity from Realm: clicking may happen after the selected map changes.
        var user = new APIUser { Id = author.OnlineID > 1 ? author.OnlineID : 0, Username = author.Username };
        Mark(user);
        return user;
    }
    internal static void Mark(IUser? user)
    {
        if (!SomsClientPreferences.Enabled || user == null || user.OnlineID == 1
            || (user.OnlineID <= 0 && string.IsNullOrWhiteSpace(user.Username))) return;
        official.GetValue(user, _ => new object());
        if (user is APIUser apiUser && apiUser.Id > 1) apiUser.AvatarUrl = $"https://a.ppy.sh/{apiUser.Id}";
    }
}

[HarmonyPatch(typeof(AuthorInfo), "updateDisplay")]
public static class SomsMapperAuthorPatch
{
    static void Prefix(AuthorInfo __instance) => SomsMapperProfiles.Mark(__instance.BeatmapSet?.Author);
}

[HarmonyPatch]
public static class SomsMapperResponsePatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertySetter(typeof(APIBeatmapSet), nameof(APIBeatmapSet.AuthorID));
        yield return AccessTools.PropertySetter(typeof(APIBeatmapSet), "author");
    }
    static void Postfix(APIBeatmapSet __instance) => SomsMapperProfiles.Mark(__instance.Author);
}

[HarmonyPatch(typeof(ClickableAvatar), MethodType.Constructor, typeof(APIUser), typeof(bool))]
public static class SomsMapperHoverPatch
{
    static void Prefix(APIUser? user, ref bool showCardOnHover)
    {
        if (SomsClientPreferences.Enabled && SomsMapperProfiles.IsOfficial(user)) showCardOnHover = false;
    }
}

[HarmonyPatch(typeof(OsuGame), nameof(OsuGame.ShowUser))]
public static class SomsMapperOpenProfilePatch
{
    static bool Prefix(OsuGame __instance, IUser user)
    {
        if (!SomsClientPreferences.Enabled || !SomsMapperProfiles.IsOfficial(user)) return true;
        SomsOfficialProfileOverlay.Open(__instance, user);
        return false;
    }
}

[HarmonyPatch]
public static class SomsSongSelectMapperPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(BeatmapTitleWedge.DifficultyDisplay), "updateDisplay");
        yield return AccessTools.Method(typeof(BeatmapMetadataWedge), "updateDisplay");
    }
    static void Postfix(object __instance)
    {
        if (!SomsClientPreferences.Enabled) return;
        var map = (IBindable<WorkingBeatmap>)AccessTools.PropertyGetter(__instance.GetType(), "beatmap").Invoke(__instance, null)!;
        if (map.IsDefault) return;
        var author = SomsMapperProfiles.FromMetadata(map.Value.Metadata.Author);
        var game = ((CompositeDrawable)__instance).Dependencies.Get<OsuGame>();
        Action open = () => game.ShowUser(author);
        if (__instance is BeatmapTitleWedge.DifficultyDisplay)
            ((OsuHoverContainer)AccessTools.Field(__instance.GetType(), "mapperLink").GetValue(__instance)!).Action = open;
        else
            ((BeatmapMetadataWedge.MetadataDisplay)AccessTools.Field(__instance.GetType(), "creator").GetValue(__instance)!).Data = (author.Username, open);
    }
}
