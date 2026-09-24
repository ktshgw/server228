using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.Scoring;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using osu.Framework;
using osu.Framework.Configuration;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.StateChanges;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game;
using osu.Game.Configuration;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.Cursor;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Leaderboards;
using osu.Game.Online.Rooms;
using osu.Game.Online.Solo;
using osu.Game.Screens.OnlinePlay.Multiplayer;
using osu.Game.Screens.Ranking;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.EnhancedAuth;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens;
using osu.Game.Screens.Menu;
using osu.Game.Screens.Play;
using osu.Game.Screens.Select;
using osu.Game.Users;
using osuTK;
using osuTK.Input;

internal static class Program
{
    public static int Main(string[] args) => QuietTestProcess.Run(() => MainImpl(args));
    private static int MainImpl(string[] args)
    {
        string client = Path.GetFullPath(args[0]), plugin = Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = name.Name == "osu.Game.Rulesets.EnhancedAuth" ? plugin : Path.Combine(client, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        foreach (string mode in new[] { "Osu", "Taiko", "Catch", "Mania" })
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(client, "osu.Game.Rulesets." + mode + ".dll"));
        try { return Run(Path.GetFullPath(args[2]), args.Contains("--visual")); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int Run(string output, bool visual)
    {
        Directory.CreateDirectory(output);
        string profile = Path.Combine(output, "profiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, EnhancedRulesetConfig.CONFIG_FILE_NAME),
            Newtonsoft.Json.JsonConvert.SerializeObject(new EnhancedRulesetConfig { ApiUrl = "https://replay-duel.invalid" }));
        string archive = CreateMap(output);
        using GameHost host = visual ? Host.GetSuitableDesktopHost("replay-duel-" + Guid.NewGuid().ToString("N"), new HostOptions { PortableInstallation = true, FriendlyGameName = "SOMS replay duel preview" })
            : new HeadlessGameHost("replay-duel-" + Guid.NewGuid().ToString("N"), realtime: false);
        using var game = new ResultsGame(profile, archive, output, visual);
        Exception? failure = null;
        host.ExceptionThrown += e => { failure = e; Console.Error.WriteLine(e); host.Exit(); return true; };
        using var timer = new Timer(_ => { Console.Error.WriteLine("TIMEOUT " + game.Stage); host.Exit(); }, null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        try { host.Run(game); } catch (Exception e) { Console.Error.WriteLine("HOST: " + e); throw; }
        if (failure != null) throw failure;
        if (!game.Passed) throw new Exception("Duel did not finish: " + game.Stage);
        return 0;
    }

    private static string CreateMap(string output)
    {
        string path = Path.Combine(output, "duel-" + Guid.NewGuid().ToString("N") + ".osz");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var audio = new BinaryWriter(zip.CreateEntry("silence.wav").Open()))
        {
            const int length = 44100 * 10 * 2;
            audio.Write("RIFF"u8); audio.Write(length + 36); audio.Write("WAVEfmt "u8); audio.Write(16);
            audio.Write((short)1); audio.Write((short)1); audio.Write(44100); audio.Write(88200); audio.Write((short)2); audio.Write((short)16);
            audio.Write("data"u8); audio.Write(length); audio.Write(new byte[length]);
        }
        using var map = new StreamWriter(zip.CreateEntry("duel.osu").Open());
        map.Write("""
            osu file format v14
            [General]
            AudioFilename: silence.wav
            Mode: 0
            Countdown: 0
            [Metadata]
            Title:Replay duel integration
            Artist:SOMS
            Creator:Test fixture
            Version:Circles sliders spinner
            BeatmapID:424242
            BeatmapSetID:4242
            [Difficulty]
            HPDrainRate:2
            CircleSize:4
            OverallDifficulty:4
            ApproachRate:4
            SliderMultiplier:1.4
            SliderTickRate:1
            [TimingPoints]
            0,500,4,2,1,50,1,0
            [HitObjects]
            160,192,2000,1,0,0:0:0:0:
            256,192,2500,1,0,0:0:0:0:
            352,192,3000,1,0,0:0:0:0:
            256,120,3500,2,0,L|400:120,1,140
            352,192,4500,1,0,0:0:0:0:
            256,192,5000,8,0,6000
            160,192,6500,1,0,0:0:0:0:
            256,192,7000,1,0,0:0:0:0:
            """);
        return path;
    }
}


internal sealed partial class ResultsGame : OsuGame
{
    private readonly string profile, archive, output;
    private readonly bool visual;
    private Task? imported, capture;
    private int stage, frames, polls, outcome;
    private double shownAt;
    private bool confirmed;
    private SomsAiMatch match = null!;
    private SomsAiMultiplayerResultsScreen results = null!;
    public bool Passed { get; private set; }
    public string Stage => $"{stage}, outcome={outcome}, polls={polls}";
    public ResultsGame(string profile, string archive, string output, bool visual) : base(Array.Empty<string>())
    {
        this.profile = profile; this.archive = archive; this.output = output; this.visual = visual;
        API = new DummyAPIAccess(); API.LocalUser.Value.Id = 42; API.LocalUser.Value.Username = "Player";
        ((DummyAPIAccess)API).HandleRequest = request =>
        {
            if (request is GetSomsAiStateRequest state)
            {
                polls++;
                var snapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<SomsAiMatch>(Newtonsoft.Json.JsonConvert.SerializeObject(match))!;
                if (polls == 1) { snapshot.Stage = "results"; snapshot.History.Clear(); snapshot.WinnerTeamId = null; }
                typeof(APIRequest<SomsAiState>).GetMethod("TriggerSuccess", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, new[] { typeof(SomsAiState) })!
                    .Invoke(state, new object[] { new SomsAiState { Match = snapshot } });
                return true;
            }
            if (request is IndexPlaylistScoresRequest || request is ShowPlaylistScoreRequest) throw new Exception("Offline fixture must not contact a real room");
            return false;
        };
    }
    protected override int UnhandledExceptionsBeforeCrash => 0;
    protected override Storage CreateStorage(GameHost host, Storage defaultStorage) => host.GetStorage(profile);
    protected override Loader CreateLoader() => new DirectLoader();
    protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults() => new Dictionary<FrameworkSetting, object>
    {
        [FrameworkSetting.WindowMode] = WindowMode.Windowed, [FrameworkSetting.WindowedSize] = new System.Drawing.Size(1280, 720),
        [FrameworkSetting.VolumeUniversal] = 0.0, [FrameworkSetting.VolumeMusic] = 0.0, [FrameworkSetting.VolumeEffect] = 0.0,
    };
    protected override void LoadComplete()
    {
        GlobalConfigManager.InitializeGameBase(this);
        typeof(GlobalConfigManager).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null,
            new EnhancedRulesetConfig { ApiUrl = "https://results.invalid" });
        _ = new EnhancedAuthRuleset();
        SessionStatics.SetValue(Enum.Parse<Static>("LoginOverlayDisplayed"), true);
        LocalConfig.SetValue(Enum.Parse<OsuSetting>("ShowFirstRunSetup"), false);
        base.LoadComplete();
        if (Audio.Volume.Value != 0 || Audio.VolumeSample.Value != 0 || Audio.VolumeTrack.Value != 0) throw new Exception("Audio enabled");
        if (visual) { Host.Window.Hide(); var active = (Bindable<bool>)Host.IsActive; active.UnbindAll(); active.Value = true; }
        Add((DummyAPIAccess)API);
        Ruleset.Value = RulesetStore.GetRuleset(0)!;
        imported = BeatmapManager.Import(archive);
    }
    protected override void Update()
    {
        if (visual) ((Bindable<bool>)IsActive).Value = true;
        base.Update();
        if (capture != null && !capture.IsCompleted) return;
        capture?.GetAwaiter().GetResult(); capture = null;
        if (++frames % 10 != 0) return;
        if (stage == 0)
        {
            if (imported?.IsCompleted != true || ScreenStack.CurrentScreen is not MainMenu) return;
            imported.GetAwaiter().GetResult();
            var map = BeatmapManager.QueryOnlineBeatmapId(424242)!;
            Beatmap.Value = BeatmapManager.GetWorkingBeatmap(map);
            begin(); stage = 1;
        }
        else if (stage == 1)
        {
            if (!results.IsLoaded) return;
            if (polls == 1 && ChildrenOf(results).Any(d => d is SomsAiOutcomeAnimation)) throw new Exception("Premature outcome before server settlement");
            if (!confirmed) return;
            if (shownAt == 0) { shownAt = Clock.CurrentTime; Capture("outcome-" + outcome); return; }
            if (Clock.CurrentTime - shownAt < 7000) return;
            var panels = ChildrenOf(results).OfType<ScorePanel>().ToArray();
            if (panels.Length != 2) throw new Exception("Expected human and bot native panels; got " + panels.Length);
            var bot = panels.Single(p => p.Score.UserID == 99).Score;
            if (bot.TotalScore != 800000 || bot.MaxCombo != 10 || bot.Statistics[HitResult.Great] != 5 || bot.Statistics[HitResult.Ok] != 2) throw new Exception("Bot statistics differ from authoritative simulation");
            if (results.Score!.Position != (outcome == 0 ? 1 : 2)) throw new Exception("Wrong native leaderboard position");
            if (polls != 2) throw new Exception("Expected confirmation poll after incomplete round");
            Console.WriteLine("PASS native multiplayer results with two panels; no winner before confirmed round; outcome=" + outcome);
            Capture("panels-" + outcome);
            stage = 2;
        }
        else if (stage == 2)
        {
            if (outcome++ == 0) { results.Exit(); stage = 3; }
            else { Console.WriteLine("PASS SOMSAI victory and defeat, real bot statistics, silent hidden client"); Passed = true; Host.Exit(); }
        }
        else if (stage == 3 && ScreenStack.CurrentScreen is MainMenu) { begin(); stage = 1; }
    }
    private void begin()
    {
        shownAt = 0; polls = 0; confirmed = false;
        var map = Beatmap.Value.BeatmapInfo;
        var score = new ScoreInfo(map, Ruleset.Value) { User = API.LocalUser.Value, TotalScore = outcome == 0 ? 950000 : 600000,
            Accuracy = .98, MaxCombo = 12, Passed = true, Rank = ScoreRank.A, Date = DateTimeOffset.UtcNow, BeatmapHash = map.Hash,
            Statistics = new() { [HitResult.Great] = 6, [HitResult.Ok] = 1 }, MaximumStatistics = new() { [HitResult.Great] = 7 } };
        match = new SomsAiMatch { Id = 55, RoomId = 66, Stage = "ended", WinnerTeamId = outcome == 0 ? 1 : 0,
            Wins = outcome == 0 ? new[] { 1, 4 } : new[] { 4, 1 }, Teams = new()
            {
                new() { Id = 0, Members = new() { new() { Id = 99, Username = "Bot rival", IsBot = true } } },
                new() { Id = 1, Members = new() { new() { Id = 42, Username = "Player" } } },
            }, History = new() { JObject.Parse("""{"round":5,"playlist_item_id":77,"winner_team_id":0,"players":[{"user_id":99,"score":800000,"accuracy":0.97,"max_combo":10,"passed":true,"rank":"A","is_bot":true,"statistics":{"Great":5,"Ok":2},"maximum_statistics":{"Great":7}}]}""") } };
        results = new SomsAiMultiplayerResultsScreen(score, 66, new PlaylistItem(map) { ID = 77, RulesetID = 0 }, match,
            settled => { if (!settled.IsFinished) throw new Exception("Not settled"); confirmed = true; }) { IsLocalPlay = true };
        ScreenStack.Push(results);
    }
    private void Capture(string name)
    {
        if (visual) capture = Host.TakeScreenshotAsync().ContinueWith(t => SixLabors.ImageSharp.ImageExtensions.SaveAsPng(t.Result, Path.Combine(output, name + ".png")));
    }
    private static IEnumerable<Drawable> ChildrenOf(CompositeDrawable root)
    {
        var children = (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
        foreach (var child in children) { yield return child; if (child is CompositeDrawable c) foreach (var nested in ChildrenOf(c)) yield return nested; }
    }
    private sealed partial class DirectLoader : Loader { protected override OsuScreen CreateLoadableScreen() => new MainMenu(); }
}
