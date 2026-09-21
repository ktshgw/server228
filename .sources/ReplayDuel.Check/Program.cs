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
        using var game = new DuelGame(profile, archive, output, visual);
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

internal sealed partial class DuelGame : OsuGame
{
    private readonly string profile, archive, output;
    private readonly bool visual;
    private Task? imported, capture;
    private int stage, round, frames, downloadCount;
    private double pausedTime;
    private long pausedScore;
    private double introCaptureTime;
    private bool pauseVerified;
    private int submittedCount, tokenCount, inputPhase;
    private double resultsTime;
    private SoloScoreInfo? submitted;
    private APIUser rival = new() { Id = 12345, Username = "Replay rival", Statistics = new UserStatistics { GlobalRank = 5432, PP = 7654 } };
    private Menu.DrawableMenuItem? duelMenuItem;
    private byte[] replayBytes = Array.Empty<byte>();
    private SomsReplayDuelScreen? duel;
    private SomsReplayDuelPlayer? player;
    private ScoreInfo selectedFixture = null!;
    private BeatmapLeaderboardScore? scoreRow;
    private TestInput? testInput;
    public bool Passed { get; private set; }
    public string Stage => $"stage={stage}, round={round}, screen={ScreenStack?.CurrentScreen?.GetType().Name}";

    public DuelGame(string profile, string archive, string output, bool visual) : base(Array.Empty<string>())
    {
        this.profile = profile; this.archive = archive; this.output = output; this.visual = visual;
        API = new DummyAPIAccess();
        API.LocalUser.Value.Username = "Player";
        ((DummyAPIAccess)API).HandleRequest = request =>
        {
            if (request is CreateSoloScoreRequest token)
            {
                tokenCount++;
                typeof(APIRequest<APIScoreToken>).GetMethod("TriggerSuccess", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, new[] { typeof(APIScoreToken) })!.Invoke(token, new object[] { new APIScoreToken { ID = 123000 + tokenCount } });
                return true;
            }
            if (request is SubmitSoloScoreRequest submit)
            {
                submitted = submit.Score; submittedCount++;
                var human = (Score)typeof(Player).GetProperty("Score", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(player)!;
                Require(human.ScoreInfo.UserID == API.LocalUser.Value.Id && submit.Score.TotalScore == human.ScoreInfo.TotalScore && submit.Score.TotalScore > 0, "only human nonzero score submitted");
                typeof(APIRequest<MultiplayerScore>).GetMethod("TriggerSuccess", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, new[] { typeof(MultiplayerScore) })!.Invoke(submit, new object[] { new MultiplayerScore { ID = 333000 + submittedCount } });
                return true;
            }
            if (request is IndexPlaylistScoresRequest || request is ShowPlaylistScoreRequest)
                throw new Exception("Replay duel must not request a fake multiplayer room");
            if (request is GetUserRequest user)
            {
                var response = user.Lookup == rival.Id.ToString() ? rival : new APIUser
                {
                    Id = API.LocalUser.Value.Id, Username = "Player", Statistics = new UserStatistics { GlobalRank = 1234, PP = 12345 },
                };
                typeof(APIRequest<APIUser>).GetMethod("TriggerSuccess", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, new[] { typeof(APIUser) })!.Invoke(user, new object[] { response });
                return true;
            }
            if (request is DownloadReplayRequest download)
            {
                downloadCount++;
                string path = Path.Combine(output, "download-" + Guid.NewGuid().ToString("N") + ".osr");
                File.WriteAllBytes(path, replayBytes);
                typeof(APIDownloadRequest).GetMethod("TriggerSuccess", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(string) })!.Invoke(download, new object[] { path });
                return true;
            }
            return false;
        };
    }
    protected override int UnhandledExceptionsBeforeCrash => 0;
    protected override Storage CreateStorage(GameHost host, Storage defaultStorage) => host.GetStorage(profile);
    protected override Loader CreateLoader() => new DirectLoader();
    protected override UserInputManager CreateUserInputManager() => testInput = new TestInput();
    protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults() => new Dictionary<FrameworkSetting, object>
    {
        [FrameworkSetting.WindowMode] = WindowMode.Windowed, [FrameworkSetting.WindowedSize] = new System.Drawing.Size(1280, 720),
        [FrameworkSetting.VolumeUniversal] = 0.0, [FrameworkSetting.VolumeMusic] = 0.0, [FrameworkSetting.VolumeEffect] = 0.0,
    };
    protected override void LoadComplete()
    {
        GlobalConfigManager.InitializeGameBase(this);
        typeof(GlobalConfigManager).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null,
            new EnhancedRulesetConfig { ApiUrl = "https://replay-duel.invalid" });
        _ = new EnhancedAuthRuleset();
        SomsClientPreferences.Instance.LegacyInterface.Value = false;
        Require(SomsReplayDuelPatch.Enabled, "normal interface enables duel");
        SomsClientPreferences.Instance.LegacyInterface.Value = true;
        Require(!SomsReplayDuelPatch.Enabled, "legacy disables duel");
        SomsClientPreferences.Instance.LegacyInterface.Value = false;
        SessionStatics.SetValue(Enum.Parse<Static>("LoginOverlayDisplayed"), true);
        LocalConfig.SetValue(Enum.Parse<OsuSetting>("ShowFirstRunSetup"), false);
        base.LoadComplete();
        Require(Audio.Volume.Value == 0 && Audio.VolumeSample.Value == 0 && Audio.VolumeTrack.Value == 0, "test audio must be silent");
        Console.WriteLine("PASS test music, effects and master volume are muted");
        if (visual)
        {
            Host.Window.Hide();
            var active = (Bindable<bool>)Host.IsActive;
            active.UnbindAll();
            active.Value = true;
        }
        Add((DummyAPIAccess)API);
        Ruleset.Value = RulesetStore.GetRuleset(0)!;
        imported = BeatmapManager.Import(archive);
    }

    protected override void Update()
    {
        // The preview stays hidden and must not steal focus from the user's apps.
        if (visual)
        {
            ((Bindable<bool>)IsActive).Value = true;
            // SDL marks a hidden window's cursor outside. Our isolated input
            // handler sends synthetic mouse events; keep hover/click routing on.
            ((Bindable<bool>)Host.Window.CursorInWindow).Value = true;
        }
        base.Update();
        if (player?.IsLoaded == true && ScreenStack.CurrentScreen == player && round < 2)
        {
            var gameplayClock = (GameplayClockContainer)typeof(Player).GetProperty("GameplayClockContainer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
            if (inputPhase == 0 && gameplayClock.CurrentTime >= 1985 && gameplayClock.CurrentTime < 2100)
            {
                var drawable = (DrawableRuleset)typeof(Player).GetProperty("DrawableRuleset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(player)!;
                var circle = ChildrenOf(drawable).OfType<DrawableHitObject>().First(d => d.HitObject.StartTime == 2000 && d.GetType().Name == "DrawableHitCircle");
                testInput!.Send(new MousePositionAbsoluteInput { Position = circle.ToScreenSpace(circle.DrawSize / 2) });
                testInput.Send(new KeyboardKeyInput(Key.Z, true)); inputPhase = 1;
            }
            if (inputPhase == 1 && gameplayClock.CurrentTime > 2150)
            { testInput!.Send(new KeyboardKeyInput(Key.Z, false)); inputPhase = 2; }
        }
        if (capture != null && !capture.IsCompleted) return;
        capture?.GetAwaiter().GetResult(); capture = null;
        if (Passed || ++frames < 20) return;
        frames = 0;
        switch (stage)
        {
            case 0:
                if (imported?.IsCompleted != true || ScreenStack?.CurrentScreen is not MainMenu { IsLoaded: true }) return;
                imported.GetAwaiter().GetResult();
                Beatmap.Value = BeatmapManager.GetWorkingBeatmap(BeatmapManager.GetAllUsableBeatmapSets().First().Beatmaps.First());
                CloseAllOverlays();
                BeginRound();
                stage = 1;
                break;
            case 1:
                if (duel?.IsLoaded != true) return;
                var labels = ChildrenOf(duel).OfType<OsuSpriteText>().Select(t => t.Text.ToString()).ToArray();
                if (!labels.Contains(SomsReplayDuelScreen.PerformanceLabel(rival))) return;
                Require(labels.Contains($"{12345:N0} PP") && labels.Count(t => t.EndsWith(" PP")) == 2
                    && !labels.Any(t => t.StartsWith("Rating:") || t.StartsWith("Мировой ранг")), "native VS has current player PP and no rank/Elo");
                Console.WriteLine("PASS native VS displays both players' PP " + round);
                if (visual)
                {
                    if (introCaptureTime == 0) introCaptureTime = Clock.CurrentTime + 1500;
                    if (Clock.CurrentTime < introCaptureTime) return;
                }
                Capture("intro-" + round);
                stage = 2;
                break;
            case 2:
                if (ScreenStack.CurrentScreen is not SomsReplayDuelPlayer { IsLoaded: true } active) return;
                player = active;
                Require(player.LoadedBeatmapSuccessfully, "human player loaded");
                if (!ChildrenOf(player).OfType<SomsReplayOpponent>().Single().IsLoaded) return;
                var simulation = ChildrenOf(player).OfType<SomsReplayOpponent>().Single();
                Require(simulation.Alpha == 0 && simulation.AlwaysPresent, "replay simulation updates without drawing");
                Require(!simulation.PropagatePositionalInputSubTree && !simulation.PropagateNonPositionalInputSubTree, "simulation cannot intercept human input");
                Require(!ChildrenOf(player).Any(d => d.Name == "soms-replay-duel-spectator"), "no replay spectator viewport");
                testInput!.Send(new MousePositionAbsoluteInput { Position = player.ToScreenSpace(player.DrawSize / 2) });
                Console.WriteLine("PASS real player and opponent loaded " + round);
                if (round == 2)
                {
                    player.Exit();
                    stage = 10;
                    return;
                }
                stage = 3;
                break;
            case 3:
                var clock = (GameplayClockContainer)typeof(Player).GetProperty("GameplayClockContainer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
                var opponent = ChildrenOf(player!).OfType<SomsReplayOpponent>().Single();
                if (!pauseVerified && clock.CurrentTime > 3100 && clock.CurrentTime < 6000)
                {
                    if (pausedTime == 0)
                    {
                        var humanRuleset = (DrawableRuleset)typeof(Player).GetProperty("DrawableRuleset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(player)!;
                        var globalCursor = ChildrenOf(this).OfType<GlobalCursorDisplay>().Single();
                        var provider = typeof(GlobalCursorDisplay).GetField("currentOverrideProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(globalCursor);
                        Require(ReferenceEquals(provider, humanRuleset), "human gameplay ruleset owns the cursor, actual=" + provider?.GetType().Name);
                        Require(humanRuleset.Cursor.State.Value == Visibility.Visible && globalCursor.MenuCursor.State.Value == Visibility.Hidden, "only gameplay cursor is shown during play");
                        Console.WriteLine("PASS human gameplay cursor and invisible replay simulation " + round);
                        clock.Stop(); pausedTime = clock.CurrentTime; pausedScore = opponent.Processor.TotalScore.Value;
                        Require(pausedScore > 0 && pausedScore < 1000000, "opponent score grows from real replay hits");
                        foreach (var board in ChildrenOf(player!).OfType<osu.Game.Screens.Play.HUD.DrawableGameplayLeaderboard>())
                        {
                            var expanded = (Bindable<bool>)typeof(osu.Game.Screens.Play.HUD.DrawableGameplayLeaderboard).GetField("expanded", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(board)!;
                            Require(expanded.Value, "duel leaderboard keeps live scores expanded");
                        }
                        Capture("gameplay-" + round);
                        return;
                    }
                    Require(Math.Abs(clock.CurrentTime - pausedTime) < 1, "paused map clock stays still");
                    Require(opponent.Processor.TotalScore.Value == pausedScore, "pause freezes replay score");
                    clock.Start(); pauseVerified = true;
                    Console.WriteLine("PASS synchronized pause and progressive score " + round);
                }
                if (ScreenStack.CurrentScreen is not SomsReplayDuelResultsScreen { IsLoaded: true } result) return;
                Require(pauseVerified, "pause was exercised");
                var panels = ChildrenOf(result).OfType<ScorePanel>().ToArray();
                if (panels.Length != 2 || panels.Any(p => !p.IsLoaded)) return;
                Require(result is MultiplayerResultsScreen && result.IsLocalPlay, "actual native multiplayer result screen");
                Require(result.Score!.Position == 2 && panels.Single(p => p.Score.UserID == rival.Id).Score.Position == 1, "native multiplayer positions compare human and replay");
                Require(result.Score.OnlineID == 333000 + submittedCount && submittedCount == round + 1, "human score submitted exactly once before results");
                Require(result.Score.Files.Count > 0 && result.Score.HitEvents.Count > 0, "human replay and statistics imported locally");
                Require(submitted!.Mods.Select(m => m.Acronym).Order().SequenceEqual(result.Score.Mods.Select(m => m.Acronym).Order()), "submitted mods match actual play");
                if (round == 1) Require(submitted.Passed, "passing DT NF result submitted as passed");
                if (resultsTime == 0) resultsTime = Clock.CurrentTime;
                if (Clock.CurrentTime - resultsTime < 9500) return;
                Require(opponent.Processor.Accuracy.Value == 1 && opponent.Processor.HasCompleted.Value, "native replay reaches 100% including slider and spinner");
                Require(downloadCount == round + 1, "one download per match");
                Console.WriteLine("PASS native multiplayer results, two score panels, local human replay and exactly one solo submission; human=" + result.Score.TotalScore + " passed=" + submitted.Passed);
                Capture("result-" + round);
                stage = 4;
                break;
            case 4:
                ((OsuScreen)ScreenStack.CurrentScreen).Exit();
                stage = 5;
                break;
            case 5:
                if (ScreenStack.CurrentScreen is not MainMenu && ScreenStack.CurrentScreen is not SoloSongSelect) return;
                Require(((OsuScreen)ScreenStack.CurrentScreen).Mods.Value.Count == 0, "leaving duel restores original mods");
                if (++round < 3) { BeginRound(); stage = 1; return; }
                Require(!Directory.EnumerateFiles(output, "download-*.osr").Any(), "download temporary files cleaned");
                Require(submittedCount == 2, "aborted zero-hit round never submits and replay opponent never submits");
                Console.WriteLine("PASS all replay duel checks, native multiplayer results, human-only submission, abort, restored mods");
                Passed = true; Host.Exit();
                break;
            case 6:
                if (ScreenStack.CurrentScreen is not SoloSongSelect { IsLoaded: true }) return;
                ((Bindable<LeaderboardScores?>)typeof(LeaderboardManager).GetField("scores", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(LeaderboardManager)!).Value
                    = LeaderboardScores.Success(new[] { selectedFixture }, 50, 1, null);
                stage = 7;
                break;
            case 7:
                scoreRow = ChildrenOf((SoloSongSelect)ScreenStack.CurrentScreen).OfType<BeatmapLeaderboardScore>().FirstOrDefault(r => r.Score.OnlineID == selectedFixture.OnlineID && r.IsLoaded && r.IsPresent);
                if (scoreRow == null || scoreRow.DrawWidth < 20) return;
                SomsClientPreferences.Instance.LegacyInterface.Value = true;
                var originalMenu = ((IHasContextMenu)scoreRow).ContextMenuItems!.Select(i => i.Text.Value.ToString()).ToArray();
                Require(!originalMenu.Contains("Сыграть 1 на 1 с реплеем"), "legacy has only native score actions");
                SomsClientPreferences.Instance.LegacyInterface.Value = false;
                var extendedMenu = ((IHasContextMenu)scoreRow).ContextMenuItems.Select(i => i.Text.Value.ToString()).ToArray();
                Require(originalMenu.Length > 0 && extendedMenu.Take(originalMenu.Length).SequenceEqual(originalMenu)
                    && extendedMenu.Count(t => t == "Сыграть 1 на 1 с реплеем") == 1, "native context actions preserved and duel added once");
                testInput!.Send(new MousePositionAbsoluteInput { Position = scoreRow.ToScreenSpace(new Vector2(scoreRow.DrawWidth * .55f, 25)) });
                stage = 8;
                break;
            case 8:
                if (scoreRow?.IsHovered != true) return;
                testInput!.Send(new MouseButtonInput(MouseButton.Right, true));
                stage = 9;
                break;
            case 9:
                testInput!.Send(new MouseButtonInput(MouseButton.Right, false));
                Require(ScreenStack.CurrentScreen is SoloSongSelect, "right click only opens the native context menu");
                duelMenuItem = ChildrenOf(this).OfType<Menu.DrawableMenuItem>().FirstOrDefault(m => m.Item.Text.Value.ToString() == "Сыграть 1 на 1 с реплеем" && m.IsLoaded && m.IsPresent);
                if (duelMenuItem == null) return;
                Console.WriteLine("PASS right click opens native score menu with duel option and legacy is unchanged");
                Capture("context-menu");
                testInput!.Send(new MousePositionAbsoluteInput { Position = duelMenuItem.ToScreenSpace(duelMenuItem.DrawSize / 2) });
                stage = 11;
                break;
            case 11:
                if (duelMenuItem?.IsHovered != true) return;
                testInput!.Send(new MouseButtonInput(MouseButton.Left, true));
                stage = 12;
                break;
            case 12:
                testInput!.Send(new MouseButtonInput(MouseButton.Left, false));
                stage = 13;
                break;
            case 13:
                if (ScreenStack.CurrentScreen is not SomsReplayDuelScreen clicked) return;
                duel = clicked;
                Console.WriteLine("PASS clicking the native context menu option starts replay duel");
                stage = 1;
                break;
            case 10:
                if (ScreenStack.CurrentScreen is not SoloSongSelect && ScreenStack.CurrentScreen is not MainMenu) return;
                Console.WriteLine("PASS abort returns directly to song selection");
                Require(downloadCount == 3, "no duplicate replay downloads");
                stage = 5;
                break;
        }
    }

    private void BeginRound()
    {
        pauseVerified = false; pausedTime = 0; introCaptureTime = 0; inputPhase = 0; resultsTime = 0; player = null;
        var ruleset = Ruleset.Value.CreateInstance();
        var mods = round == 0 ? Array.Empty<Mod>() : ruleset.CreateAllMods().Where(m => m.Acronym is "DT" or "NF").ToArray();
        var playable = Beatmap.Value.GetPlayableBeatmap(Ruleset.Value, mods);
        var autoplay = ruleset.CreateAllMods().OfType<ModAutoplay>().First();
        var replay = autoplay.CreateScoreFromReplayData(playable, mods);
        replay.ScoreInfo.BeatmapInfo = Beatmap.Value.BeatmapInfo;
        replay.ScoreInfo.BeatmapHash = Beatmap.Value.BeatmapInfo.Hash;
        replay.ScoreInfo.Ruleset = Ruleset.Value;
        replay.ScoreInfo.Mods = mods;
        replay.ScoreInfo.User = rival;
        replay.ScoreInfo.OnlineID = 999000 + round;
        replay.ScoreInfo.TotalScoreVersion = LegacyScoreEncoder.LATEST_VERSION;
        SomsReplayDuelScreen.ValidateReplay(replay, replay.ScoreInfo);
        using var stream = new MemoryStream();
        new LegacyScoreEncoder(replay, playable).Encode(stream, leaveOpen: true);
        replayBytes = stream.ToArray();
        selectedFixture = replay.ScoreInfo;
        ScreenStack.Push(duel = new SomsReplayDuelScreen(replay.ScoreInfo));
    }
    private void Capture(string name)
    {
        if (visual) capture = Host.TakeScreenshotAsync().ContinueWith(t => SixLabors.ImageSharp.ImageExtensions.SaveAsPng(t.Result, Path.Combine(output, name + ".png")));
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static IEnumerable<Drawable> ChildrenOf(CompositeDrawable root)
    {
        var children = (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
        foreach (var child in children) { yield return child; if (child is CompositeDrawable c) foreach (var nested in ChildrenOf(c)) yield return nested; }
    }
    private sealed partial class DirectLoader : Loader { protected override OsuScreen CreateLoadableScreen() => new MainMenu(); }
    private sealed partial class TestInput : UserInputManager
    {
        private readonly QueuedInput queued = new();
        protected override ImmutableArray<InputHandler> InputHandlers => ImmutableArray.Create<InputHandler>(queued);
        public void Send(IInput input) => queued.Send(input);
        private sealed class QueuedInput : InputHandler
        {
            public override bool Initialize(GameHost host) => true;
            public override bool IsActive => true;
            public void Send(IInput input) => PendingInputs.Enqueue(input);
        }
    }
}
