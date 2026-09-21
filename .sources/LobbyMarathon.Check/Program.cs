using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Immutable;
using osu.Framework.Input;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.StateChanges;
using osu.Game.Beatmaps.Drawables;
using osuTK.Input;
using Newtonsoft.Json.Linq;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game;
using osu.Game.Configuration;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Spectator;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Overlays;
using osu.Game.Overlays.Mods;
using osu.Game.Overlays.Settings.Sections;
using osu.Game.Rulesets;
using osu.Game.Rulesets.EnhancedAuth;
using osu.Game.Rulesets.EnhancedAuth.Beatmaps;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens;
using osu.Game.Screens.Menu;
using osu.Game.Screens.OnlinePlay;
using osu.Game.Screens.OnlinePlay.Playlists;
using osu.Game.Screens.Play;
using osu.Game.Utils;
using osuTK;

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
        return Run(Path.GetFullPath(args[2]), args.Contains("--visual"));
    }
    private static int Run(string output, bool visual)
    {
        Directory.CreateDirectory(output);
        string profile = Path.Combine(output, "profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, EnhancedRulesetConfig.CONFIG_FILE_NAME), "{\"ApiUrl\":\"https://marathon.invalid\"}");
        string archive = CreateMap(output);
        using GameHost host = visual ? Host.GetSuitableDesktopHost("marathon-check-" + Guid.NewGuid().ToString("N"), new HostOptions { PortableInstallation = true })
            : new HeadlessGameHost("marathon-check-" + Guid.NewGuid().ToString("N"), realtime: false);
        var game = new MarathonGame(profile, archive, output, visual);
        Exception? failure = null;
        host.ExceptionThrown += error => { failure = error; Console.Error.WriteLine(error); host.Exit(); return true; };
        using var timer = new Timer(_ => { Console.Error.WriteLine("TIMEOUT " + game.Stage); host.Exit(); }, null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        host.Run(game);
        if (failure != null) throw failure;
        if (!game.Passed) throw new Exception("Did not finish: " + game.Stage);
        return 0;
    }
    private static string CreateMap(string output)
    {
        string path = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N") + ".osz");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var audio = new BinaryWriter(zip.CreateEntry("silence.wav").Open()))
        {
            const int length = 44100 * 10 * 2;
            audio.Write("RIFF"u8); audio.Write(length + 36); audio.Write("WAVEfmt "u8); audio.Write(16);
            audio.Write((short)1); audio.Write((short)1); audio.Write(44100); audio.Write(88200); audio.Write((short)2); audio.Write((short)16);
            audio.Write("data"u8); audio.Write(length); audio.Write(new byte[length]);
        }
        using var map = new StreamWriter(zip.CreateEntry("marathon.osu").Open());
        map.Write("""
            osu file format v14
            [General]
            AudioFilename:silence.wav
            Mode:0
            Countdown:0
            [Metadata]
            Title:Marathon fixture
            Artist:SOMS test
            Creator:Integration fixture
            Version:Circles sliders spinner
            BeatmapID:0
            BeatmapSetID:-1
            [Difficulty]
            HPDrainRate:2
            CircleSize:4
            OverallDifficulty:4
            ApproachRate:9
            SliderMultiplier:1.4
            SliderTickRate:1
            [TimingPoints]
            0,500,4,2,1,50,1,0
            1500,-100,4,2,1,50,0,1
            7500,-100,4,2,1,50,0,0
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

internal sealed partial class MarathonGame : OsuGame
{
    private readonly string profile, archive, output;
    private readonly bool visual;
    private Task? imported, capture;
    private int stage, frames;
    private TestPanel? panel;
    private ModDoubleTime dt = null!;
    private SomsRateFields fields = null!;
    private SomsMarathonScreen screen = null!;
    private JObject? submitted;
    private bool normalSubmission, paused, settingsCaptured;
    private int lastStage = -1, editorStage;
    private SpectatorScoreProcessor spectatorProbe = null!;
    private Drawable[] originalFragmentCards = Array.Empty<Drawable>();
    private double animationStarted;
    private float initialFirstY;
    private int pickerPhase;
    private double pickerTime;
    private OverlayProbe overlayProbe = null!;
    private string lastMessage = "";
    private double pauseTime, resultEntered;
    private TestInput input = null!;
    private readonly SomsMarathonDefinition foreign = new()
    {
        Id = 81, OwnerId = 98765, OwnerName = "Another player", Name = "Public marathon · not downloaded",
        Segments = new()
        {
            new() { BeatmapId = 75, BeatmapSetId = 1, Checksum = new string('1', 32), Title = "First song", StartMs = 1000, EndMs = 21000 },
            new() { BeatmapId = 131891, BeatmapSetId = 41823, Checksum = new string('2', 32), Title = "Second song", StartMs = 2000, EndMs = 22000 },
        },
    };
    public bool Passed { get; private set; }
    public string Stage => $"stage={stage}, editor={editorStage}, screen={ScreenStack?.CurrentScreen?.GetType().Name}";

    public MarathonGame(string profile, string archive, string output, bool visual) : base(Array.Empty<string>())
    {
        this.profile = profile; this.archive = archive; this.output = output; this.visual = visual;
        API = new DummyAPIAccess();
        API.LocalUser.Value.Username = "Player";
        ((DummyAPIAccess)API).HandleRequest = request =>
        {
            if (request is GetBeatmapsRequest lookup)
            {
                // Native playlist cards fetch their display metadata through this cache.
                var beatmapResponse = new GetBeatmapsResponse { Beatmaps = lookup.BeatmapIds.Select(id => new APIBeatmap
                {
                    OnlineID = id, RulesetID = 0, DifficultyName = "Circles sliders spinner", StarRating = .68,
                    BeatmapSet = new APIBeatmapSet { OnlineID = -1, Title = "Marathon fixture", Artist = "SOMS test",
                        Author = new APIUser { Id = 0, Username = "Integration fixture" } },
                }).ToList() };
                typeof(APIRequest<GetBeatmapsResponse>).GetMethod("TriggerSuccess", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, new[] { typeof(GetBeatmapsResponse) })!.Invoke(lookup, new object[] { beatmapResponse });
                return true;
            }
            if (request.GetType().Name.Contains("CreateSoloScore")) normalSubmission = true;
            if (request is not SomsMarathonRequest marathon) return false;
            string path = (string)typeof(SomsMarathonRequest).GetField("path", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(marathon)!;
            var body = (JObject?)typeof(SomsMarathonRequest).GetField("body", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(marathon);
            JObject response;
            if (path.EndsWith("/start")) response = new JObject { ["attempt_id"] = "00000000-0000-0000-0000-000000000042" };
            else if (path.EndsWith("/scores") && body != null) { submitted = body; response = new JObject { ["saved"] = true }; }
            else if (path.EndsWith("/scores")) response = new JObject { ["items"] = new JArray(new JObject
            {
                ["user_id"] = 7562902, ["username"] = "mrekk", ["avatar_url"] = "https://a.ppy.sh/7562902",
                ["country_code"] = "AU", ["total_score"] = 9876543, ["accuracy"] = .9876, ["max_combo"] = 1234,
                ["mods"] = new JArray(new JObject { ["acronym"] = "HD" }),
            }) };
            else if (path.Length == 0 && body != null) { response = (JObject)body.DeepClone(); response["id"] = 42; response["owner_id"] = API.LocalUser.Value.Id; }
            else response = new JObject { ["items"] = path.StartsWith("?") ? new JArray(JObject.FromObject(foreign)) : new JArray() };
            typeof(APIRequest<JObject>).GetMethod("TriggerSuccess", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, new[] { typeof(JObject) })!.Invoke(marathon, new object[] { response });
            return true;
        };
    }
    protected override int UnhandledExceptionsBeforeCrash => 0;
    protected override Storage CreateStorage(GameHost host, Storage defaultStorage) => host.GetStorage(profile);
    protected override Loader CreateLoader() => new DirectLoader();
    protected override UserInputManager CreateUserInputManager() => input = new TestInput();
    protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults() => new Dictionary<FrameworkSetting, object>
    {
        [FrameworkSetting.WindowMode] = WindowMode.Windowed, [FrameworkSetting.WindowedSize] = new System.Drawing.Size(1280, 720),
        [FrameworkSetting.VolumeUniversal] = 0.0, [FrameworkSetting.VolumeMusic] = 0.0, [FrameworkSetting.VolumeEffect] = 0.0,
    };
    protected override void LoadComplete()
    {
        GlobalConfigManager.InitializeGameBase(this);
        typeof(GlobalConfigManager).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, new EnhancedRulesetConfig { ApiUrl = "https://marathon.invalid" });
        _ = new EnhancedAuthRuleset();
        SessionStatics.SetValue(Enum.Parse<Static>("LoginOverlayDisplayed"), true);
        LocalConfig.SetValue(Enum.Parse<OsuSetting>("ShowFirstRunSetup"), false);
        base.LoadComplete();
        Require(Audio.Volume.Value == 0 && Audio.VolumeSample.Value == 0 && Audio.VolumeTrack.Value == 0, "tests are silent");
        if (visual)
        {
            Host.Window.Hide();
            var active = (Bindable<bool>)Host.IsActive;
            active.UnbindAll();
            active.Value = true;
        }
        Add((DummyAPIAccess)API);
        Add(overlayProbe = new OverlayProbe());
        Ruleset.Value = RulesetStore.GetRuleset(0)!;
        imported = BeatmapManager.Import(archive);
    }

    private void AssistChecks()
    {
        SomsAssistModes.Ensure();
        var osu = RulesetStore.GetRuleset(0)!;
        var all = osu.CreateInstance().CreateAllMods().ToArray();
        var rx = all.Single(m => m.Acronym == "RX");
        var ap = all.Single(m => m.Acronym == "AP");
        var dtMod = all.Single(m => m.Acronym == "DT");
        var hd = all.Single(m => m.Acronym == "HD");
        foreach (var assist in new[] { rx, ap })
        {
            SelectedMods.Value = new[] { assist, dtMod };
            Require(Ruleset.Value.ShortName == "osu" && Ruleset.Value.OnlineID == 0, "assist uses native osu engine");
            Require(SomsAssistModes.Presented.Value.ShortName == (assist.Acronym == "RX" ? "osurx" : "osuap"), "mod changes displayed mode");
            Require(assist.Ranked && all.Single(m => m.Acronym == "DA").Ranked == false, "assist ranked policy preserves DA exclusion");
            SelectedMods.Value = new[] { dtMod };
            Require(SomsAssistModes.Presented.Value.ShortName == "osu", "removing assist restores osu");
        }
        SelectedMods.Value = new[] { hd, dtMod };
        SomsAssistModes.Presented.Value = RulesetStore.GetRuleset("osurx")!;
        Require(SelectedMods.Value.Select(m => m.Acronym).ToHashSet().SetEquals(new[] { "HD", "DT", "RX" }), "toolbar RX preserves compatible mods");
        SomsAssistModes.Presented.Value = RulesetStore.GetRuleset("osuap")!;
        Require(SelectedMods.Value.Select(m => m.Acronym).ToHashSet().SetEquals(new[] { "HD", "DT", "AP" }), "toolbar AP replaces RX");
        SomsAssistModes.Presented.Value = osu;
        Require(SelectedMods.Value.Select(m => m.Acronym).ToHashSet().SetEquals(new[] { "HD", "DT" }), "toolbar osu removes only assist");
        foreach (int id in new[] { 1, 2, 3 })
        {
            Ruleset.Value = RulesetStore.GetRuleset(id)!;
            var native = Ruleset.Value.CreateInstance().CreateAllMods().FirstOrDefault(m => m.Acronym == "RX");
            if (native != null) SelectedMods.Value = new[] { native };
            Require(SomsAssistModes.Presented.Value.OnlineID == id && Ruleset.Value.OnlineID == id, "other engines never enter osu assist partitions");
            SelectedMods.Value = Array.Empty<Mod>();
            Require(SomsAssistModes.Presented.Value.OnlineID == id, "removing mod preserves other engine");
        }
        Ruleset.Value = osu;
        var selectors = ChildrenOf(this).OfType<osu.Game.Overlays.Toolbar.ToolbarRulesetSelector>().Where(s => s.IsLoaded).ToArray();
        Require(selectors.Length > 0, "native toolbar loaded");
        Require(selectors[0].Items.Any(r => r.ShortName == "osurx") && selectors[0].Items.Any(r => r.ShortName == "osuap"), "toolbar has assist tabs");
        SelectedMods.Value = Array.Empty<Mod>();
        Console.WriteLine("PASS RX/AP toolbar, mod sync, base gameplay IDs, other modes, ranked policy");
    }

    protected override void Update()
    {
        if (visual) ((Bindable<bool>)IsActive).Value = true;
        base.Update();
        if (capture != null && !capture.IsCompleted) return;
        capture?.GetAwaiter().GetResult(); capture = null;
        if (Passed || ++frames < 20) return;
        frames = 0;
        if (stage != lastStage) { lastStage = stage; Console.WriteLine(Stage); }
        switch (stage)
        {
            case 0:
                if (imported?.IsCompleted != true || ScreenStack?.CurrentScreen is not MainMenu { IsLoaded: true }) return;
                imported.GetAwaiter().GetResult();
                Beatmap.Value = BeatmapManager.GetWorkingBeatmap(BeatmapManager.GetAllUsableBeatmapSets().First().Beatmaps.First());
                CloseAllOverlays();
                Require(!ChildrenOf((CompositeDrawable)ScreenStack.CurrentScreen).OfType<DailyChallengeButton>().Any(), "daily challenge replaced");
                AssistChecks();
                UnitChecks();
                spectatorProbe = new SpectatorScoreProcessor(98765);
                spectatorProbe.OnLoadComplete += _ =>
                {
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(SpectatorScoreProcessor).GetField("scoreInfo", flags)!.SetValue(spectatorProbe, new ScoreInfo { Ruleset = Ruleset.Value });
                    typeof(SpectatorScoreProcessor).GetField("spectatorState", flags)!.SetValue(spectatorProbe, new SpectatorState());
                    var receive = typeof(SpectatorScoreProcessor).GetMethod("onNewFrames", flags)!;
                    for (int packet = 0; packet < 30; packet++)
                        receive.Invoke(spectatorProbe, new object[] { 98765, new FrameDataBundle(new FrameHeader(new ScoreInfo(), new ScoreProcessorStatistics()), Array.Empty<LegacyReplayFrame>()) });
                    receive.Invoke(spectatorProbe, new object[] { 98765, new FrameDataBundle(new FrameHeader(new ScoreInfo { TotalScore = 12345, Accuracy = .98, MaxCombo = 12 }, new ScoreProcessorStatistics()), new[] { new LegacyReplayFrame(0, 0, 0, ReplayButtonState.None) }) });
                };
                Add(spectatorProbe);
                dt = (ModDoubleTime)Ruleset.Value.CreateInstance().CreateAllMods().Single(mod => mod.Acronym == "DT");
                SelectedMods.Value = new[] { dt };
                Add(panel = new TestPanel { Width = 580, AutoSizeAxes = Axes.Y, Children = new Drawable[] { new ModCustomisationSection(dt, dt.CreateSettingsControls().ToArray()), new SkinSection() } });
                stage++;
                break;
            case 1:
                if (!ChildrenOf(panel!).OfType<SomsRateFields>().Any(drawable => drawable.IsLoaded)) return;
                Require(spectatorProbe.TotalScore.Value == 12345 && spectatorProbe.Accuracy.Value == .98, "empty bot packets ignored and next native score update preserved");
                Console.WriteLine("PASS native spectator survives 30 empty packets and displays the next valid score");
                Remove(spectatorProbe, true);
                fields = ChildrenOf(panel!).OfType<SomsRateFields>().Single();
                var bpm = field("bpm");
                var ar = field("ar");
                Require(bpm.Current.Value == "180", "initial BPM is rate-adjusted");
                commit(bpm, "300", false); Require(dt.SpeedChange.Value == 2 && bpm.Current.Value == "240", "BPM ceiling clamps to 2x");
                commit(ar, "11.5", true); Require(dt.SpeedChange.Value == 2 && ar.Current.Value == "11", "AR ceiling clamps");
                commit(bpm, "100", false); Require(dt.SpeedChange.Value == 1.01, "BPM floor is 1.01x");
                commit(ar, "1", true); Require(dt.SpeedChange.Value == 1.01, "AR floor is 1.01x");
                commit(bpm, "200", false); Require(Math.Abs(dt.SpeedChange.Value - 1.67) < .0001, "BPM inversely changes native speed");
                dt.SpeedChange.Value = 1.5; Require(bpm.Current.Value == "180", "native slider changes BPM");
                var apiMod = JObject.FromObject(new APIMod(dt));
                Require(!apiMod.ToString().Contains("bpm") && !apiMod.ToString().Contains("approach"), "convenience fields do not alter serialized mods");
                var choices = SkinManager.GetAllUsableSkins().Where(skin => skin.ID != Guid.Empty && !SomsSkinHotkeys.IsRandomSkin(skin.ID)).Take(2).ToArray();
                var preferences = SomsClientPreferences.Instance;
                preferences.ModSkinsEnabled.Value = true;
                preferences.ModSkins[0].Value = choices[0].ID; preferences.ModSkins[3].Value = choices[1].ID;
                SomsModSkins.Apply(SkinManager, Array.Empty<Mod>()); Require(SkinManager.CurrentSkinInfo.Value.ID == choices[0].ID, "NM skin selected");
                SomsModSkins.Apply(SkinManager, new[] { dt }); Require(SkinManager.CurrentSkinInfo.Value.ID == choices[1].ID, "DT skin selected");
                Console.WriteLine("PASS native DT fields, clamps, bidirectional speed, serialization and real skin switching");
                stage++;
                break;
            case 2:
                if (!settingsCaptured) { settingsCaptured = true; Capture("settings"); return; }
                Remove(panel!, true);
                SelectedMods.Value = Array.Empty<Mod>();
                var segment = SomsMarathonCompiler.Suggest(Beatmap.Value);
                Require(segment.StartMs == 1500 && segment.EndMs == 7001, "Kiai chosen within playable section");
                var definition = new SomsMarathonDefinition { Id = 42, OwnerId = API.LocalUser.Value.Id, Name = "Songs compilation · native test", Segments = new() { segment, segment } };
                Directory.CreateDirectory(Path.Combine(profile, "soms-marathons"));
                File.WriteAllText(Path.Combine(profile, "soms-marathons/draft.json"), Newtonsoft.Json.JsonConvert.SerializeObject(definition));
                var menu = (CompositeDrawable)ScreenStack.CurrentScreen;
                ChildrenOf(menu).OfType<ButtonSystem>().Single().State = ButtonSystemState.Play;
                var marathonButton = ChildrenOf(menu).OfType<MainMenuButton>().Single(button => ChildrenOf(button).OfType<OsuSpriteText>().Any(text => text.Text.ToString() == "Марафон"));
                marathonButton.FinishTransforms(true);
                marathonButton.TriggerClick();
                stage++;
                break;
            case 3:
                if (screen != null && frames++ % 300 == 0)
                    Console.WriteLine(Stage + " " + ((OsuSpriteText)typeof(SomsMarathonScreen).GetField("status", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(screen)!).Text);
                if (ScreenStack.CurrentScreen is SomsMarathonSongSelect picker)
                {
                    if (!picker.IsLoaded) return;
                    var addButton = ChildrenOf(this).OfType<AddToPlaylistFooterButton>().FirstOrDefault(b => b.IsLoaded && b.Text.ToString() == "Добавить в марафон");
                    if (addButton == null) return;
                    var tray = ChildrenOf(picker).OfType<PlaylistsSongSelect.PlaylistTray>().Single();
                    if (pickerPhase == 0)
                    {
                        Require(addButton.Enabled.Value, "native add button accepts the local difficulty");
                        addButton.TriggerClick(); pickerTime = Time.Current; pickerPhase = 1; return;
                    }
                    if (pickerPhase == 1)
                    {
                        if (Time.Current - pickerTime < 650) return;
                        Require(tray.Alpha > .95f, "native tray appears on first addition");
                        Require(ChildrenOf(tray).OfType<DrawableRoomPlaylistItem>().Count() == 1, "tray displays native playlist card");
                        var trayText = string.Join(" | ", ChildrenOf(tray).OfType<osu.Framework.Graphics.Sprites.SpriteText>().Select(t => t.Text.ToString()));
                        Console.WriteLine("Native tray content: " + trayText);
                        Require(trayText.Contains("Marathon"), "native tray displays the map title, not an empty card");
                        Capture("picker-first"); addButton.TriggerClick(); pickerTime = Time.Current; pickerPhase = 2; return;
                    }
                    if (pickerPhase == 2)
                    {
                        if (Time.Current - pickerTime < 650) return;
                        Require(tray.Alpha > .95f && ChildrenOf(tray).OfType<DrawableRoomPlaylistItem>().Count() == 2, "native tray scrolls between successive cards");
                        Require(!ChildrenOf(picker).OfType<OsuSpriteText>().Any(t => t.Text.ToString().StartsWith("Добавлено:")), "old floating summary removed");
                        Capture("picker-second"); pickerPhase = 3; return;
                    }
                    if (Time.Current - pickerTime < 2600) return;
                    Require(tray.Alpha < .01f, "native tray fades away after its display delay");
                    Require(addButton.IsPresent, "native add button remains visible");
                    Console.WriteLine("PASS actual lazer PlaylistTray, native scrolling cards, timed fade and AddToPlaylistFooterButton");
                    picker.Exit(); editorStage = 2; return;
                }
                if (ScreenStack.CurrentScreen is not SomsMarathonScreen current) return;
                screen = current;
                if (!screen.IsLoaded || (bool)typeof(SomsMarathonScreen).GetField("busy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(screen)!) return;
                void click(string name) => ChildrenOf(screen).OfType<ShearedButton>().First(b => b.IsLoaded && b.Text.ToString() == name).TriggerClick();
                if (editorStage == 0)
                {
                    var library = (CompositeDrawable)typeof(SomsMarathonScreen).GetField("library", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(screen)!;
                    var cover = ChildrenOf(library).OfType<UpdateableOnlineBeatmapSetCover>().SingleOrDefault();
                    if (cover?.IsLoaded != true) return;
                    Require(cover.OnlineInfo.Covers.Cover.Contains("/beatmaps/1/"), "public card uses the FIRST map's cover without a local map");
                    Capture("public-lounge");
                    ChildrenOf(library).OfType<ClickableContainer>().First(c => c.GetType().Name == "MarathonListItem").TriggerClick();
                    editorStage = 80; return;
                }
                if (editorStage == 80)
                {
                    var detail = (CompositeDrawable)typeof(SomsMarathonScreen).GetField("detail", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(screen)!;
                    if (ChildrenOf(detail).OfType<UpdateableOnlineBeatmapSetCover>().Count(c => c.IsLoaded) != 2) return;
                    Require(ChildrenOf(detail).OfType<IconButton>().All(b => !b.IsPresent), "foreign marathon has no edit/reorder/remove controls");
                    Require(ChildrenOf(detail).OfType<ShearedButton>().Where(b => b.Text.ToString() is "Сохранить" or "+ Добавить песни").All(b => !b.IsPresent), "foreign marathon has no save/add buttons");
                    Require(ChildrenOf(detail).OfType<FormTextBox>().Single().ReadOnly, "foreign marathon name is readonly");
                    Require(ChildrenOf(detail).OfType<osu.Game.Users.Drawables.ClickableAvatar>().Count() == 1,
                        "marathon leaderboard avatar uses the native clickable profile control");
                    Require(ChildrenOf(detail).OfType<osu.Game.Users.Drawables.UpdateableFlag>().Count() == 1,
                        "marathon leaderboard flag uses the native country ranking control");
                    var currentDefinition = (SomsMarathonDefinition)typeof(SomsMarathonScreen).GetField("definition", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(screen)!;
                    typeof(SomsMarathonScreen).GetMethod("move", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(screen, new object[] { 0, 1 });
                    typeof(SomsMarathonScreen).GetMethod("changed", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(screen, null);
                    Require(currentDefinition.Id == 81 && currentDefinition.Segments[0].BeatmapSetId == 1, "foreign editing handlers cannot mutate the definition or detach it into a draft");
                    Console.WriteLine("PASS remote first cover, both remote song backgrounds and foreign marathon readonly guards");
                    Capture("public-detail"); click("Мой черновик"); editorStage = 1; return;
                }
                if (editorStage == 1)
                {
                    var pane = (Drawable)typeof(SomsMarathonScreen).GetField("detail", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(screen)!;
                    if (pane.Alpha < .999f) return;
                    click("+ Добавить песни"); return;
                }
                if (editorStage == 2)
                {
                    var definitionNow = (SomsMarathonDefinition)typeof(SomsMarathonScreen).GetField("definition", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(screen)!;
                    if (definitionNow.Segments.Count < 4) return;
                    Require(definitionNow.Segments.Count == 4, "native song picker commits both additions once");
                    originalFragmentCards = ChildrenOf(screen).Where(d => d.GetType().Name == "MarathonListItem" && d.Height == 68).ToArray();
                    Require(originalFragmentCards.Length == 4, "four half-height fragment cards");
                    if (animationStarted == 0) { animationStarted = Time.Current; return; }
                    if (Time.Current - animationStarted < 600) return;
                    initialFirstY = originalFragmentCards[0].Y;
                    ChildrenOf(screen).OfType<IconButton>().First(b => b.TooltipText.ToString() == "Переместить ниже").TriggerClick();
                    animationStarted = Time.Current; editorStage = 20; return;
                }
                if (editorStage == 20)
                {
                    Require(ChildrenOf(screen).Where(d => d.GetType().Name == "MarathonListItem" && d.Height == 68).SequenceEqual(originalFragmentCards), "reordering retains loaded card drawables");
                    if (Time.Current - animationStarted < 650) return;
                    Require(originalFragmentCards[0].Y > initialFirstY + 60, "song smoothly moved into its new compact row");
                    foreach (var card in originalFragmentCards.Cast<CompositeDrawable>())
                    {
                        var icons = ChildrenOf(card).OfType<IconButton>().ToArray();
                        Require(icons.Length == 4, "compact row retains all actions");
                        foreach (var icon in icons)
                        {
                            var bounds = icon.ScreenSpaceDrawQuad.AABBFloat;
                            Require(card.ScreenSpaceDrawQuad.AABBFloat.Contains(bounds), "compact buttons stay inside their card");
                        }
                    }
                    Console.WriteLine("PASS playlist-style page transition and retained animated song reordering");
                    Console.WriteLine("PASS half-height cards with all four actions inside each row");
                    Capture("marathon"); ChildrenOf(screen).OfType<IconButton>().First(b => b.TooltipText.ToString() == "Редактировать отрезок").TriggerClick();
                    editorStage = 3; animationStarted = 0; return;
                }
                if (editorStage == 3)
                {
                    var range = ChildrenOf(screen).OfType<SomsMarathonRange>().FirstOrDefault(r => r.IsLoaded);
                    if (range == null) return;
                    if (animationStarted == 0) { animationStarted = Time.Current; return; }
                    if (Time.Current - animationStarted < 450) return;
                    Require(range.End > range.Start && range.End - range.Start <= SomsMarathonRange.Maximum, "range bounds");
                    Capture("range-editor"); input.Send(new KeyboardKeyInput(Key.Escape, true)); editorStage = 81; return;
                }
                if (editorStage == 81)
                {
                    input.Send(new KeyboardKeyInput(Key.Escape, false));
                    Require(ScreenStack.CurrentScreen == screen && typeof(SomsMarathonScreen).GetField("rangeModal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(screen) == null, "Escape closes only segment dialog and keeps marathon screen");
                    Console.WriteLine("PASS real Escape key dismisses range editor without leaving marathon");
                    editorStage = 4; return;
                }
                if (editorStage == 4)
                {
                    Require(overlayProbe.Selector.Items.Select(i => i.OnlineID).SequenceEqual(new[] {0,1,2,3,4,5}), "overlay order");
                    Require(ChildrenOf(overlayProbe).OfType<OsuSpriteText>().Count(t => t.Text.ToString() is "RX" or "AP") == 2, "RX/AP inside icons");
                    Console.WriteLine("PASS native picker, range editor, overlay icons and layout without crash");
                    click("Играть"); stage++;
                }
                break;
            case 4:
                if (ScreenStack.CurrentScreen is not SomsMarathonPlayer { IsLoaded: true } player)
                {
                    string message = ((OsuSpriteText)typeof(SomsMarathonScreen).GetField("status", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(screen)!).Text.ToString();
                    if (message != lastMessage) { lastMessage = message; Console.WriteLine("Status: " + message); }
                    if (message.Contains("failed", StringComparison.OrdinalIgnoreCase) || message.Contains("Не удалось")) throw new Exception(message);
                    return;
                }
                Require(player.LoadedBeatmapSuccessfully, "compiled gameplay loads");
                Require(player.Beatmap.Value.Beatmap.HitObjects.Count == 32, "all four song excerpts preserve objects");
                Console.WriteLine("PASS native compilation player loaded with 32 objects");
                Capture("gameplay"); stage++;
                break;
            case 5:
                if (ScreenStack.CurrentScreen is SomsMarathonPlayer active)
                {
                    var clock = (GameplayClockContainer)typeof(Player).GetProperty("GameplayClockContainer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(active)!;
                    if (!paused && clock.CurrentTime > 3500)
                    {
                        if (pauseTime == 0) { active.Pause(); pauseTime = clock.CurrentTime; Capture("gameplay"); return; }
                        Require(Math.Abs(clock.CurrentTime - pauseTime) < 1, "pause freezes compilation clock");
                        active.Resume(); paused = true;
                    }
                    return;
                }
                if (submitted == null || ScreenStack.CurrentScreen is not SomsMarathonResultsScreen { IsLoaded: true } results) return;
                if (resultEntered == 0) resultEntered = Time.Current;
                if (Time.Current - resultEntered < 9500) return;
                Require(results.Score != null && results.Score.PP == null && results.Score.HitEvents.Count > 0, "full native result with hit statistics and no pp");
                Require(BeatmapManager.GetWorkingBeatmap(results.Score.BeatmapInfo) is SomsMarathonWorkingBeatmap, "results resolves temporary compilation");
                Require(paused, "pause checked");
                Require(!normalSubmission, "no normal ranked score request");
                Require(submitted.Value<string>("attempt_id")!.EndsWith("42"), "score belongs to playlist attempt");
                Console.WriteLine("PASS complete marathon, pause/resume, native results and hit graphs and isolated playlist submission");
                Capture("results"); stage++;
                break;
            case 6:
                var resultScreen = (SomsMarathonResultsScreen)ScreenStack.CurrentScreen;
                ((VisibilityContainer)typeof(osu.Game.Screens.Ranking.ResultsScreen).GetProperty("StatisticsPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resultScreen)!).Show();
                resultEntered = Time.Current; stage++; break;
            case 7:
                if (Time.Current - resultEntered < 2000) return;
                Capture("result-statistics"); stage++; break;
            case 8:
                Passed = true; Exit(); break;
        }
    }
    private FormTextBox field(string name) => (FormTextBox)typeof(SomsRateFields).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fields)!;
    private sealed partial class TestInput : UserInputManager
    {
        private readonly QueuedInput queued = new();
        protected override ImmutableArray<InputHandler> InputHandlers => ImmutableArray.Create<InputHandler>(queued);
        public void Send(IInput value) => queued.Send(value);
        private sealed class QueuedInput : InputHandler
        {
            public override bool Initialize(GameHost host) => true;
            public override bool IsActive => true;
            public void Send(IInput value) => PendingInputs.Enqueue(value);
        }
    }
    private void commit(FormTextBox input, string value, bool ar)
    {
        input.Current.Value = value;
        typeof(SomsRateFields).GetMethod("commit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fields, new object[] { input, ar });
    }
    private static void UnitChecks()
    {
        var range = new SomsMarathonRange(300000, 0, 30000);
        range.SetBoundary(true, 300000); Require(range.End == 90000, "90 second maximum");
        range.SetBoundary(false, 100000); Require(range.Start == 85000, "minimum separation");
        range.SetBoundary(true, 0); Require(range.End == 90000, "handles never cross");
        range.SetBoundary(false, -100); Require(range.Start == 0, "start clamp");
        foreach (var pair in new[] { ("", 0), ("CL,FL", 0), ("HD,EZ", 1), ("HD,HR", 2), ("DT,HD,HR", 3), ("NC", 3), ("DT,EZ", 4), ("DT,HD,EZ", 1) })
            Require(SomsModSkins.Category(pair.Item1.Split(',')) == pair.Item2, "mod skin precedence " + pair.Item1);
        foreach (double ar in new[] { 0d, 3, 5, 7, 9, 10 })
        foreach (double speed in new[] { 1.01, 1.2, 1.5, 2 })
            Require(Math.Abs(SomsRateConversion.ForApproachRate(ar, SomsRateConversion.ApproachRate(ar, speed)) - speed) < .000001, "AR inverse");
        Console.WriteLine("PASS AR inverse across both preempt ranges and mod skin precedence");
    }
    private void Capture(string name)
    {
        if (visual) capture = Host.TakeScreenshotAsync().ContinueWith(task => SixLabors.ImageSharp.ImageExtensions.SaveAsPng(task.Result, Path.Combine(output, name + ".png")));
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static IEnumerable<Drawable> ChildrenOf(CompositeDrawable root)
    {
        var children = (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
        foreach (var child in children) { yield return child; if (child is CompositeDrawable container) foreach (var nested in ChildrenOf(container)) yield return nested; }
    }
    private sealed partial class DirectLoader : Loader { protected override OsuScreen CreateLoadableScreen() => new MainMenu(); }
    private sealed partial class TestPanel : FillFlowContainer
    {
        [Cached] private readonly OverlayColourProvider colours = new(OverlayColourScheme.Purple);
        public TestPanel() { Direction = FillDirection.Vertical; }
    }
}

internal sealed partial class OverlayProbe : Container
{
    [Cached] private readonly osu.Game.Overlays.OverlayColourProvider colours = new(osu.Game.Overlays.OverlayColourScheme.Purple);
    public readonly osu.Game.Overlays.OverlayRulesetSelector Selector = new();
    public OverlayProbe() { Size = new osuTK.Vector2(250, 40); Position = new osuTK.Vector2(20, 70); Child = Selector; Depth = -999; }
}
