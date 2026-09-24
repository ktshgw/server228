using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
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
    private int lastStage = -1;
    private string lastMessage = "";
    private double pauseTime;
    public bool Passed { get; private set; }
    public string Stage => $"stage={stage}, screen={ScreenStack?.CurrentScreen?.GetType().Name}";

    public MarathonGame(string profile, string archive, string output, bool visual) : base(Array.Empty<string>())
    {
        this.profile = profile; this.archive = archive; this.output = output; this.visual = visual;
        API = new DummyAPIAccess();
        API.LocalUser.Value.Username = "Player";
        ((DummyAPIAccess)API).HandleRequest = request =>
        {
            if (request.GetType().Name.Contains("CreateSoloScore")) normalSubmission = true;
            if (request is not SomsMarathonRequest marathon) return false;
            string path = (string)typeof(SomsMarathonRequest).GetField("path", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(marathon)!;
            var body = (JObject?)typeof(SomsMarathonRequest).GetField("body", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(marathon);
            JObject response;
            if (path.EndsWith("/start")) response = new JObject { ["attempt_id"] = "00000000-0000-0000-0000-000000000042" };
            else if (path.EndsWith("/scores") && body != null) { submitted = body; response = new JObject { ["saved"] = true }; }
            else response = new JObject { ["items"] = new JArray() };
            typeof(APIRequest<JObject>).GetMethod("TriggerSuccess", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, new[] { typeof(JObject) })!.Invoke(marathon, new object[] { response });
            return true;
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
        Ruleset.Value = RulesetStore.GetRuleset(0)!;
        imported = BeatmapManager.Import(archive);
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
                UnitChecks();
                dt = (ModDoubleTime)Ruleset.Value.CreateInstance().CreateAllMods().Single(mod => mod.Acronym == "DT");
                SelectedMods.Value = new[] { dt };
                Add(panel = new TestPanel { Width = 580, AutoSizeAxes = Axes.Y, Children = new Drawable[] { new ModCustomisationSection(dt, dt.CreateSettingsControls().ToArray()), new SkinSection() } });
                stage++;
                break;
            case 1:
                if (!ChildrenOf(panel!).OfType<SomsRateFields>().Any(drawable => drawable.IsLoaded)) return;
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
                var definition = new SomsMarathonDefinition { Id = 42, Name = "Songs compilation · native test", Segments = new() { segment, segment } };
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
                if (ScreenStack.CurrentScreen is not SomsMarathonScreen current) return;
                screen = current;
                if (!screen.IsLoaded || !ChildrenOf(screen).OfType<ShearedButton>().Any(button => button.IsLoaded && button.Text.ToString() == "Играть")) return;
                Require(ChildrenOf(screen).OfType<OsuSpriteText>().Any(text => text.Text.ToString().Contains("Circles sliders spinner")), "saved fragments restored");
                Capture("marathon");
                ChildrenOf(screen).OfType<ShearedButton>().Single(button => button.Text.ToString() == "Играть").TriggerClick();
                stage++;
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
                Require(player.Beatmap.Value.Beatmap.HitObjects.Count == 16, "both song excerpts preserve objects");
                Console.WriteLine("PASS native compilation player loaded with 16 objects");
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
                if (submitted == null) return;
                Require(paused, "pause checked");
                Require(!normalSubmission, "no normal ranked score request");
                Require(submitted.Value<string>("attempt_id")!.EndsWith("42"), "score belongs to playlist attempt");
                Console.WriteLine("PASS complete marathon, pause/resume, local score flow and isolated playlist submission");
                Capture("results"); stage++;
                break;
            case 6:
                Passed = true; Exit(); break;
        }
    }
    private FormTextBox field(string name) => (FormTextBox)typeof(SomsRateFields).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fields)!;
    private void commit(FormTextBox input, string value, bool ar)
    {
        input.Current.Value = value;
        typeof(SomsRateFields).GetMethod("commit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fields, new object[] { input, ar });
    }
    private static void UnitChecks()
    {
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
