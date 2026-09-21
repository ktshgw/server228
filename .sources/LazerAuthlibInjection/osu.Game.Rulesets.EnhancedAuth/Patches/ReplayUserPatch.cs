#nullable enable
using HarmonyLib;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Scoring;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// LegacyScoreDecoder assigns User before it reads lazer's appended metadata.
// The later RealmUser ID update otherwise leaves the cached APIUser as a guest.
[HarmonyPatch(typeof(ScoreInfo), nameof(ScoreInfo.User), MethodType.Getter)]
public static class ReplayUserPatch
{
    static void Postfix(ScoreInfo __instance, APIUser __result)
    {
        if (!GlobalConfigManager.Patched || GlobalConfigManager.Config.NonG0V0Server)
            return;

        if (__instance.RealmUser.OnlineID > 1 && __result.Id != __instance.RealmUser.OnlineID)
        {
            __result.Id = __instance.RealmUser.OnlineID;
            // An avatar cached for a former identity must not follow a new ID.
            __result.AvatarUrl = null;
        }
    }
}
