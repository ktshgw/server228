using System.Reflection;
using System.Runtime.Loader;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input.Events;
using osu.Framework.Input.States;
using osu.Framework.Input;
using osu.Framework.Platform;
using osu.Framework.IO.Stores;
using osu.Game;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Graphics.Sprites;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Rooms;
using osu.Game.Overlays;
using osu.Game.Overlays.Mods;
using osu.Game.Rulesets;
using osu.Game.Rulesets.EnhancedAuth;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Game.Screens.Menu;
using osu.Game.Screens.OnlinePlay.Multiplayer;
using osu.Game.Screens.OnlinePlay.Lounge.Components;
using osu.Game.Screens.Play;
using osu.Game.Screens.Ranking;
using osu.Game.Screens.Select;
using osu.Game.Skinning;
using osu.Game.IO;
using osuTK;
using osuTK.Input;
using SixLabors.ImageSharp;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("client-directory plugin-dll [--visual] [--output=absolute-directory] [--skin-directory=absolute-directory]");
        string client = Path.GetFullPath(args[0]), plugin = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--output="))?[9..] ?? ".test-tmp/legacy-v2");
        Directory.CreateDirectory(output);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = name.Name == "osu.Game.Rulesets.EnhancedAuth" ? plugin : Path.Combine(client, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        foreach (string assembly in new[] { "osu.Framework", "osu.Game.Resources", "osu.Game" })
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(client, assembly + ".dll"));
        foreach (string mode in new[] { "Osu", "Taiko", "Catch", "Mania" })
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(client, "osu.Game.Rulesets." + mode + ".dll"));
        string? skinDirectory = args.FirstOrDefault(a => a.StartsWith("--skin-directory="))?[17..];
        if (skinDirectory != null && !Directory.Exists(skinDirectory)) throw new DirectoryNotFoundException(skinDirectory);
        return Run(args.Contains("--visual"), output, skinDirectory);
    }

    private static int Run(bool visual, string output, string? skinDirectory)
    {
        string name = "soms-legacy-check-" + Guid.NewGuid().ToString("N");
        string profile = Path.Combine(output, "profiles", name);
        Directory.CreateDirectory(profile);
        using GameHost host = visual
            ? osu.Framework.Host.GetSuitableDesktopHost(name, new HostOptions { PortableInstallation = true, FriendlyGameName = "SOMS! interface verification" })
            : new HeadlessGameHost(name, realtime: false);
        using var game = new CheckGame(visual, output, profile, skinDirectory);
        Exception? failure = null;
        host.ExceptionThrown += e => { failure = e; Console.Error.WriteLine(e); host.Exit(); return false; };
        using var timeout = new Timer(_ => host.Exit(), null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        host.Run(game);
        if (failure != null) throw failure;
        if (!game.Passed) throw new Exception("Legacy UI verification did not complete in 90 seconds");
        return 0;
    }
}

internal sealed partial class CheckGame : OsuGameBase
{
    private readonly bool visual;
    private readonly string output, profile;
    private readonly string? skinDirectory;
    private FixtureRoot root = null!;
    private readonly List<Scene> scenes = new();
    private readonly Queue<(string Name, Action Action)> steps = new();
    private readonly List<Drawable> owners = new();
    private Task? capture;
    private int frames;
    private Scene main = null!, result = null!, pause = null!, mods = null!, lounge = null!;
    private SoloResultsScreen resultOwner = null!;
    private ModSelectOverlay modOwner = null!;
    private Box loungeNativeVisual = null!;
    private RoomListing nativeRooms = null!;
    private FixedRulesetStore? modeChoices;
    private osu.Framework.Graphics.UserInterface.BasicTextBox nativeSearch = null!;
    private object nativeSongSearch = null!;
    private Bindable<string> nativeSongQuery => (Bindable<string>)Property(nativeSongSearch.GetType(), "Current").GetValue(nativeSongSearch)!;
    private Box original = null!;
    private SomsLegacyTexture texture = null!;
    private int soloCalls, multiplayerCalls, somsaiCalls, optionsCalls, retryCalls;
    private Ruleset nativeRuleset = null!;
    [Resolved]
    private FrameworkConfigManager frameworkConfig { get; set; } = null!;
    public bool Passed { get; private set; }

    public CheckGame(bool visual, string output, string profile, string? skinDirectory)
    {
        this.visual = visual; this.output = output; this.profile = profile; this.skinDirectory = skinDirectory;
        API = new DummyAPIAccess();
    }
    protected override int UnhandledExceptionsBeforeCrash => 0;
    protected override Storage CreateStorage(GameHost host, Storage defaultStorage) => host.GetStorage(profile);
    protected override Container CreateScalingContainer() => new Container { RelativeSizeAxes = Axes.Both };
    protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults() => new Dictionary<FrameworkSetting, object>
    {
        [FrameworkSetting.WindowMode] = WindowMode.Windowed,
        [FrameworkSetting.WindowedSize] = new System.Drawing.Size(1280, 720),
    };
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        GlobalConfigManager.InitializeGameBase(this);
        typeof(GlobalConfigManager).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new EnhancedRulesetConfig { ApiUrl = "https://legacy-check.invalid" });
        _ = new EnhancedAuthRuleset();
        nativeRuleset = NativeRuleset("Osu");
        Ruleset.Value = nativeRuleset.RulesetInfo;
        var fixtureMap = Beatmap.Value.BeatmapInfo;
        fixtureMap.Metadata.Artist = "SOMS! test artist";
        fixtureMap.Metadata.Title = "Legacy interface — playback test";
        fixtureMap.Metadata.Author.Username = "Test mapper";
        fixtureMap.DifficultyName = "Insane";
        fixtureMap.Ruleset = nativeRuleset.RulesetInfo;
        fixtureMap.StarRating = 5.31;
        fixtureMap.BPM = 180;
        fixtureMap.Length = 125000;
        fixtureMap.Difficulty.CircleSize = 4;
        fixtureMap.Difficulty.ApproachRate = 9;
        fixtureMap.Difficulty.OverallDifficulty = 8;
        fixtureMap.Difficulty.DrainRate = 6;
        Require(!SomsClientPreferences.Instance.LegacyInterface.Value, "Legacy interface must default to off in a fresh profile");
        Add(root = new FixtureRoot(skinDirectory) { RelativeSizeAxes = visual ? Axes.Both : Axes.None, Size = visual ? Vector2.One : new Vector2(1280, 720) });
        var skinProbe = new Container { RelativeSizeAxes = Axes.Both, AlwaysPresent = true, Alpha = 0 };
        root.Add(skinProbe);
        skinProbe.Add(original = new Box { Size = new Vector2(128, 64), Alpha = .75f });
        skinProbe.Add(texture = new SomsLegacyTexture("soms-fixture", new[] { original }) { Size = new Vector2(.1f, .1f) });

        var menuOwner = new MainMenu();
        owners.Add(menuOwner);
        var buttons = new ButtonSystem
        {
            OnSolo = () => soloCalls++, OnMultiplayer = () => multiplayerCalls++, OnSettings = () => optionsCalls++,
        };
        SetMember(buttons, "buttonsMulti", new List<MainMenuButton>
        {
            new MainMenuButton("Лобби", "", FontAwesome.Solid.Couch, osuTK.Graphics.Color4.White, (_, _) => multiplayerCalls++),
            new MainMenuButton("SOMSAI", "", FontAwesome.Solid.Trophy, osuTK.Graphics.Color4.White, (_, _) => somsaiCalls++) { Name = "somsai-menu-button" },
        });
        SetMember(menuOwner, "Buttons", buttons);
        main = addScene("main-menu", new SomsLegacyMainMenu(menuOwner));

        resultOwner = new SoloResultsScreen(sampleScore(nativeRuleset));
        owners.Add(resultOwner);
        var property = Property(typeof(ResultsScreen), "VerticalScrollContent");
        var constructor = property.PropertyType.GetConstructors().Single(c => c.GetParameters().All(p => p.IsOptional));
        var nativeResult = (Drawable)constructor.Invoke(constructor.GetParameters().Select(p => p.DefaultValue).ToArray());
        nativeResult.RelativeSizeAxes = Axes.Both;
        property.SetValue(resultOwner, nativeResult);
        result = addScene("results", new SomsLegacyResults(resultOwner), nativeResult);

        var pauseOwner = new PauseOverlay { OnResume = () => { }, OnRetry = () => retryCalls++, OnQuit = () => { } };
        owners.Add(pauseOwner);
        pause = addScene("pause", new SomsLegacyGameplayMenu(pauseOwner));

        modOwner = new ModSelectOverlay();
        owners.Add(modOwner);
        var nativeMods = new Container { RelativeSizeAxes = Axes.Both };
        nativeMods.Add(nativeSearch = new osu.Framework.Graphics.UserInterface.BasicTextBox { Size = new Vector2(300, 45) });
        SetMember(modOwner, "TopLevelContent", nativeMods);
        modOwner.AvailableMods.Value = Enum.GetValues<ModType>()
            .Select(type => (type, mods: (IReadOnlyList<ModState>)nativeRuleset.GetModsFor(type).SelectMany(m => m is MultiMod multi ? multi.Mods : new[] { m }).Select(m => new ModState(m)).ToArray()))
            .Where(group => group.mods.Count > 0).ToDictionary(group => group.type, group => group.mods);
        foreach (var mod in modOwner.AllAvailableMods)
            mod.Active.BindValueChanged(_ => modOwner.SelectedMods.Value = modOwner.AllAvailableMods.Where(m => m.Active.Value).Select(m => m.Mod).ToArray());
        mods = addScene("mods", new SomsLegacyMods(modOwner), nativeMods);
        addSongSelect();
        addLounge();
        arrangeSteps();
    }

    private void arrangeSteps()
    {
        step("off preserves native scene", () =>
        {
            Require(scenes.All(s => s.Layer.Alpha == 0), "Off must retain native visuals");
            Require(original.Alpha == .75f, "Off changed underlying skin component");
            show(mods);
            Require(root.Focus.ChangeFocus(nativeSearch) && root.Input.FocusedDrawable == nativeSearch, "Native search could not receive focus while legacy is off");
            SomsClientPreferences.Instance.LegacyInterface.Value = true;
            show(main);
        });
        step("separate main menu", () =>
        {
            Require(texture.Alpha == 1 && original.Alpha == 0, "Texture replacement failed");
            Require(scenes.All(s => s.Layer.Alpha == 1), "A classic scene failed to build");
            Require(root.Input.FocusedDrawable != nativeSearch, "Hidden native search retained keyboard focus after enable");
            Require((string)typeof(SomsLegacyMainMenu).GetField("page", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main.Layer)! == "closed", "Stable menu must start with its cookie closed");
            snapshot("main-menu-closed-1280x720");
        });
        step("open main menu", () => click(main, "legacy-menu-logo"));
        step("native options from expanded menu", () =>
        {
            click(main, "legacy-menu-options");
            Require(optionsCalls == 1, "Options does not invoke the native callback");
            snapshot("main-menu-1280x720");
        });
        step("main menu play navigation", () => click(main, "legacy-menu-play"));
        step("solo and multiplayer native actions", () =>
        {
            click(main, "legacy-menu-solo");
            Require(soloCalls == 1, "Solo menu callback was lost");
            snapshot("play-menu-1280x720");
        });
        // Multiplayer now uses the real lazer menu. Its loaded controls and Back path
        // are exercised by LegacyScreens.Integration, not by the detached menu fixture.
        step("results", () => show(result));
        step("complete result metadata and score", () =>
        {
            Require(result.Native.All(d => d.Alpha == 0), "Classic results did not hide native layout");
            if (typeof(SomsLegacyComponent).Assembly.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.SomsLegacyInputPatch") != null)
                foreach (var native in result.Native)
                    Require(!(bool)Property(native.GetType(), "PropagateNonPositionalInputSubTree").GetValue(native)!, "Hidden native controls still receive keyboard focus");
            foreach (string name in new[] { "soms-result-map", "soms-result-difficulty", "soms-result-player", "soms-result-score", "soms-result-combo", "soms-result-accuracy" })
                visibleBounds(find(result, name), result.Root);
            visibleBounds(descendants(result.Layer).Single(d => d.Name is "soms-result-rank" or "soms-result-asset-ranking-A"), result.Root);
            Require(numberText(result, "soms-result-score") == "09876543", "Results do not show the actual score");
            var great = find(result, "soms-result-count-Great");
            var ok = find(result, "soms-result-count-Ok");
            var meh = find(result, "soms-result-count-Meh");
            var miss = find(result, "soms-result-count-Miss");
            Require(great.Y < ok.Y && ok.Y < meh.Y && Math.Abs(meh.Y - miss.Y) < 1 && miss.X > meh.X,
                "Stable osu! result columns or hit order are incorrect");
            Require(!descendants(result.Layer).Any(d => d.Name is "soms-result-count-Perfect" or "soms-result-count-Good"), "Fake osu! judgement counts were added");
            if (skinDirectory == null) Require(descendants(result.Layer).Any(d => d.Name == "soms-result-asset-ranking-panel"), "A supplied 1x1 ranking panel was replaced by fallback");
            verifyResultFont(result);
            Require(!allText(result).Contains("LargeTickHit") && !allText(result).Contains("SliderTailHit"), "Internal hit result identifiers leaked");
            snapshot("results-osu-1280x720");
        });
        foreach (string mode in new[] { "Taiko", "Catch", "Mania" })
        {
            step("set result mode " + mode, () => resultOwner.SelectedScore.Value = sampleScore(NativeRuleset(mode)));
            step("result statistics " + mode, () =>
            {
                Require(numberText(result, "soms-result-score") == "09876543", "Mode-specific results lost score");
                snapshot("results-" + mode.ToLowerInvariant() + "-1280x720");
            });
        }
        if (skinDirectory == null)
        {
            step("animated ranking panel before drawable load", () => { root.Skin.AnimatedRankingPanel = true; root.Skin.Change(); });
            step("animated ranking panel retains frame geometry", () =>
            {
                var panel = find(result, "soms-result-asset-ranking-panel");
                Require(panel.Width > 0 && panel.Height > 0 && float.IsFinite(panel.Width), "Animated panel read an unloaded or zero-size frame");
                root.Skin.AnimatedRankingPanel = false; root.Skin.Change();
            });
        }
        step("pause", () => show(pause));
        step("pause actions", () =>
        {
            click(pause, "soms-legacy-pause-retry");
            Require(retryCalls == 1, "Pause retry lost native callback");
            snapshot("pause-1280x720");
        });
        step("mods", () => show(mods));
        step("mod state changes", () =>
        {
            var dt = modOwner.AllAvailableMods.First(m => m.Mod.Acronym == "DT");
            click(mods, "soms-legacy-mod-DT");
            Require(dt.Active.Value && modOwner.SelectedMods.Value.Any(m => m.Acronym == "DT"), "DT selection does not reach native state");
            snapshot("mods-1280x720");
        });
        step("clear mods", () =>
        {
            click(mods, "soms-legacy-clear-mods");
            Require(!modOwner.AllAvailableMods.Any(m => m.Active.Value), "Clear mods left active mods behind");
            modOwner.AllAvailableMods.First(m => m.Mod.Acronym == "DT").ValidForSelection.Value = false;
            modOwner.AllAvailableMods.First(m => m.Mod.Acronym == "NC").ValidForSelection.Value = false;
        });
        step("free-mod permissions disable existing tile", () =>
        {
            var tile = find(mods, "soms-legacy-mod-DT");
            Require(tile.Alpha < .5f, "Invalid multiplayer mod is not visibly disabled");
            click(mods, "soms-legacy-mod-DT");
            Require(!modOwner.AllAvailableMods.First(m => m.Mod.Acronym == "DT").Active.Value, "Invalid mod remains selectable");
            modOwner.AllAvailableMods.First(m => m.Mod.Acronym == "DT").ValidForSelection.Value = true;
            modOwner.AllAvailableMods.First(m => m.Mod.Acronym == "NC").ValidForSelection.Value = true;
        });
        var song = scenes.FirstOrDefault(s => s.Name == "song-select");
        if (song != null)
        {
            step("song selection", () => show(song));
            step("song selection full layout", () =>
            {
                verifySelectionSkin(song);
                snapshot("song-select-1280x720");
            });
            step("open mode popup", () => click(song, "soms-selection-mode"));
            step("four classic rulesets", () =>
            {
                foreach (int mode in new[] { 0, 1, 2, 3 }) visibleBounds(find(song, "soms-selection-mode-" + mode), song.Root);
                snapshot("song-select-modes-1280x720");
            });
            step("close mode popup", () => click(song, "soms-selection-mode"));
            step("open map actions", () => click(song, "soms-selection-options"));
            step("numbered native map actions", () =>
            {
                for (int number = 1; number <= 6; number++) visibleBounds(find(song, "soms-selection-option-" + number), song.Root);
                snapshot("song-select-options-1280x720");
            });
            step("cancel map actions", () => click(song, "soms-selection-option-6"));
            step("wide default viewport", () => resize(1920, 1080));
            step("wide song select", () => snapshot("song-select-1920x1080"));
            step("restore default viewport", () => resize(1280, 720));
            if (skinDirectory != null)
            {
                step("hover skinned mod selection", () => Method(find(song, "soms-selection-mods").GetType(), "OnHover").Invoke(find(song, "soms-selection-mods"), new object[] { new HoverEvent(new InputState()) }));
                step("skinned selection hover artwork", () =>
                {
                    if (root.Skin.GetTexture("selection-mods-over", default, default) != null)
                        Require(find(song, "soms-selection-art-selection-mods-over").Alpha > .99f, "Authored hover texture did not appear");
                    snapshot("song-select-hover-1280x720");
                    Method(find(song, "soms-selection-mods").GetType(), "OnHoverLost").Invoke(find(song, "soms-selection-mods"), new object[] { new HoverLostEvent(new InputState()) });
                });
                step("large stable viewport", () => resize(1920, 1080));
                step("song selection actual skin at 1080p", () => { verifySelectionSkin(song); snapshot("song-select-1920x1080"); });
                step("large result viewport", () => { resultOwner.SelectedScore.Value = sampleScore(NativeRuleset("Osu")); show(result); });
                step("results actual skin at 1080p", () =>
                {
                    var back = find(result, "soms-legacy-results-back").ScreenSpaceDrawQuad.AABBFloat;
                    Require(back.Left < 10, "Results back button is still centred inside a 4:3 canvas");
                    snapshot("results-osu-1920x1080");
                });
                step("restore song viewport", () => { resize(1280, 720); show(song); });
            }
            step("legacy search accepts focus and updates native filter", () =>
            {
                var search = descendants(song.Layer).OfType<osu.Framework.Graphics.UserInterface.TextBox>().Single();
                Require(root.Focus.ChangeFocus(search), "Legacy search cannot receive keyboard focus");
                search.Current.Value = "playback";
                Require(nativeSongQuery.Value == "playback", "Legacy search did not reach the native map filter");
                root.Skin.Change();
            });
            step("search query survives a live skin reload", () =>
            {
                var search = descendants(song.Layer).OfType<osu.Framework.Graphics.UserInterface.TextBox>().Single();
                Require(search.Current.Value == "playback", "Skin reload lost the current search query");
                search.Current.Value = "";
            });
            step("locked ruleset cannot be changed from classic controls", () =>
            {
                modeChoices = new FixedRulesetStore(Ruleset.Value, NativeRuleset("Taiko").RulesetInfo);
                SetMember(song.Layer, "rulesets", modeChoices);
                int currentMode = Ruleset.Value.OnlineID;
                Ruleset.Disabled = true;
                try
                {
                    Method(song.Layer.GetType(), "cycleMode").Invoke(song.Layer, null);
                    Require(Ruleset.Value.OnlineID == currentMode, "Classic control changed a locked ruleset");
                }
                finally { Ruleset.Disabled = false; }
            });
        }
        step("multiplayer room browser", () => show(lounge));
        step("room selection", () => click(lounge, "soms-legacy-room-102"));
        step("room browser contents", () =>
        {
            Require(allText(lounge).Contains("Practice lobby"), "Room listing data was lost");
            snapshot("lounge-1280x720");
        });
        step("native room search changes", () => nativeRooms.Filter.Value = new LoungeFilterCriteria { SearchString = "Practice" });
        step("room rows follow changed native filter", () =>
        {
            Require(descendants(lounge.Layer).Any(d => d.Name == "soms-legacy-room-101"), "Matching room disappeared");
            Require(!descendants(lounge.Layer).Any(d => d.Name == "soms-legacy-room-102"), "Nonmatching room survived native filter change");
            nativeRooms.Filter.Value = null;
        });
        step("cleared native filter restores room rows", () => Require(descendants(lounge.Layer).Any(d => d.Name == "soms-legacy-room-102"), "Clearing room filter did not restore rows"));
        step("room browser suspension restores native immediately", () =>
        {
            Method(lounge.Layer.GetType(), "PauseLegacy").Invoke(lounge.Layer, null);
            Require(lounge.Layer.Alpha == 0 && loungeNativeVisual.Alpha == 1, "Suspending a room browser did not restore native chrome immediately");
            root.Skin.Change();
        });
        step("skin reload cannot resurrect suspended room browser", () =>
        {
            Require(lounge.Layer.Alpha == 0, "Skin reload showed a suspended room browser");
            Method(lounge.Layer.GetType(), "ResumeLegacy").Invoke(lounge.Layer, null);
        });
        step("room browser resumes once", () =>
        {
            Require(lounge.Layer.Alpha == 1 && loungeNativeVisual.Alpha == 0, "Room browser did not restore classic mode after resume");
            Require(descendants(lounge.Layer).Count(d => d.Name == "soms-legacy-room-list") == 1, "Room browser duplicated after resume");
        });
        step("missing skin textures", () => { texture.Parent.Alpha = 1; root.Skin.Available = false; root.Skin.Change(); });
        step("incomplete skin fallback", () =>
        {
            Require(texture.Alpha == 0 && original.Alpha == .75f, "Missing image did not restore original");
            Require(scenes.All(s => s.Layer.Alpha == 1), "Missing assets erased a legacy scene");
            if (skinDirectory != null) { root.Skin.Available = true; root.Skin.Change(); }
            resize(640, 480);
        });
        foreach (var scene in scenes)
        {
            step("show narrow " + scene.Name, () => show(scene));
            step("narrow " + scene.Name, () =>
            {
                Require(float.IsFinite(scene.Layer.DrawWidth) && scene.Layer.DrawWidth > 0, "Invalid narrow layout");
                snapshot(scene.Name + "-640x480");
            });
        }
        for (int i = 0; i < 30; i++)
        {
            int cycle = i;
            step("disable cycle " + cycle, () => { SomsClientPreferences.Instance.LegacyInterface.Value = false; root.Skin.Change(); });
            step("restore cycle " + cycle, () =>
            {
                Require(scenes.All(s => s.Layer.Alpha == 0), "Disable failed after live skin reload");
                Require(result.Native.All(d => d.Alpha == 1) && mods.Native.All(d => d.Alpha == 1), "Native visuals did not restore");
                if (cycle == 0)
                {
                    show(mods);
                    Require(root.Focus.ChangeFocus(nativeSearch), "Disabling legacy did not restore native keyboard focus");
                }
                SomsClientPreferences.Instance.LegacyInterface.Value = true;
                root.Skin.Available = cycle % 2 == 0; root.Skin.Change();
            });
            step("enabled cycle " + cycle, () => Require(scenes.All(s => s.Layer.Alpha == 1), "Enable failed after live skin reload"));
        }
        step("dispose with queued callbacks", () =>
        {
            root.Skin.Change();
            foreach (var scene in scenes) scene.Root.Remove(scene.Layer, true);
            ((Container)texture.Parent).Remove(texture, true);
            SomsClientPreferences.Instance.LegacyInterface.Value = false; root.Skin.Change();
        });
        step("restoration and persistence after disposal", () =>
        {
            Require(original.Alpha == .75f && result.Native.All(d => d.Alpha == 1), "Disposal did not restore native visuals");
            Require(!new SomsClientPreferences(Storage.GetFullPath(SomsClientPreferences.FileName)).LegacyInterface.Value, "Preference did not persist");
            Console.WriteLine("PASS: classic scenes, native actions, mod permissions, fallback, 30 skin/toggle cycles, disposal and saved preference.");
            Passed = true; Host.Exit();
        });
    }

    private Scene addScene(string name, SomsLegacyComponent layer, params Drawable[] native)
    {
        var container = new Container { Name = "fixture-" + name, RelativeSizeAxes = Axes.Both, AlwaysPresent = true, Alpha = 0 };
        foreach (var drawable in native) container.Add(drawable);
        container.Add(layer); root.Add(container);
        var scene = new Scene(name, container, layer, native); scenes.Add(scene); return scene;
    }
    private void show(Scene scene)
    {
        foreach (var candidate in scenes) candidate.Root.Alpha = candidate == scene ? 1 : 0;
    }
    private void resize(int width, int height)
    {
        if (visual) frameworkConfig.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(width, height));
        else root.Size = new Vector2(width, height);
    }
    private void step(string name, Action action) => steps.Enqueue((name, action));
    private double nextStep;
    protected override void Update()
    {
        base.Update();
        if (Passed || root == null || !root.IsLoaded || scenes.Any(s => !s.Layer.IsLoaded)) return;
        if (capture != null)
        {
            if (!capture.IsCompleted) return;
            capture.GetAwaiter().GetResult(); capture = null; frames = 0;
        }
        if (++frames < (visual ? 16 : 8)) return;
        if (Time.Current < nextStep) return;
        nextStep = Time.Current + 250;
        frames = 0;
        if (!steps.TryDequeue(out var next)) throw new Exception("Test exhausted without completion");
        Console.WriteLine("CHECK: " + next.Name); next.Action();
    }
    private void snapshot(string name) { if (visual) capture = saveScreenshot(name); }
    private async Task saveScreenshot(string name)
    {
        using var image = await Host.TakeScreenshotAsync();
        string path = Path.Combine(output, name + ".png");
        await image.SaveAsPngAsync(path);
        Console.WriteLine("SCREENSHOT: " + path + " " + image.Width + "x" + image.Height);
    }
    private static void click(Scene scene, string name)
    {
        var control = find(scene, name); visibleBounds(control, scene.Root);
        Require(control.ReceivePositionalInputAt(control.ScreenSpaceDrawQuad.Centre), name + " lost its input area");
        Method(control.GetType(), "OnClick").Invoke(control, new object[] { new ClickEvent(new InputState(), MouseButton.Left) });
    }
    private static void visibleBounds(Drawable drawable, Drawable viewport)
    {
        var bounds = drawable.ScreenSpaceDrawQuad.AABBFloat; var area = viewport.ScreenSpaceDrawQuad.AABBFloat;
        Require(bounds.Width > 0 && bounds.Height > 0 && float.IsFinite(bounds.X), drawable.Name + " has invalid geometry");
        Require(bounds.Right > area.Left && bounds.Left < area.Right && bounds.Bottom > area.Top && bounds.Top < area.Bottom, drawable.Name + " is outside viewport");
    }
    private static Drawable find(Scene scene, string name) => descendants(scene.Layer).Single(d => d.Name == name);
    private static string allText(Scene scene) => string.Join(" ", descendants(scene.Layer).OfType<SpriteText>().Select(d => d.Text.ToString()));
    private static string numberText(Scene scene, string name)
    {
        var number = find(scene, name);
        if (number is SpriteText text) return text.Text.ToString();
        return string.Concat(descendants((CompositeDrawable)number)
            .Where(d => d.Name.StartsWith(name + "-glyph-") || d.Name.StartsWith(name + "-fallback-"))
            .Select(d => d.Name.Split('-')).OrderBy(parts => int.Parse(parts[^1])).Select(parts => parts[^2]));
    }
    private void verifySelectionSkin(Scene song)
    {
        if (root.Skin.GetTexture("selection-mode", default, default) is { } modeTexture)
        {
            var art = find(song, "soms-selection-art-selection-mode");
            var bounds = art.ScreenSpaceDrawQuad.AABBFloat;
            float unit = song.Root.DrawHeight / 480;
            Require(Math.Abs(bounds.Left - 142 * unit) < 2, "Selection-mode origin disagrees with stable reference");
            Require(Math.Abs(bounds.Width - modeTexture.DisplayWidth * unit * (480f / 768)) < 2,
                "Selection-mode decoration is being stretched into a hitbox");
            Require(Math.Abs(bounds.Height - modeTexture.DisplayHeight * unit * (480f / 768)) < 2,
                "Selection-mode decoration lost its native aspect ratio");
            var button = find(song, "soms-selection-mode");
            if (bounds.Height > button.ScreenSpaceDrawQuad.AABBFloat.Height + 2)
                Require(!button.ReceivePositionalInputAt(bounds.TopRight - new Vector2(1, -1)), "Decorative overflow expanded the selection-mode hitbox");
        }
        foreach (string asset in new[] { "selection-mods", "selection-random", "selection-options" })
        {
            if (root.Skin.GetTexture(asset, default, default) is not { } texture || texture.DisplayWidth > 1 || texture.DisplayHeight > 1) continue;
            var button = (CompositeDrawable)find(song, "soms-" + asset);
            Require(!descendants(button).Any(d => d is Box or SpriteText), "Intentional 1x1 " + asset + " was replaced by visible fallback");
        }
    }
    private void verifyResultFont(Scene scene)
    {
        string prefix = root.Skin.GetFontPrefix(LegacyFont.Score);
        if (root.Skin.GetTexture(prefix + "-0", default, default) == null) return;
        var flow = (FillFlowContainer)find(scene, "soms-result-score-glyphs");
        Require(Math.Abs(flow.Spacing.X + root.Skin.GetFontOverlap(LegacyFont.Score)) < .001f, "ScoreOverlap from skin.ini was ignored");
        foreach (var glyph in descendants(scene.Layer).OfType<Sprite>().Where(d => d.Name.StartsWith("soms-result-score-glyph-")))
        {
            Require(glyph.Texture != null && Math.Abs(glyph.DrawHeight - glyph.Texture.DisplayHeight) < .01f,
                "Score digits were individually resized instead of preserving the skin font");
        }
        if (root.Skin.GetTexture(prefix + "-x", default, default) == null)
        {
            Require(descendants(scene.Layer).Any(d => d.Name.StartsWith("soms-result-combo-fallback-x-")), "Missing x suffix did not fall back separately");
            Require(descendants(scene.Layer).OfType<Sprite>().Any(d => d.Name.StartsWith("soms-result-combo-glyph-")), "A missing x discarded skinned combo digits");
        }
        if (root.Skin.GetTexture(prefix + "-percent", default, default) is { } percent && percent.DisplayWidth > 500)
        {
            var accuracy = (FillFlowContainer)find(scene, "soms-result-accuracy-glyphs");
            var combo = (FillFlowContainer)find(scene, "soms-result-combo-glyphs");
            Require(accuracy.Scale.X >= combo.Scale.X * .75f, "Oversized decorative percent sprite shrank the accuracy digits");
        }
    }
    private static IEnumerable<Drawable> descendants(CompositeDrawable value)
    {
        foreach (var child in (IEnumerable<Drawable>)Property(typeof(CompositeDrawable), "InternalChildren").GetValue(value)!)
        {
            yield return child;
            if (child is CompositeDrawable composite) foreach (var descendant in descendants(composite)) yield return descendant;
        }
    }
    private ScoreInfo sampleScore(Ruleset ruleset) => new(Beatmap.Value.BeatmapInfo, ruleset.RulesetInfo)
    {
        User = new APIUser { Id = 99, Username = "Legacy interface check" },
        TotalScore = 9876543, MaxCombo = 321, Accuracy = .9876, Rank = ScoreRank.A, Date = new DateTimeOffset(2026, 9, 8, 12, 30, 0, TimeSpan.Zero),
        Statistics = new Dictionary<osu.Game.Rulesets.Scoring.HitResult, int>
        {
            [osu.Game.Rulesets.Scoring.HitResult.Great] = 321, [osu.Game.Rulesets.Scoring.HitResult.Ok] = 12,
            [osu.Game.Rulesets.Scoring.HitResult.Meh] = 3, [osu.Game.Rulesets.Scoring.HitResult.Miss] = 2,
            [osu.Game.Rulesets.Scoring.HitResult.LargeTickHit] = 40, [osu.Game.Rulesets.Scoring.HitResult.SliderTailHit] = 27,
        },
    };
    private static Ruleset NativeRuleset(string mode) => (Ruleset)Activator.CreateInstance(AppDomain.CurrentDomain.GetAssemblies()
        .Single(a => a.GetName().Name == "osu.Game.Rulesets." + mode).GetType("osu.Game.Rulesets." + mode + "." + mode + "Ruleset", true)!)!;
    private static PropertyInfo Property(Type type, string name) => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? (type.BaseType != null ? Property(type.BaseType, name) : throw new MissingMemberException(type.Name, name));
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        ?? (type.BaseType != null ? Method(type.BaseType, name) : throw new MissingMemberException(type.Name, name));
    private static void SetMember(object owner, string name, object value)
    {
        for (Type? type = owner.GetType(); type != null; type = type.BaseType)
        {
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field) { field.SetValue(owner, value); return; }
            if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } property) { property.SetValue(owner, value); return; }
        }
        throw new MissingMemberException(owner.GetType().Name, name);
    }
    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        foreach (var owner in owners) owner.Dispose();
        modeChoices?.Dispose();
    }
    private void addSongSelect()
    {
        var owner = new SoloSongSelect();
        owners.Add(owner);
        var carousel = new BeatmapCarousel { RequestSelection = _ => { }, RequestRecommendedSelection = _ => { } };
        SetMember(owner, "carousel", carousel);
        var filter = new FilterControl();
        var searchType = typeof(FilterControl).GetField("searchTextBox", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType;
        nativeSongSearch = Activator.CreateInstance(searchType, nonPublic: true)!;
        SetMember(filter, "searchTextBox", nativeSongSearch);
        SetMember(owner, "FilterControl", filter);
        var native = new Container { RelativeSizeAxes = Axes.Both };
        SetMember(owner, "mainContent", native);
        var layer = new SomsLegacySongSelect(owner);
        var filtered = (List<GroupedBeatmap>)typeof(SomsLegacySongSelect).GetField("filtered", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(layer)!;
        filtered.Add(new GroupedBeatmap(null, Beatmap.Value.BeatmapInfo));
        for (int i = 0; i < 7; i++)
        {
            var map = new BeatmapInfo(nativeRuleset.RulesetInfo)
            {
                DifficultyName = new[] { "Easy", "Normal", "Hard", "Insane", "Extra", "Another", "Final" }[i],
                BeatmapSet = i < 2 ? Beatmap.Value.BeatmapInfo.BeatmapSet : new BeatmapSetInfo(),
                StarRating = 1.5 + i * .6,
                Metadata = new BeatmapMetadata { Artist = "Test artist " + (i + 1), Title = "Collection sample " + (i + 1) },
            };
            map.Metadata.Author.Username = "Test mapper";
            filtered.Add(new GroupedBeatmap(null, map));
        }
        addScene("song-select", layer, native);
    }
    private void addLounge()
    {
        var owner = new MultiplayerLoungeSubScreen();
        owners.Add(owner);
        loungeNativeVisual = new Box { Size = new Vector2(200, 80) };
        Method(typeof(CompositeDrawable), "AddInternal").Invoke(owner, new object[] { loungeNativeVisual });
        var listing = nativeRooms = new RoomListing();
        SetMember(owner, "roomListing", listing);
        SetMember(owner, "hasListingResults", new Bindable<bool>(true));
        SetMember(owner, "searchTextBox", new osu.Game.Graphics.UserInterface.SearchTextBox());
        listing.Rooms.Add(new Room { RoomID = 101, Name = "Practice lobby", ParticipantCount = 2, MaxParticipants = 8, Host = new APIUser { Id = 100, Username = "Test host" } });
        listing.Rooms.Add(new Room { RoomID = 102, Name = "Tournament warmup", ParticipantCount = 4, MaxParticipants = 4, Host = new APIUser { Id = 101, Username = "Second host" } });
        listing.Rooms.Add(new Room { RoomID = 103, Name = "Private training", ParticipantCount = 1, MaxParticipants = 2, Password = "fixture-only", Host = new APIUser { Id = 102, Username = "Third host" } });
        lounge = addScene("lounge", new SomsLegacyLounge(owner));
    }
    private sealed record Scene(string Name, Container Root, SomsLegacyComponent Layer, Drawable[] Native);
}

internal sealed class FixedRulesetStore : RulesetStore
{
    private readonly RulesetInfo[] choices;
    public FixedRulesetStore(params RulesetInfo[] choices) => this.choices = choices;
    public override IEnumerable<RulesetInfo> AvailableRulesets => choices;
}

internal sealed partial class FixtureRoot : Container
{
    public FixtureSkin Skin { get; } = new();
    private readonly string? skinDirectory;
    public FixtureRoot(string? skinDirectory) => this.skinDirectory = skinDirectory;
    public IFocusManager Focus => GetContainingFocusManager();
    public InputManager Input => GetContainingInputManager();
    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
        dependencies.CacheAs<ISkinSource>(Skin);
        dependencies.Cache(new OverlayColourProvider(OverlayColourScheme.Blue));
        dependencies.CacheAs<IBindable<SongSelect.BeatmapSetLookupResult?>>(new Bindable<SongSelect.BeatmapSetLookupResult?>());
        return dependencies;
    }
    [BackgroundDependencyLoader]
    private void load(IRenderer renderer, SkinManager skinManager)
    {
        Skin.Texture = renderer.WhitePixel;
        if (skinDirectory != null)
            Skin.DirectorySkin = new DirectorySkin(skinDirectory, skinManager);
    }
    protected override void Dispose(bool isDisposing) { base.Dispose(isDisposing); Skin.DirectorySkin?.Dispose(); }
}
internal sealed class DirectorySkin : LegacySkin
{
    // Read existing assets directly; no import and no writes to the user's skin/profile.
    public DirectorySkin(string directory, IStorageResourceProvider resources)
        : base(new SkinInfo { Name = Path.GetFileName(directory) }, resources, new StorageBackedResourceStore(new NativeStorage(directory))) { }
}
internal sealed class FixtureSkin : ISkinSource
{
    public bool Available = true;
    public Texture Texture = null!;
    public DirectorySkin? DirectorySkin;
    public bool AnimatedRankingPanel;
    public event Action? SourceChanged;
    public void Change() => SourceChanged?.Invoke();
    public IEnumerable<ISkin> AllSources => DirectorySkin == null ? new[] { (ISkin)this } : new ISkin[] { DirectorySkin, this };
    public ISkin? FindProvider(Func<ISkin, bool> lookupFunction) => !Available ? null : AllSources.FirstOrDefault(lookupFunction);
    public Drawable? GetDrawableComponent(ISkinComponentLookup lookup) => null;
    public ISample? GetSample(ISampleInfo sampleInfo) => null;
    public IBindable<TValue>? GetConfig<TLookup, TValue>(TLookup lookup) where TLookup : notnull where TValue : notnull => Available ? DirectorySkin?.GetConfig<TLookup, TValue>(lookup) : null;
    public Texture? GetTexture(string name, WrapMode s, WrapMode t)
    {
        if (!Available) return null;
        if (name is "soms-fixture-0" or "soms-fixture-1") return Texture;
        if (AnimatedRankingPanel && name is "ranking-panel-0" or "ranking-panel-1") return Texture;
        return DirectorySkin != null ? DirectorySkin.GetTexture(name, s, t) : name is "menu-back" or "ranking-panel" ? Texture : null;
    }
}
