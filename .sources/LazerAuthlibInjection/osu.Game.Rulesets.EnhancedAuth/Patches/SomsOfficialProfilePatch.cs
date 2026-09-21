#nullable enable
using System.Linq;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Online.API;
using osu.Game.Graphics.Containers;
using osu.Game.Overlays;
using osu.Game.Overlays.Profile.Header;
using osu.Game.Overlays.Profile.Header.Components;
using osu.Game.Rulesets.EnhancedAuth.UI;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(APIRequest), "get_Uri")]
public static class SomsOfficialProfileRoutePatch
{
    static bool Prefix(APIRequest __instance, ref string __result)
    {
        if (!SomsOfficialProfileAPI.Routes.TryGetValue(__instance, out var route)) return true;
        var target = (string)AccessTools.PropertyGetter(__instance.GetType(), "Target").Invoke(__instance, null)!;
        __result = route.Origin + "/api/private/official-profiles/" + target;
        return false;
    }
}

[HarmonyPatch(typeof(UserProfileOverlay), "fetchAndSetContent")]
public static class SomsOfficialProfileFetchPatch
{
    static bool Prefix(UserProfileOverlay __instance)
    {
        if (__instance is not SomsOfficialProfileOverlay official) return true;
        official.QueueFetch();
        return false;
    }
}

[HarmonyPatch(typeof(CentreHeaderContainer), "load")]
public static class SomsOfficialProfileActionsPatch
{
    internal static bool IsOfficial(Drawable drawable) =>
        ((IReadOnlyDependencyContainer)AccessTools.PropertyGetter(drawable.GetType(), "Dependencies").Invoke(drawable, null)!)
        .Get<IAPIProvider>() is SomsOfficialProfileAPI;

    static void Postfix(CentreHeaderContainer __instance)
    {
        if (!IsOfficial(__instance)) return;
        var children = (System.Collections.Generic.IReadOnlyList<Drawable>)AccessTools.PropertyGetter(typeof(CompositeDrawable), "InternalChildren").Invoke(__instance, null)!;
        foreach (var flow in children.OfType<FillFlowContainer>())
            foreach (var button in flow.Children.Where(child => child is MessageUserButton or UserActionsButton).ToArray())
                flow.Remove(button, true);
    }
}

[HarmonyPatch(typeof(FollowersButton), "load")]
public static class SomsOfficialProfileFollowersPatch
{
    static void Postfix(FollowersButton __instance)
    {
        if (SomsOfficialProfileActionsPatch.IsOfficial(__instance)) __instance.Action = null;
    }
}

[HarmonyPatch(typeof(TopHeaderContainer), "updateUser")]
public static class SomsOfficialProfileCountryPatch
{
    static void Postfix(TopHeaderContainer __instance, OsuHoverContainer ___userCountryContainer, SupporterIcon ___supporterTag)
    {
        if (!SomsOfficialProfileActionsPatch.IsOfficial(__instance)) return;
        var game = __instance.Dependencies.Get<OsuGame>();
        ___userCountryContainer.Action = () =>
        {
            var profile = __instance.Dependencies.Get<UserProfileOverlay>().Header.User.Value;
            game.OpenUrlExternally($"https://osu.ppy.sh/rankings/{profile?.Ruleset.ShortName ?? "osu"}/performance?country={profile?.User.CountryCode}");
        };
        ___supporterTag.Action = () => game.OpenUrlExternally("https://osu.ppy.sh/home/support");
    }
}
