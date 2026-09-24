using System.Reflection;
using System.Runtime.Loader;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Extensions;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays;
using osu.Game.Overlays.Profile;
using osu.Game.Overlays.Profile.Header;
using osu.Game.Overlays.Profile.Header.Components;
using osu.Game.Overlays.Settings.Sections;
using osu.Game.Rulesets.EnhancedAuth;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Users;
using osu.Game.Users.Drawables;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static class Program
{
    public static int Main(string[] args)
    {
        string client = Path.GetFullPath(args[0]), plugin = Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = name.Name == "osu.Game.Rulesets.EnhancedAuth" ? plugin : Path.Combine(client, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { return Run(Path.GetFullPath(args[2])); }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static int Run(string profile)
    {
        _ = new EnhancedAuthRuleset();
        using var host = new HeadlessGameHost("stealth-check-" + Guid.NewGuid());
        using var game = new CheckGame(profile);
        Exception? error = null;
        host.ExceptionThrown += e => { error = e; Console.Error.WriteLine(e); host.Exit(); return false; };
        using var timeout = new Timer(_ => host.Exit(), null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
        host.Run(game);
        if (error != null) throw error;
        if (!game.Passed) throw new Exception("Stealth lifecycle checks timed out: " + game.Progress);
        return 0;
    }
}

internal sealed partial class CheckGame(string profile) : OsuGameBase
{
    private readonly LocalUserStatisticsProvider stats = new();
    private readonly TestAPI testApi = new();
    private SomsStealthSession session = null!;
    private SpriteText username = null!;
    private DrawableAvatar avatar = null!;
    private DrawableAvatar? freshAvatar;
    private DebugSection debug = null!;
    private UserRankPanel rankPanel = null!;
    private UserRankPanel foreignPanel = null!;
    private readonly osu.Game.Screens.Play.HUD.PlayerFlag gameplayFlag = new();
    private readonly TopHeaderContainer profileHeader = new();
    private readonly MainDetails profileDetails = new();
    private SomsLegacyUserPanel legacy = null!;
    private int step, frames, selected;
    private bool enabledAtStartup;
    private readonly System.Diagnostics.Stopwatch avatarLoadTime = new();
    private Texture? selectedAvatarTexture;
    private GetSomsStealthRequest? stale;
    public bool Passed;
    public string Progress
    {
        get
        {
            var states = typeof(SomsStealthSession).Assembly.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.SomsAvatarPatch")!.GetField("avatars", all)!.GetValue(null)!;
            object?[] values = { avatar, null };
            states.GetType().GetMethod("TryGetValue")!.Invoke(states, values);
            string state = values[1] == null ? "missing" : string.Join(";", values[1]!.GetType().GetFields().Select(f => f.Name + "=" + f.GetValue(values[1])));
            return $"step={step}; status={session?.Status.Value}; busy={session?.Busy.Value}; avatar={avatar?.Texture?.Width}; real-url={testApi.LocalUser.Value.AvatarUrl}; requested={string.Join(",", AvatarTextures.Requests)}; state={state}";
        }
    }
    private const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    protected override Storage CreateStorage(GameHost host, Storage defaultStorage) => host.GetStorage(profile);
    protected override Container CreateScalingContainer() => new Container { RelativeSizeAxes = Axes.Both };
    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        API = testApi;
        var deps = new DependencyContainer(base.CreateChildDependencies(parent));
        GlobalConfigManager.InitializeGameBase(this);
        typeof(GlobalConfigManager).GetField("instance", all)!.SetValue(null, new EnhancedRulesetConfig { ApiUrl = "https://soms.invalid" });
        deps.Cache(stats);
        deps.Cache(new OverlayColourProvider(OverlayColourScheme.Plum));
        return deps;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        typeof(GlobalConfigManager).GetField("instance", all)!.SetValue(null, new EnhancedRulesetConfig { ApiUrl = "https://soms.invalid" });
        Ruleset.Value = RulesetStore.AvailableRulesets.First(r => r.ShortName == "osu");
        enabledAtStartup = SomsClientPreferences.Instance.StealthMode.Value;
        Dependencies.Get<LargeTextureStore>().AddTextureSource(new AvatarTextures());
        avatarLoadTime.Start();
        Require(SomsStealthSession.Current == null, "Session unexpectedly exists before statistics load");
        Add(testApi);
        // Exercise production's patched lifecycle, including asynchronous loading.
        // Never add SomsStealthSession manually here: that hid the Game.AddInternal failure.
        LoadComponentAsync(stats, Add);
        Add(username = new SpriteText { Text = testApi.LocalUser.Value.Username });
        LoadComponentAsync(avatar = new DrawableAvatar(testApi.LocalUser.Value), Add);
        Add(debug = new DebugSection { Width = 800 });
        Add(rankPanel = new UserRankPanel(testApi.LocalUser.Value) { Width = 350 });
        Add(foreignPanel = new UserRankPanel(new APIUser { Id = 2, Username = "OtherPlayer", CountryCode = CountryCode.DE, AvatarUrl = "soms-test-real" }) { Width = 350 });
        Add(gameplayFlag);
        profileHeader.User.Value = profileDetails.User.Value = new UserProfileData(testApi.LocalUser.Value, Ruleset.Value);
        Add(profileHeader);
        Add(profileDetails);
        Add(legacy = new SomsLegacyUserPanel());
    }

    protected override void Update()
    {
        base.Update();
        session ??= SomsStealthSession.Current!;
        if (session?.IsLoaded != true || debug?.IsLoaded != true || rankPanel?.IsLoaded != true || avatar?.IsLoaded != true || ++frames < 35) return;
        frames = 0;
        switch (step)
        {
            case 0:
                if (enabledAtStartup && !session.Active) return;
                Require(avatarLoadTime.Elapsed.TotalSeconds < 8 && !AvatarTextures.Release.IsSet,
                    "Avatar blocked profile/card loading while its image was unavailable");
                Require(Children.OfType<SomsStealthSession>().Count() == 1, "Production bootstrap did not attach exactly one session");
                Console.WriteLine(enabledAtStartup
                    ? "PASS: production bootstrap restores enabled Stealth automatically on restart."
                    : "PASS: production bootstrap attaches Stealth after asynchronous statistics loading.");
                Require(debug.Children.OfType<SomsStealthSettings>().Count() == 1, "Debug subsection missing");
                Require(stats.GetStatisticsFor(Ruleset.Value)?.PP == 3000, "Fixture stats not initialised");
                Add(new LocalUserStatisticsProvider());
                Add(new DebugSection { Width = 800 });
                SomsClientPreferences.Instance.StealthMode.Value = true;
                AvatarTextures.Release.Set();
                break;
            case 1:
                if (!session.Active) return;
                if (session.DisplayCountryRank == null) return;
                if (avatar.Texture?.Width != AvatarTextures.WidthFor(session.Identity!.AvatarUrl)) return;
                selectedAvatarTexture = avatar.Texture;
                Require(ReferenceEquals(session, SomsStealthSession.Current)
                    && Children.OfType<SomsStealthSession>().Count() == 1, "Additional provider/settings duplicated the session");
                selected = session.Identity!.Id;
                Require(username.Text.ToString() == session.Identity.Username, "Existing username did not update");
                Require(testApi.LocalUser.Value.Username == "RealPlayer" && testApi.LocalUser.Value.Id == 1, "Real identity changed");
                Require(testApi.LocalUser.Value.CountryCode == CountryCode.DE, "Stealth mutated the account country");
                CheckCountry();
                Require(((Dictionary<string, UserStatistics>)typeof(LocalUserStatisticsProvider).GetField("statisticsCache", all)!.GetValue(stats)!)["osu"].GlobalRank == 7, "Real stats mutated");
                Require(stats.GetStatisticsFor(Ruleset.Value)!.GlobalRank == 100000, "Display rank missing");
                var fresh = new SpriteText { Text = "RealPlayer" };
                Require(fresh.Text.ToString() == session.Identity.Username, "New username did not get masked");
                LoadComponentAsync(freshAvatar = new DrawableAvatar(testApi.LocalUser.Value), Add);
                Console.WriteLine("PASS: real Debug controls, existing/new username, avatar and native/legacy panels load; identity/cache untouched.");
                testApi.PP = 3100;
                stats.RefetchStatistics(Ruleset.Value);
                break;
            case 2:
                if (session.DisplayRank != 90000) return;
                if (freshAvatar?.IsLoaded != true) return;
                var drawnUser = (IUser)typeof(DrawableAvatar).GetField("user", all)!.GetValue(freshAvatar)!;
                Require(drawnUser.OnlineID == 1, "Avatar changed the account identity used by native controls");
                if (freshAvatar.Texture?.Width != AvatarTextures.WidthFor(session.Identity!.AvatarUrl)) return;
                Require(ReferenceEquals(avatar.Texture, selectedAvatarTexture), "PP-only refresh reloaded an unchanged avatar");
                var presentation = typeof(SomsStealthSession).Assembly.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.SomsStealthPresentation")!;
                var officialProfile = (APIUser)presentation.GetMethod("ProfileUser", all)!.Invoke(null, new object[] { testApi.LocalUser.Value })!;
                Require(officialProfile.Id == selected && officialProfile.Username == session.Identity.Username, "Stealth profile routed to another identity");
                Require(!(bool)presentation.GetMethod("IsLocal", all)!.Invoke(null, new object[] { officialProfile })!, "Official identity confused with the SOMS namespace");
                Require(session.Identity!.Id == selected, "PP gain rerolled identity");
                Require(session.Active, "Mask lost after PP gain");
                Require(session.DisplayCountryRank == 90, "PP gain did not update national rank");
                CheckCountry();
                Console.WriteLine("PASS: PP gain updates rank without reroll.");
                testApi.PP = 3200;
                testApi.Hold = true;
                stats.RefetchStatistics(Ruleset.Value);
                break;
            case 3:
                if (testApi.Held == null) return;
                stale = testApi.Held;
                SomsClientPreferences.Instance.StealthMode.Value = false;
                TestAPI.Complete(stale, TestAPI.Response(1));
                break;
            case 4:
                if (avatar.Texture?.Width != 23) return;
                Require(!session.Active && session.Identity == null, "Late request revived stealth");
                Require(username.Text.ToString() == "RealPlayer", "Original username not restored");
                Require(stats.GetStatisticsFor(Ruleset.Value)!.GlobalRank == 7, "Original rank not restored");
                Require(stats.GetStatisticsFor(Ruleset.Value)!.CountryRank == 3, "Original national rank not restored");
                Require(Flag(rankPanel).CountryCode == CountryCode.DE && Flag(gameplayFlag).CountryCode == CountryCode.DE, "Original country flag not restored");
                Require(Flag(profileHeader).CountryCode == CountryCode.DE, "Profile header flag not restored");
                testApi.Hold = false;
                SomsClientPreferences.Instance.StealthMode.Value = true;
                break;
            case 5:
                if (!session.Active) return;
                Require(session.Identity!.Id != selected, "Re-enable did not reroll");
                selected = session.Identity.Id;
                session.Reroll();
                break;
            case 6:
                if (!session.Active) return;
                if (session.DisplayCountryRank == null) return;
                Require(session.Identity!.Id != selected, "Explicit reroll repeated previous identity despite alternatives");
                CheckCountry();
                selected = session.Identity.Id;
                testApi.Fail = true;
                testApi.PP = 3300;
                stats.RefetchStatistics(Ruleset.Value);
                break;
            case 7:
                if (!session.Status.Value.Contains("не обновлён")) return;
                Require(session.Identity!.Id == selected && session.Active, "Network failure discarded identity");
                Console.WriteLine("PASS: disable restores display; stale replies ignored; toggle/button reroll; failure preserves chosen identity.");
                Require(!File.ReadAllText(Path.Combine(profile, SomsClientPreferences.FileName)).Contains("PlayerA"), "Persona persisted to profile");
                Passed = true;
                Exit();
                break;
        }
        step++;
    }

    private static void Require(bool ok, string error) { if (!ok) throw new Exception(error); }

    private static UpdateableFlag Flag(CompositeDrawable root)
    {
        foreach (Drawable child in (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", all)!.GetValue(root)!)
        {
            if (child is UpdateableFlag flag) return flag;
            if (child is CompositeDrawable container)
            {
                var result = TryFlag(container);
                if (result != null) return result;
            }
        }
        throw new Exception("No country flag in " + root.GetType().Name);
    }

    private static UpdateableFlag? TryFlag(CompositeDrawable root)
    {
        foreach (Drawable child in (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", all)!.GetValue(root)!)
        {
            if (child is UpdateableFlag flag) return flag;
            if (child is CompositeDrawable container && TryFlag(container) is { } found) return found;
        }
        return null;
    }

    private void CheckCountry()
    {
        var expected = Enum.Parse<CountryCode>(session.Identity!.CountryCode);
        Require(Flag(rankPanel).CountryCode == expected && Flag(gameplayFlag).CountryCode == expected, "Card/gameplay flag is not the selected official player's flag");
        Require(Flag(profileHeader).CountryCode == expected, "Profile header flag is stale");
        var countryName = (SpriteText)typeof(TopHeaderContainer).GetField("userCountryText", all)!.GetValue(profileHeader)!;
        Require(countryName.Text.ToString() == expected.GetDescription(), "Profile country name is stale");
        var profileRank = (ProfileValueDisplay)typeof(MainDetails).GetField("detailCountryRank", all)!.GetValue(profileDetails)!;
        Require(profileRank.Content.Text.ToString().Contains(session.DisplayCountryRank!.Value.ToString("N0")), "Profile national rank is blank or stale");
        Require(Flag(foreignPanel).CountryCode == CountryCode.DE, "Stealth changed another player's flag with the same original country");
        Require(stats.GetStatisticsFor(Ruleset.Value)!.CountryRank == session.DisplayCountryRank && session.DisplayCountryRank > 0, "Display statistics lack national rank");
        Require(((Dictionary<string, UserStatistics>)typeof(LocalUserStatisticsProvider).GetField("statisticsCache", all)!.GetValue(stats)!)["osu"].CountryRank == 3, "Real cached country rank mutated");
        var countryDisplay = (osu.Game.Overlays.Profile.Header.Components.ProfileValueDisplay)typeof(UserRankPanel).GetField("countryRankDisplay", all)!.GetValue(rankPanel)!;
        Require(countryDisplay.Content.Text.ToString().Contains(session.DisplayCountryRank!.Value.ToString("N0")), "Native national-rank label is blank or stale");
    }
}

internal sealed partial class TestAPI : DummyAPIAccess
{
    public decimal PP = 3000;
    public bool Hold, Fail;
    public GetSomsStealthRequest? Held;
    public TestAPI() => LocalUser.Value = new APIUser { Id = 1, Username = "RealPlayer", AvatarUrl = "soms-test-real", CountryCode = CountryCode.DE,
        Statistics = new UserStatistics { PP = 3000, GlobalRank = 7, CountryRank = 3 } };
    public override void Queue(APIRequest request)
    {
        request.AttachAPI(this);
        switch (request)
        {
            case GetUserRequest user:
                Complete(user, new APIUser { Id = 1, Username = "RealPlayer", CountryCode = CountryCode.DE, Statistics = new UserStatistics { PP = PP, GlobalRank = 7, CountryRank = 3 } });
                break;
            case GetSomsStealthRequest stealth:
                if (Hold) { Held = stealth; return; }
                if (Fail) { stealth.Fail(new IOException("fixture failure")); return; }
                var response = Response(PP == 3000 ? 100000 : 90000);
                if (Environment.GetEnvironmentVariable("SOMS_TEST_MISSING_COUNTRY_RANK") == "1")
                    foreach (var candidate in response.Candidates) candidate.CountryRank = null;
                if (SomsStealthSession.Current?.Identity is { } identity)
                {
                    string uri = (string)typeof(GetSomsStealthRequest).GetProperty("Uri", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stealth)!;
                    if (!uri.Contains("country=" + identity.CountryCode)) throw new Exception("Rank refresh omitted the selected country");
                    response.CountryCode = identity.CountryCode;
                    response.CountryRank = PP == 3000 ? 100 : 90;
                }
                Complete(stealth, response);
                break;
        }
    }
    public static void Complete<T>(APIRequest<T> request, T value) where T : class =>
        ((Delegate?)typeof(APIRequest<T>).GetField("Success", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(request))?.DynamicInvoke(value);
    public static SomsStealthResponse Response(int rank) => new()
    {
        GlobalRank = rank, Estimated = true,
        Candidates = new()
        {
            new() { Id = 100, Username = "PlayerA", AvatarUrl = "soms-test-a", PP = 3000, GlobalRank = 100000, CountryCode = "FI", CountryRank = 100 },
            new() { Id = 101, Username = "PlayerB", AvatarUrl = "soms-test-b", PP = 3000, GlobalRank = 100000, CountryCode = "NZ", CountryRank = 200 },
        },
    };
}

internal sealed class AvatarTextures : ResourceStore<TextureUpload>
{
    internal static readonly System.Collections.Concurrent.ConcurrentQueue<string> Requests = new();
    internal static readonly ManualResetEventSlim Release = new(false);
    internal static int WidthFor(string name) => name switch { "soms-test-real" => 23, "soms-test-a" => 31, "soms-test-b" => 37, _ => 0 };
    public override TextureUpload Get(string name)
    {
        int width = WidthFor(name);
        if (width == 0) return null!;
        Requests.Enqueue(name);
        Release.Wait(TimeSpan.FromSeconds(15));
        return new TextureUpload(new Image<Rgba32>(width, 7));
    }
}
