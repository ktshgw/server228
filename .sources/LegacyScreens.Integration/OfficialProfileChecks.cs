using System.Reflection;
using Newtonsoft.Json;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays;
using osu.Game.Overlays.Profile.Header.Components;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.EnhancedAuth.UI;

internal sealed partial class IntegrationGame
{
    private int profilePhase;
    private bool failProfile;
    private readonly List<string> profileRoutes = new();
    private bool songMapperClicked;
    private bool songMapperChecked;
    private APIUser ProfileUser(int id = 2)
    {
        var result = JsonConvert.DeserializeObject<APIUser>(File.ReadAllText(Environment.GetEnvironmentVariable("SOMS_PROFILE_FIXTURE")!))!;
        result.Id = id;
        return result;
    }

    private bool CheckSongSelectProfile()
    {
        if (Environment.GetEnvironmentVariable("SOMS_PROFILE_FIXTURE") == null || songMapperChecked) return true;
        var official = descendants(this).OfType<SomsOfficialProfileOverlay>().Single();
        if (frames % 600 == 0) Console.WriteLine($"MAPPER clicked={songMapperClicked}, profile={official.State.Value}/{official.Header.User.Value?.User.Id}, routes={profileRoutes.LastOrDefault()}, displays={descendants(selection!).OfType<osu.Game.Screens.Select.BeatmapTitleWedge.DifficultyDisplay>().Count()}");
        if (!songMapperClicked)
        {
            var difficulty = descendants(selection!).OfType<osu.Game.Screens.Select.BeatmapTitleWedge.DifficultyDisplay>().FirstOrDefault();
            if (difficulty?.IsLoaded != true) return false;
            var link = member<osu.Game.Graphics.Containers.OsuHoverContainer>(difficulty, "mapperLink")!;
            Console.WriteLine($"MAPPER action={link.Action?.Method.DeclaringType}, enabled={osu.Game.Rulesets.EnhancedAuth.Configuration.SomsClientPreferences.Enabled}, default={Beatmap.IsDefault}, author={Beatmap.Value.Metadata.Author.OnlineID}/{Beatmap.Value.Metadata.Author.Username}, linkHandler={member<osu.Game.Online.ILinkHandler>(difficulty, "linkHandler")?.GetType().Name}");
            link.Action!();
            songMapperClicked = true;
            return false;
        }
        if (official.State.Value != Visibility.Visible || official.Header.User.Value?.User.Id != 2) return false;
        require(profileRoutes.Any(route => Uri.UnescapeDataString(route).Contains("users/Integration fixture/") && route.Contains("key=username")), "Song-select creator with ID=1 must be requested by username");
        official.Hide();
        songMapperChecked = true;
        Console.WriteLine("PASS actual native song-select mapper click resolves imported creator by username");
        return true;
    }

    private bool CheckOfficialProfile()
    {
        if (Environment.GetEnvironmentVariable("SOMS_PROFILE_FIXTURE") == null) return true;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var official = descendants(this).OfType<SomsOfficialProfileOverlay>().FirstOrDefault();
        if (frames % 600 == 0) Console.WriteLine($"PROFILE phase={profilePhase}, header={official?.Header.User.Value?.User.Id}, routes={profileRoutes.Count}, loaded={official?.IsLoaded}");
        switch (profilePhase)
        {
            case 0:
                ((DummyAPIAccess)API).HandleRequest = req =>
                {
                    var uri = (string)typeof(APIRequest).GetProperty("Uri", flags)!.GetValue(req)!;
                    if (!uri.Contains("/official-profiles/")) return false;
                    profileRoutes.Add(uri);
                    require(!uri.StartsWith("https://osu.ppy.sh"), "Private authentication must not be sent to official transport");
                    if (failProfile && req is GetUserRequest) { req.Fail(new IOException("Simulated upstream failure")); return true; }
                    Type? generic = req.GetType();
                    while (generic != null && (!generic.IsGenericType || generic.GetGenericTypeDefinition() != typeof(APIRequest<>))) generic = generic.BaseType;
                    var responseType = generic!.GetGenericArguments()[0];
                    object response = req is GetUserRequest user ? ProfileUser(int.TryParse(user.Lookup, out int id) ? id : 2)
                        : Activator.CreateInstance(responseType)!;
                    generic.GetMethod("TriggerSuccess", flags, null, new[] { responseType }, null)!.Invoke(req, new[] { response });
                    return true;
                };
                var author = ProfileUser();
                typeof(SomsMapperProfiles).GetMethod("Mark", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { author });
                ShowUser(author);
                profilePhase++;
                return false;
            case 1:
                if (official?.Header.User.Value?.User.Id != 2) return false;
                var followers = descendants(official).OfType<FollowersButton>().FirstOrDefault();
                var selector = descendants(official).OfType<ProfileRulesetSelector>().FirstOrDefault();
                if (followers?.IsLoaded != true || selector?.IsLoaded != true) return false;
                require(followers.Action == null, "Official follower count must be read-only");
                require(!descendants(official).Any(d => d is MessageUserButton or UserActionsButton), "Private chat/moderation buttons must not target an official ID");
                require(member<UserProfileOverlay>(this, "userProfile") != official, "Private profile overlay must remain separate");
                selector.Current.Value = RulesetStore.GetRuleset(1)!;
                profilePhase++;
                return false;
            case 2:
                if (official?.Header.User.Value?.Ruleset.OnlineID != 1) return false;
                require(profileRoutes.Any(route => route.Contains("users/2/taiko")), "Mode selector must retain the official provider");
                failProfile = true;
                official.ShowUser(ProfileUser(404));
                profilePhase++;
                return false;
            case 3:
                var retry = official == null ? null : descendants(official).OfType<FormButton>().FirstOrDefault(d => d.Name == "soms-official-profile-retry");
                if (retry?.IsLoaded != true || retry.Alpha == 0) return false;
                failProfile = false;
                retry.Action!();
                profilePhase++;
                return false;
            case 4:
                if (official?.Header.User.Value?.User.Id != 404) return false;
                official.Hide();
                profilePhase++;
                Console.WriteLine("PASS real native official profile, isolated provider, ruleset switch, read-only social actions, error and retry");
                return true;
            default: return true;
        }
    }
}
