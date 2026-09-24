using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using osu.Framework.Configuration;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterface;
using osu.Game.Graphics.Containers;
using osu.Game.Overlays.Settings.Sections.Graphics;
using osu.Game.Online.API;
using osu.Game.Overlays;
using osu.Game.Overlays.Mods;
using osu.Game.Rulesets;
using osu.Game.Rulesets.EnhancedAuth;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Screens;
using osu.Game.Screens.Menu;
using osu.Game.Screens.Play;
using osu.Game.Screens.Ranking;
using osu.Game.Screens.Select;

internal static class Program
{
    public static int Main(string[] args) => QuietTestProcess.Run(() => MainImpl(args));
    private static int MainImpl(string[] args)
    {
        if (args.Length is < 3 or > 4) throw new ArgumentException("client-directory plugin-dll isolated-output-directory or beatmap-fixture.json [--bot-only]");
        string client = Path.GetFullPath(args[0]), plugin = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        if (!output.EndsWith(".json")) Directory.CreateDirectory(output);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = name.Name == "osu.Game.Rulesets.EnhancedAuth" ? plugin : Path.Combine(client, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        foreach (string assembly in new[] { "osu.Framework", "osu.Game.Resources", "osu.Game" })
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(client, assembly + ".dll"));
        foreach (string mode in new[] { "Osu", "Taiko", "Catch", "Mania" })
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(client, "osu.Game.Rulesets." + mode + ".dll"));
        if (output.EndsWith(".json")) { BeatmapRequestChecks.Run(output); return 0; }
        return run(output, args.Contains("--bot-only"));
    }

    private static int run(string output, bool botOnly)
    {
        string profile = Path.Combine(output, "profiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, EnhancedRulesetConfig.CONFIG_FILE_NAME),
            Newtonsoft.Json.JsonConvert.SerializeObject(new EnhancedRulesetConfig { ApiUrl = "https://legacy-integration.invalid" }));
        string[] archives = { createMap(output), createMap(output, "Native Legacy Integration second set"), createMap(output, "Native Legacy Integration third set") };
        using var host = new HeadlessGameHost("soms-native-screens-" + Guid.NewGuid().ToString("N"), realtime: false);
        using var game = new IntegrationGame(profile, archives) { BotOnly = botOnly };
        Exception? failure = null;
        host.ExceptionThrown += e => { failure = e; Console.Error.WriteLine(e); host.Exit(); return true; };
        using var timer = new Timer(_ => { Console.Error.WriteLine("Timeout at " + game.Stage); host.Exit(); }, null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
        host.Run(game);
        if (failure != null) throw failure;
        if (!game.Passed) throw new Exception("Native screen integration did not complete: " + game.Stage);
        return 0;
    }

    private static string createMap(string output, string title = "Native Legacy Integration")
    {
        string path = Path.Combine(output, "integration-" + Guid.NewGuid().ToString("N") + ".osz");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var cover = zip.CreateEntry("cover.png").Open())
            cover.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAAAwCAIAAAAuKetIAAAAS0lEQVR4nO3PMREAMAgEMJQwV07FIuwl1EIXttzFQCr3rOrJqhIQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQyGfgAWPi/Q8MzMXMAAAAAElFTkSuQmCC"));
        using (var audio = new BinaryWriter(zip.CreateEntry("silence.wav").Open()))
        {
            const int length = 44100 * 8 * 2;
            audio.Write("RIFF"u8); audio.Write(length + 36); audio.Write("WAVEfmt "u8); audio.Write(16);
            audio.Write((short)1); audio.Write((short)1); audio.Write(44100); audio.Write(88200); audio.Write((short)2); audio.Write((short)16);
            audio.Write("data"u8); audio.Write(length); audio.Write(new byte[length]);
        }
        for (int i = 0; i < 18; i++)
        {
            using var map = new StreamWriter(zip.CreateEntry($"Integration {i}.osu").Open());
            map.Write($$"""
                osu file format v14
                [General]
                AudioFilename: silence.wav
                AudioLeadIn: 0
                PreviewTime: 1000
                Mode: 0
                Countdown: 0
                [Metadata]
                Title:{{title}}
                Artist:SOMS test
                Creator:Integration fixture
                Version:Test {{i}}
                Source:
                Tags:legacy integration
                BeatmapID:0
                BeatmapSetID:-1
                [Difficulty]
                HPDrainRate:2
                CircleSize:4
                OverallDifficulty:4
                ApproachRate:4
                SliderMultiplier:1.4
                SliderTickRate:1
                [Events]
                0,0,"cover.png",0,0
                [TimingPoints]
                0,500,4,2,1,50,1,0
                [HitObjects]
                160,192,1000,1,0,0:0:0:0:
                256,192,1500,1,0,0:0:0:0:
                352,192,2000,1,0,0:0:0:0:
                256,120,2500,1,0,0:0:0:0:
                """);
        }
        return path;
    }
}

internal sealed partial class IntegrationGame : OsuGame
{
    private readonly string profile;
    private readonly string[] archives;
    private Task? imported;
    private int stage, frames;
    private double lastProgress;
    private SoloSongSelect? selection;
    private LayoutSettings? scaleSettings;
    public string Stage => $"{stage}, screen={ScreenStack?.CurrentScreen?.GetType().Name}, frame={frames}";
    public bool Passed { get; private set; }

    public IntegrationGame(string profile, string[] archives) : base(Array.Empty<string>())
    {
        this.profile = profile; this.archives = archives;
        API = new DummyAPIAccess();
    }
    protected override int UnhandledExceptionsBeforeCrash => 0;
    protected override Storage CreateStorage(GameHost host, Storage defaultStorage) => host.GetStorage(profile);
    protected override Loader CreateLoader() => new DirectMenuLoader();
    protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults() => new Dictionary<FrameworkSetting, object>
    {
        [FrameworkSetting.VolumeUniversal] = 0.0, [FrameworkSetting.VolumeMusic] = 0.0, [FrameworkSetting.VolumeEffect] = 0.0,
    };
    protected override void LoadComplete()
    {
        GlobalConfigManager.InitializeGameBase(this);
        typeof(GlobalConfigManager).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new EnhancedRulesetConfig { ApiUrl = "https://legacy-integration.invalid" });
        _ = new EnhancedAuthRuleset();
        SomsClientPreferences.Instance.LegacyInterface.Value = true;
        SomsClientPreferences.Instance.SeparateInterfaceScales.Value = true;
        SomsClientPreferences.Instance.MenuInterfaceScale.Value = 1.4f;
        SomsClientPreferences.Instance.GameplayInterfaceScale.Value = 0.7f;
        LocalConfig.SetValue(Enum.Parse<OsuSetting>("UIScale"), 1.1f);
        SessionStatics.SetValue(Enum.Parse<Static>("LoginOverlayDisplayed"), true);
        LocalConfig.SetValue(Enum.Parse<OsuSetting>("ShowFirstRunSetup"), false);
        base.LoadComplete();
        Add((DummyAPIAccess)API); // OsuGameBase only attaches a real APIAccess; drive the fixture scheduler explicitly.
        Add(new ScaleSettingsHost { Child = scaleSettings = new LayoutSettings() });
        InitPolishChecks();
        InitActivityChecks();
        Ruleset.Value = RulesetStore.GetRuleset(0) ?? throw new Exception("Installed osu! ruleset not discovered");
        imported = Task.WhenAll(archives.Select(archive => BeatmapManager.Import(archive)));
        Console.WriteLine("START real OsuGame with isolated profile and Dummy API");
    }

    protected override void Update()
    {
        base.Update();
        if (Passed) return;
        frames++;
        if (Clock.CurrentTime - lastProgress > 10000) { lastProgress = Clock.CurrentTime; Console.WriteLine("WAIT " + Stage); }
        if (frames < 30) return;
        switch (stage)
        {
            case 0:
                if (imported?.IsCompleted != true || ScreenStack?.CurrentScreen is not MainMenu menu || !menu.IsLoaded) return;
                imported.GetAwaiter().GetResult();
                if (BotOnly)
                {
                    Beatmap.Value = BeatmapManager.GetWorkingBeatmap(BeatmapManager.GetAllUsableBeatmapSets().First().Beatmaps.First());
                    CloseAllOverlays();
                    stage = 14;
                    frames = 0;
                    return;
                }
                var legacyMenu = descendants(menu).FirstOrDefault(d => d.Name == "soms-legacy-main-menu");
                if (legacyMenu == null || !legacyMenu.IsLoaded) return;
                if (!CheckNativeMultiplayer(menu, legacyMenu)) return;
                if (member<string>(legacyMenu, "page") == "closed")
                {
                    if (!click(menu, "legacy-menu-logo")) return;
                    frames = 0;
                    return;
                }
                if (!CheckOfficialProfile()) return;
                require(scaleSettings?.IsLoaded == true, "Real graphics settings must load");
                require(scaleSettings!.Children.Count(d => d.Name.StartsWith("soms-interface-scale-")) == 3, "Graphics settings must have one advanced toggle and two sliders");
                checkScale(1.4f);
                CheckPolishSettings();
                CheckActivitySettings();
                var map = BeatmapManager.GetAllUsableBeatmapSets().SelectMany(s => s.Beatmaps).FirstOrDefault(b => b.Metadata.Title == "Native Legacy Integration");
                require(map != null, "Imported fixture beatmap must be stored by real BeatmapManager");
                Beatmap.Value = BeatmapManager.GetWorkingBeatmap(map!);
                CloseAllOverlays();
                if (!click(menu, "legacy-menu-play")) return;
                next("MainMenu loaded with original ButtonSystem and legacy replacement");
                break;
            case 1:
                if (ScreenStack.CurrentScreen is MainMenu playMenu && descendants(playMenu).Any(d => d.Name == "legacy-menu-solo")) next("Legacy Play menu click");
                break;
            case 2:
                if (ScreenStack.CurrentScreen is MainMenu soloMenu && click(soloMenu, "legacy-menu-solo")) next("Legacy Solo invokes native loadSongSelect");
                break;
            case 3:
                if (ScreenStack.CurrentScreen is not SoloSongSelect select || !select.IsLoaded) return;
                selection = select;
                var layer = descendants(select).FirstOrDefault(d => d.Name == "soms-legacy-song-select");
                if (layer == null || !layer.IsLoaded || (layer.Alpha <= 0 && previewVisibilityStep == 0)) return;
                var maps = member<IList>(layer, "filtered");
                if (maps == null || maps.Count < 3) return;
                if (previewVisibilityStep == 0 && !CheckSongSelectProfile()) return;
                if (!CheckActivityPreview()) return;
                if (!CheckLegacyNavigation((CompositeDrawable)layer)) return;
                if (!CheckLegacyCollections((CompositeDrawable)layer)) return;
                require(descendants((CompositeDrawable)layer).OfType<SpriteText>().Any(t => t.Text.ToString().Contains("Native Legacy Integration")), "Real imported cards visible in legacy browser");
                Console.WriteLine("PASS filtered cards " + maps?.Count);
                invoke(layer, "toggleMods");
                next("SoloSongSelect and real beatmap carousel loaded; opened mods through legacy control");
                break;
            case 4:
                var modOverlay = member<ModSelectOverlay>(selection!, "modSelectOverlay");
                if (modOverlay?.State.Value != Visibility.Visible || !CheckLiveModOverlay(modOverlay) || !click(modOverlay, "soms-legacy-mod-AT")) return;
                next("Autoplay selected through actual legacy mod button");
                break;
            case 5:
                var overlay = member<ModSelectOverlay>(selection!, "modSelectOverlay");
                require(SelectedMods.Value.Any(m => m.Acronym == "AT"), "Mod must bind to actual SelectedMods");
                if (overlay == null || !click(overlay, "soms-legacy-accept-mods")) return;
                next("Mods closed through replacement footer");
                break;
            case 6:
                var browser = descendants(selection!).First(d => d.Name == "soms-legacy-song-select");
                invoke(browser, "start");
                next("Play invokes native SelectAndRun/OnStart");
                break;
            case 7:
                if (ScreenStack.CurrentScreen is Player player && player.IsLoaded)
                {
                    checkScale(0.7f);
                    CheckActivitySeek(player);
                    SomsClientPreferences.Instance.MenuInterfaceScale.Value = 2;
                    next("Player/replay uses gameplay scale; changing menu scale during play");
                }
                break;
            case 8:
                if (ScreenStack.CurrentScreen is Player) checkScale(0.7f);
                if (ScreenStack.CurrentScreen is not ResultsScreen results || !results.IsLoaded) return;
                if (descendants(results).All(d => d.Name != "soms-result-score")) return;
                require(results.SelectedScore.Value?.TotalScore > 0, "Actual autoplay score must be positive");
                checkScale(2);
                next("Real gameplay completed; native ResultsScreen hosts legacy result and actual score");
                break;
            case 9:
                var retryResult = (ResultsScreen)ScreenStack.CurrentScreen;
                require(click(retryResult, "soms-result-retry"), "Native retry action must be available after gameplay");
                next("Legacy Retry invokes the real native player restart");
                break;
            case 10:
                if (ScreenStack.CurrentScreen is Player retriedPlayer && retriedPlayer.IsLoaded)
                {
                    require(!osu.Game.Rulesets.EnhancedAuth.Patches.SomsGameplaySeek.IsPractice(retriedPlayer), "Retry must start a clean non-practice attempt");
                    CheckSeekCursorHidden(retriedPlayer);
                    next("Retried player started");
                }
                break;
            case 11:
                if (ScreenStack.CurrentScreen is not ResultsScreen retriedResults || !retriedResults.IsLoaded) return;
                if (descendants(retriedResults).All(d => d.Name != "soms-result-score")) return;
                require(retriedResults.SelectedScore.Value?.TotalScore > 0, "Retried autoplay score must be positive");
                next("Retry completed with a new real result");
                break;
            case 12:
                SomsClientPreferences.Instance.LegacyInterface.Value = false;
                SomsClientPreferences.Instance.SeparateInterfaceScales.Value = false;
                next("Disabled Legacy interface on real result screen");
                break;
            case 13:
                var resultsOwner = (ResultsScreen)ScreenStack.CurrentScreen;
                require(member<Drawable>(resultsOwner, "VerticalScrollContent")?.Alpha > 0, "Native results restored");
                checkScale(1.1f);
                require(Math.Abs(LocalConfig.Get<float>(Enum.Parse<OsuSetting>("UIScale")) - 1.1f) < 0.001f, "Split mode must preserve native UIScale");
                Console.WriteLine("PASS MainMenu -> SoloSongSelect -> mods -> gameplay -> results -> native restoration");
                next("Starting isolated SOMSAI bot practice checks");
                break;
            case 14:
                if (!CheckBotPractice()) return;
                Passed = true;
                Host.Exit();
                break;
        }
    }

    private void next(string text) { Console.WriteLine("PASS " + text); stage++; frames = 0; }
    private void checkScale(float expected)
    {
        var global = descendants(this).OfType<ScalingContainer.ScalingDrawSizePreservingFillContainer>().First(d => d.IsLoaded);
        require(Math.Abs(global.Scale.X - expected) < 0.001f, $"Real global UI scale={global.Scale.X}, expected={expected}, {Stage}");
        CheckCursorCompensation();
    }
    private static void require(bool value, string text) { if (!value) throw new Exception(text); }
    private static bool click(CompositeDrawable root, string name)
    {
        var target = descendants(root).FirstOrDefault(d => d.Name == name);
        if (target?.IsLoaded != true || !target.IsPresent) return false;
        require(target.TriggerClick(), "Click not handled: " + name);
        return true;
    }
    private static IEnumerable<Drawable> descendants(CompositeDrawable root)
    {
        var children = member<IEnumerable<Drawable>>(root, "InternalChildren") ?? Array.Empty<Drawable>();
        foreach (var child in children)
        {
            yield return child;
            if (child is CompositeDrawable composite) foreach (var nested in descendants(composite)) yield return nested;
        }
    }
    private static T? member<T>(object target, string name) where T : class
    {
        for (Type? type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(target) as T;
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property != null) return property.GetValue(target) as T;
        }
        return null;
    }
    private static void invoke(object target, string name, params object[] arguments)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private sealed partial class DirectMenuLoader : Loader
    {
        protected override OsuScreen CreateLoadableScreen() => new MainMenu();
    }

    private sealed partial class ScaleSettingsHost : Container
    {
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
            dependencies.Cache(new OverlayColourProvider(OverlayColourScheme.Purple));
            return dependencies;
        }
    }
}
