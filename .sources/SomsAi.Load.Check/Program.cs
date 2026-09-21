using System.Reflection;
using System.Runtime.Loader;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Online.Matchmaking;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.EnhancedAuth;
using osu.Game.Screens;
using osuTK;
using SixLabors.ImageSharp;

internal static class Program
{
    public static int Main(string[] args) => QuietTestProcess.Run(() => MainImpl(args));
    private static int MainImpl(string[] args)
    {
        // Load every game/framework dependency from the installed client, not NuGet.
        string client = Path.GetFullPath(args[0]);
        string plugin = Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = name.Name == "osu.Game.Rulesets.EnhancedAuth" ? plugin : Path.Combine(client, name.Name + ".dll");
            if (!File.Exists(path))
                path = Path.Combine(Path.GetDirectoryName(plugin)!, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        // The production loader injects the ruleset assembly before OsuGameBase is
        // constructed. Mirror that ordering in this standalone host.
        AssemblyLoadContext.Default.LoadFromAssemblyPath(plugin);
        string output = Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--output="))?[9..] ?? ".test-tmp/somsai-ocean-check");
        Directory.CreateDirectory(output);
        return Run(args.Contains("--visual"), args.Contains("--dashboard-only"), output);
    }

    private static int Run(bool visual, bool dashboardOnly, string output)
    {
        string name = "somsai-load-" + Guid.NewGuid().ToString("N");
        using GameHost host = visual ? Host.GetSuitableDesktopHost(name, new HostOptions { PortableInstallation = true, FriendlyGameName = "SOMSAI UI verification" }) : new HeadlessGameHost(name);
        using var game = new LoadGame(visual, dashboardOnly, output, Path.Combine(output, "profile", name));
        Exception? failure = null;
        host.ExceptionThrown += exception => { Console.Error.WriteLine(exception); failure = exception; host.Exit(); return true; };
        using var timeout = new Timer(_ => host.Exit(), null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        host.Run(game);
        if (failure != null) throw failure;
        if (!game.Passed) throw new Exception("Full drawable load/layout did not finish within 90 seconds");
        return 0;
    }
}

internal sealed partial class LoadGame : OsuGameBase
{
    public bool Passed { get; private set; }
    private readonly DialogOverlay dialogs = new();
    private DependencyContainer dependencies = null!;
    private OsuScreenStack stack = null!;
    private osu.Game.Graphics.UserInterface.ShearedButton controls = null!;
    private readonly Bindable<MatchmakingPool?> nativeSelected = new();
    private readonly Bindable<bool> nativeConnected = new(true);
    private Container queuePanel = null!;
    private int stage;
    private int frames;
    private int avatarWait;
    private SomsAiScreen dashboard = null!;
    private readonly bool visual;
    private readonly bool dashboardOnly;
    private readonly string output, profile;
    private Task? capture;
    private Action? afterCapture;
    private Vector2 firstBubble;
    private Drawable? suspendedBackdrop;
    private double suspendedAnimationTime;
    private osu.Game.Overlays.Dialog.PopupDialog? confirmation;
    private Container? bubbleTransition;
    private bool transitionReady;
    private int transitionSwitches;
    private int transitionCoverageSize;
    private Container? oceanPreview;
    private float oceanStartY;
    private readonly Vector2[] transitionSizes = { new(1024, 768), new(2560, 1080), new(720, 1280), new(1280, 720) };

    public LoadGame(bool visual, bool dashboardOnly, string output, string profile)
    {
        this.visual = visual; this.dashboardOnly = dashboardOnly; this.output = output; this.profile = profile;
        API = new DummyAPIAccess();
        API.LocalUser.Value.Username = "ADmNH_PAZyMA";
        if (dashboardOnly) ((DummyAPIAccess)API).HandleRequest = request =>
        {
            if (request is GetSomsAiStateRequest) return true;
            if (request is not SomsAiDataRequest data) return false;
            string path = (string)typeof(SomsAiDataRequest).GetField("path", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(data)!;
            string json = path.StartsWith("matches/") ? """
                {"id":51,"format":"1v1","stage":"ended","wins":[4,2],"best_of":7,
                 "ruleset_id":0,
                 "teams":[{"id":0,"members":[{"id":1001,"username":"Captain","country_code":"RU"}]},{"id":1,"members":[{"id":2,"username":"Rival [BOT]","country_code":"US","is_bot":true}]}],
                 "slots":[{"id":"NM1","label":"NM1","artist":"Artist","title":"Test map","version":"Insane"},{"id":"HD1","label":"HD1","title":"Banned map"}],
                 "draft_history":[{"action":"ban","slot_id":"HD1","team_id":1}],
                 "history":[{"round":1,"slot_id":"NM1","picked_by_team":0,"winner_team_id":0,"team_scores":[950000,800000],"players":[{"user_id":1001,"score":950000,"accuracy":0.9876,"max_combo":638,"rank":"S","statistics":{"great":543,"ok":12,"meh":1,"miss":0}},{"user_id":2,"score":800000,"accuracy":0.9421,"max_combo":419,"rank":"A","is_bot":true,"statistics":{"great":510,"ok":39,"meh":7,"miss":2}}]}]}
                """ : path == "party/chat" ? """
                {"party_id":9,"messages":[{"message_id":1,"sender":{"id":1001,"username":"Captain","avatar_url":"/users/1001/avatar"},"content":"Ready for 2v2!","timestamp":"2026-09-17T12:00:00Z"}]}
                """ : """
                {"items":[{"rank":1,"rating":3100,"wins":10,"losses":2,"user":{"id":2,"username":"Leader","avatar_url":"/users/2/avatar"}}],
                 "self":{"rank":52,"rating":1500,"wins":4,"losses":2,"user":{"id":1001,"username":"Captain","avatar_url":"/users/1001/avatar"}},"has_more":true}
                """;
            typeof(APIRequest<Newtonsoft.Json.Linq.JObject>).GetMethod("TriggerSuccess", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
                .Invoke(data, new object[] { Newtonsoft.Json.Linq.JObject.Parse(json) });
            return true;
        };
    }
    protected override int UnhandledExceptionsBeforeCrash => 0;
    protected override Storage CreateStorage(GameHost host, Storage defaultStorage) => host.GetStorage(profile);
    protected override Container CreateScalingContainer() => new Container { RelativeSizeAxes = Axes.Both };
    protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults() => new Dictionary<FrameworkSetting, object>
    {
        [FrameworkSetting.WindowMode] = WindowMode.Windowed,
        [FrameworkSetting.VolumeUniversal] = 0.0, [FrameworkSetting.VolumeMusic] = 0.0, [FrameworkSetting.VolumeEffect] = 0.0,
        [FrameworkSetting.WindowedSize] = new System.Drawing.Size(1280, 720),
    };

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
        dependencies.CacheAs<IDialogOverlay>(dialogs);
        dependencies.Cache(new OverlayColourProvider(OverlayColourScheme.Plum));
        return dependencies;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (dashboardOnly) Add((Drawable)API);
        Ruleset.Value = RulesetStore.AvailableRulesets.FirstOrDefault() ?? new EnhancedAuthRuleset().RulesetInfo;
        dialogs.Depth = -100;
        Add(dialogs);
        Add(stack = new OsuScreenStack { RelativeSizeAxes = Axes.Y, Width = 1280 });
        stack.Push(dashboard = new SomsAiScreen());
    }

    protected override void Update()
    {
        base.Update();
        if (capture != null)
        {
            if (!capture.IsCompleted) return;
            capture.GetAwaiter().GetResult(); capture = null; frames = 0;
            var after = afterCapture; afterCapture = null; after?.Invoke();
            return;
        }
        if (stack?.CurrentScreen is not Drawable { IsLoaded: true }) return;
        if (dashboardOnly && stage is 104 or 106)
        {
            var current = (Drawable)stack.CurrentScreen;
            if (current.GetType().Name != "SomsAiRecordsScreen") return;
            if (stage == 104 && ((OsuScreen)current).Title.Contains("Матч")) return;
            if (stage == 106 && !((OsuScreen)current).Title.Contains("Матч")) return;
            if (current.GetType().GetField("request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current) != null) return;
        }
        if (dashboardOnly && stage == 105 && !ReferenceEquals(stack.CurrentScreen, dashboard)) return;
        if (++frames < (visual ? 35 : 15)) return;
        frames = 0;
        switch (stage++)
        {
            case 0:
                Console.WriteLine("PASS: full SOMSAI screen and child controls loaded by real game host.");
                checkOcean();
                firstBubble = descendants(dashboard).First(d => d.Name == "somsai-ocean-bubble").Position;
                setDashboardState("""
                    {"ratings":{"1v1":{"rating":1500,"rank":123,"wins":45,"losses":20},"2v2":{"rating":1550,"rank":90}},
                     "customs":[{"id":12,"name":"A very long tournament name to check wrapping in narrow windows","format":"4v4","participants":2,"capacity":8,"target_mmr":1500,"teams":[1,1]}],
                     "recent_matches":[
                       {"id":51,"format":"1v1","outcome":"win","wins":[4,2],"local_team_id":0,"opponents":["Rival"],"rating_delta":18,"ranked":true,"ended_at":"2026-09-18T18:24:00Z"},
                       {"id":50,"format":"2v2","outcome":"loss","wins":[1,4],"local_team_id":0,"opponents":["Alpha","Beta"],"rating_delta":-12,"ranked":true,"ended_at":"2026-09-18T17:11:00Z"}]}
                    """);
                break;
            case 1:
                checkDashboardBounds();
                if (descendants(dashboard).Count(d => d.Name == "somsai-recent-match") != 2)
                    throw new Exception("Recent match cards were not rendered");
                if (!descendants(dashboard).OfType<osu.Framework.Graphics.Sprites.SpriteText>().Any(t => t.Text.ToString().Contains("18.09.2026")))
                    throw new Exception("Recent match cards must show the played-at time");
                var partyBoard = descendants(dashboard).Single(d => d.Name == "somsai-party");
                var historyBoard = descendants(dashboard).Single(d => d.Name == "somsai-recent-matches");
                var searchBoard = descendants(dashboard).Single(d => d.Name == "somsai-matchmaking-actions");
                if (!(partyBoard.X < historyBoard.X && historyBoard.X < searchBoard.X)) throw new Exception("Party/history/search columns are out of order");
                if (!descendants(dashboard).Any(d => d.Name == "somsai-rank-gold1")) throw new Exception("1500 MMR must show GOLD I plaque");
                if (descendants(dashboard).OfType<osu.Game.Graphics.Sprites.OsuSpriteText>().Any(t => t.Text.ToString() == "Капитан")) throw new Exception("Solo player must not have a captain label");
                descendants(dashboard).OfType<ClickableContainer>().First(d => d.Name == "somsai-ocean-bubble").TriggerClick();
                Console.WriteLine("PASS: party/history/search columns, GOLD I plaque and clickable bubble reactions loaded.");
                if (firstBubble == descendants(dashboard).First(d => d.Name == "somsai-ocean-bubble").Position) throw new Exception("Ocean particles are not animated");
                snapshot("01-dashboard", () => stack.Width = 640);
                break;
            case 2:
                checkDashboardBounds();
                if (dashboardOnly)
                {
                    stack.Width = 1280;
                    setDashboardState("""
                        {"ratings":{"1v1":{"rating":1500,"rank":52}},
                         "party":{"id":9,"captain_id":1001,"members":[{"id":1001,"username":"Captain"},{"id":3,"username":"Friend","ratings":{"1v1":{"rating":2800}}}]},
                         "customs":[{"id":12,"name":"SOMSAI Tournament Room","format":"4v4","participants":2,"capacity":8,"target_mmr":1500,"teams":[1,1]}],
                         "recent_matches":[{"id":51,"format":"1v1","outcome":"win","wins":[4,2],"local_team_id":0,"opponents":["Rival [bot abcdef12]"]}]}
                        """);
                    stage = 100;
                    break;
                }
                snapshot("02-dashboard-narrow", () =>
                {
                    stack.Width = 1280;
                    foreach (string name in new[] { "customForm", "inviteForm" })
                        ((Drawable)typeof(SomsAiScreen).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dashboard)!).Show();
                });
                break;
            case 3:
                checkDashboardBounds();
                var voteState = Newtonsoft.Json.JsonConvert.DeserializeObject<SomsAiState>("""
                    {"match":{"id":42,"stage":"pool_select","pool_selected":false,"target_mmr":1500,"format":"2v2",
                      "teams":[{"id":0,"captain_id":0,"members":[{"id":0,"username":"Captain"}]}],
                      "pool_candidates":[{"id":1,"name":"The longest tournament title that must wrap rather than escape its card","best_of":9,"map_count":15,"average_stars":6.25},
                                         {"id":2,"name":"Another tournament","best_of":7,"map_count":13,"average_stars":5.5}]}}
                    """)!;
                snapshot("03-forms", () => stack.Push(dashboard = new SomsAiMatchScreen(voteState)));
                break;
            case 4:
                checkDashboardBounds();
                checkOcean();
                var matchState = new SomsAiState { Match = new SomsAiMatch
                {
                    Id = 42, Format = "2v2", Stage = "banning", PoolSelected = true, PoolName = "Tournament", TurnUserId = 0,
                    Teams = new() { new() { Id = 0, Name = "Blue", Members = new() { new() { Id = 0, Username = "Captain" } } }, new() { Id = 1, Name = "Red" } },
                    Slots = Enumerable.Range(1, 15).Select(i => new SomsAiSlot { Id = "NM" + i, BeatmapId = i, BeatmapSetId = 1, Category = "NM", Title = "A long song title to test truncation on map cards", Stars = 5.25 }).ToList(),
                } };
                snapshot("04-pool-choice", () => { setDashboardState(Newtonsoft.Json.JsonConvert.SerializeObject(matchState)); stack.Width = 1280; });
                break;
            case 5:
                checkMapBoard();
                snapshot("05-draft", () => stack.Width = 640);
                break;
            case 6:
                checkMapBoard();
                var preview = descendants(dashboard).OfType<osu.Game.Overlays.BeatmapSet.Buttons.PreviewButton>().First();
                var previewDependencies = (IReadOnlyDependencyContainer)typeof(CompositeDrawable).GetProperty("Dependencies", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(preview)!;
                if (!ReferenceEquals(previewDependencies.Get<osu.Game.Audio.IPreviewTrackOwner>(), dashboard))
                    throw new Exception("Audio previews must be owned by the match screen");
                preview.TriggerClick();
                typeof(SomsAiScreen).GetMethod("stopPreviews", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dashboard, null);
                if (preview.Playing.Value || preview.BeatmapSet != null) throw new Exception("Leaving while a preview loads must stop and invalidate it");
                Console.WriteLine("PASS: real preview click, owner injection and cancellation before asynchronous audio load completes.");
                snapshot("06-draft-narrow", () => stack.Push(dashboard = new SomsAiScreen(true)));
                break;
            case 7:
                Console.WriteLine("PASS: full party screen loaded by real game host.");
                checkOcean();
                snapshot("07-party", () =>
                {
                    suspendedBackdrop = descendants(dashboard).First(d => d.Name == "somsai-ocean-background");
                    suspendedAnimationTime = (double)suspendedBackdrop.GetType().GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(suspendedBackdrop)!;
                    stack.Push(new SomsTeamRankedPlayScreen(42));
                });
                break;
            case 8:
                Console.WriteLine("PASS: full Ranked 2v2 screen and native hand loaded by real game host.");
                if ((double)suspendedBackdrop!.GetType().GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(suspendedBackdrop)! != suspendedAnimationTime)
                    throw new Exception("Suspended SOMSAI background kept animating");
                Console.WriteLine("PASS: ocean animation stops after leaving the SOMSAI screen.");
                var nativeType = typeof(osu.Game.Screens.OnlinePlay.Matchmaking.Queue.ScreenQueue).GetNestedType("BeginQueueingButton", BindingFlags.NonPublic)!;
                controls = (osu.Game.Graphics.UserInterface.ShearedButton)Activator.CreateInstance(nativeType)!;
                ((IBindable<MatchmakingPool?>)nativeType.GetField("SelectedPool")!.GetValue(controls)!).BindTo(nativeSelected);
                controls.Enabled.BindTo(nativeConnected);
                controls.Action = () => throw new Exception("Disabled search cannot run");
                typeof(osu.Game.Rulesets.EnhancedAuth.Patches.SomsTeamRankedScreenPatch).GetMethod("DisableSearch", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { controls });
                Add(queuePanel = new Container { Size = new Vector2(620, 210), Child = controls });
                break;
            case 9:
                if (!controls.IsLoaded) { stage--; return; }
                checkQueueBounds();
                queuePanel.Size = new Vector2(360, 190);
                break;
            case 10:
                checkQueueBounds();
                queuePanel.Hide(); stack.Width = 1280;
                stack.Push(dashboard = new SomsAiScreen());
                break;
            case 11:
                setDashboardState("""{"ratings":{"2v2":{"rating":2000,"rank":1}},"queue":{"format":"2v2"}}""");
                break;
            case 12:
                checkDashboardBounds(); checkOcean();
                snapshot("08-searching", () => stack.Push(dashboard = new SomsAiMatchScreen(new SomsAiState
                {
                    Match = new SomsAiMatch { Id = 42, Format = "2v2", Stage = "ready", PoolSelected = true, PoolName = "Пул SOMSAI", TargetMmr = 1942,
                        Teams = new() { new() { Id = 0, Name = "Морские котики", Members = new() { new() { Id = 0, Username = "ADmNH_PAZyMA" } } }, new() { Id = 1, Name = "Рыбы-шары", Members = new()
                        {
                            new() { Id = 8000, Username = "mrekk [BOT]", OfficialId = 7562902, OfficialUsername = "mrekk", IsBot = true, BotLevel = "mrekk", AvatarUrl = "https://a.ppy.sh/7562902" },
                            new() { Id = 8001, Username = "Reef Hunter [BOT]", IsBot = true, BotLevel = "hard" },
                        } } } },
                })));
                break;
            case 13:
                // Remote avatar loading is deliberately asynchronous; capture after it can finish.
                if (++avatarWait < 6) { stage--; return; }
                checkOcean(); checkDashboardBounds();
                if (descendants(dashboard).Count(drawable => drawable.Name == "somsai-team-player") != 3)
                    throw new Exception("Both human and bot roster entries must have clickable profile rows");
                snapshot("09-ready", () => setDashboardState("""
                    {"match":{"id":42,"format":"2v2","stage":"ended","winner_team_id":0,"wins":[4,2],"target_mmr":1942,"pool_selected":true,
                    "pool_name":"Пул SOMSAI","teams":[{"id":0,"name":"Морские котики","members":[{"id":0,"username":"ADmNH_PAZyMA"}]},{"id":1,"name":"Рыбы-шары"}],
                    "rating_changes":[{"user_id":0,"before":1942,"after":1960,"delta":18,"impact":75}]}}
                    """));
                break;
            case 14:
                checkOcean(); checkDashboardBounds();
                var status = (osu.Game.Graphics.Sprites.OsuSpriteText)typeof(SomsNativeMatchScreen).GetField("StatusText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dashboard)!;
                if (status.Text.ToString() != "Матч завершён") throw new Exception("Result screen must keep its match status");
                snapshot("10-result", () => { });
                break;
            case 15:
                confirmation = (osu.Game.Overlays.Dialog.PopupDialog)Activator.CreateInstance(typeof(SomsAiScreen).Assembly.GetType("osu.Game.Rulesets.EnhancedAuth.UI.SomsAiOceanConfirmDialog")!,
                    "Сдаться? Вашей команде будет засчитано поражение.", (Action)(() => throw new Exception("Cancel confirmed a destructive action")))!;
                dialogs.Push(confirmation);
                break;
            case 16:
                if (confirmation?.IsLoaded != true) throw new Exception("Ocean confirmation failed to load");
                snapshot("11-confirmation", () => confirmation.Buttons.Last().TriggerClick());
                break;
            case 17:
                if (confirmation!.State.Value != Visibility.Hidden) throw new Exception("Cancel did not close ocean confirmation");
                Console.WriteLine("PASS: ocean confirmation loads and cancellation preserves the existing action semantics.");
                var transitionType = typeof(SomsAiScreen).Assembly.GetType("osu.Game.Rulesets.EnhancedAuth.UI.SomsAiBubbleTransition")!;
                bubbleTransition = (Container)Activator.CreateInstance(transitionType, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { (Action)(() => transitionSwitches++), (Func<bool>)(() => transitionReady) }, null)!;
                Add(bubbleTransition);
                break;
            case 18:
                if (transitionSwitches == 0) { stage--; return; }
                if (transitionSwitches != 1 || bubbleTransition!.Children.Count(d => d.Name == "somsai-transition-bubble") != 104)
                    throw new Exception("Transition must switch once with a bounded particle count");
                if (bubbleTransition.Children.First(d => d.Name == "somsai-transition-water").Alpha != 1)
                    throw new Exception("Screen switch must be fully covered");
                checkBubbleCoverage();
                if (transitionCoverageSize < transitionSizes.Length)
                {
                    bubbleTransition.RelativeSizeAxes = Axes.None;
                    bubbleTransition.Size = transitionSizes[transitionCoverageSize++];
                    stage--; return;
                }
                snapshot("12-bubble-cover", () => { });
                break;
            case 19:
                if (!bubbleTransition!.IsPresent || bubbleTransition.Children.First(d => d.Name == "somsai-transition-water").Alpha != 1)
                    throw new Exception("Transition reveals an unready destination");
                transitionReady = true;
                break;
            case 20:
                if (bubbleTransition!.IsAlive) { stage--; return; }
                if (transitionSwitches != 1) throw new Exception("Transition repeated its navigation action");
                Console.WriteLine("PASS: bubble curtain covers before navigation, waits for destination, reveals and releases input.");
                stack.Push(dashboard = new SomsAiScreen());
                break;
            case 21:
                if (!dashboard.IsLoaded) { stage--; return; }
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var toggle = (FormCheckBox)typeof(SomsAiScreen).GetField("customWithBots", flags)!.GetValue(dashboard)!;
                var levels = (FormDropdown<string>)typeof(SomsAiScreen).GetField("customBotLevel", flags)!.GetValue(dashboard)!;
                if (levels.Items.Count() != 5 || !levels.Items.Any(label => label.Contains("Топ 1000")) || levels.Items.Any(label => label.Contains("Невозможно")))
                    throw new Exception("Five bot levels must expose the merged top-1000 tier");
                if (toggle.Current.Value) throw new Exception("Ordinary customs must remain the default");
                toggle.Current.Value = true;
                levels.Current.Value = SomsAiBotSimulation.Labels[4];
                var form = (FillFlowContainer)typeof(SomsAiScreen).GetField("customForm", flags)!.GetValue(dashboard)!;
                typeof(SomsAiScreen).GetMethod("revealForm", flags)!.Invoke(dashboard, new object[] { form });
                break;
            case 22:
                checkDashboardBounds();
                snapshot("13-custom-bots", () => { });
                break;
            case 23:
                Console.WriteLine("PASS: custom creation defaults to human players and exposes five bot tiers including Top 1000.");
                oceanPreview = (Container)Activator.CreateInstance(typeof(SomsAiScreen).Assembly.GetType("osu.Game.Rulesets.EnhancedAuth.UI.SomsAiOceanBackdrop")!,
                    new object[] { (Func<bool>)(() => true) })!;
                oceanPreview.Depth = -500;
                Add(oceanPreview);
                break;
            case 24:
                setOceanTime(2);
                break;
            case 25:
                oceanStartY = firstOceanBubble().Y;
                snapshot("14-ocean-animation-start", () => setOceanTime(12));
                break;
            case 26:
                if (firstOceanBubble().Y >= oceanStartY - 100) throw new Exception("Decorative background bubbles must rise visibly over time");
                snapshot("15-ocean-animation-later", () => setOceanTime(43));
                break;
            case 27:
                if (firstOceanBubble().Y > 30) throw new Exception("Bubble did not reach the top before recycling");
                setOceanTime(45.1);
                break;
            case 28:
                if (firstOceanBubble().Y < oceanPreview!.DrawHeight * .85f) throw new Exception("Bubble did not respawn below the bottom after its cycle");
                Console.WriteLine("PASS: decorative background bubbles rise, leave the top and recycle at the bottom.");
                Passed = true;
                Exit();
                break;
            case 100:
                checkDashboardBounds();
                if (descendants(dashboard).OfType<osu.Framework.Graphics.Sprites.SpriteText>().Count(t => t.Text.ToString() == "Капитан") != 1)
                    throw new Exception("Only the party leader should be labelled Captain");
                if (descendants(dashboard).OfType<osu.Framework.Graphics.Sprites.SpriteText>().Any(t => t.Text.ToString().Contains("Открыть результат последнего матча")))
                    throw new Exception("The obsolete last-match result button must not be shown");
                var partySurface = descendants(dashboard).First(d => d.Name == "somsai-party");
                if (descendants(partySurface).OfType<OsuScrollContainer>().OrderByDescending(s => s.DrawHeight).First().ScrollbarVisible)
                    throw new Exception("Party scrollbars must stay hidden while wheel scrolling remains available");
                var historySurface = descendants(dashboard).First(d => d.Name == "somsai-recent-matches");
                if (descendants(historySurface).OfType<OsuScrollContainer>().First().ScrollbarVisible)
                    throw new Exception("Recent match scrollbar must stay hidden");
                var plaque = descendants(dashboard).First(d => d.Name == "somsai-rank-gold1");
                if (Math.Abs(plaque.DrawHeight - 140) > 1) throw new Exception("Rank plaque must retain its original 140px height");
                var searchSurface = descendants(dashboard).First(d => d.Name == "somsai-matchmaking-actions");
                var partyAvatar = descendants(partySurface).OfType<osu.Game.Users.Drawables.DrawableAvatar>().First();
                descendants(searchSurface).OfType<RoundedButton>().First(b => b.Text.ToString().EndsWith("2v2")).TriggerClick();
                if (!ReferenceEquals(partyAvatar, descendants(partySurface).OfType<osu.Game.Users.Drawables.DrawableAvatar>().First()))
                    throw new Exception("Switching 1v1/2v2 must not recreate party avatars");
                var searchButton = descendants(searchSurface).OfType<RoundedButton>().First(b => b.Text.ToString().StartsWith("Начать поиск"));
                if (searchSurface.ScreenSpaceDrawQuad.AABB.Bottom - searchButton.ScreenSpaceDrawQuad.AABB.Bottom > 28)
                    throw new Exception("Search action must be anchored near the bottom of its panel");
                snapshot("02-party", () => descendants(dashboard).OfType<RoundedButton>().First(b => b.Text.ToString() == "Кастомки").TriggerClick());
                break;
            case 101:
                var customs = descendants(dashboard).First(d => d.Name == "somsai-customs-overlay");
                if (!customs.IsPresent || !descendants(customs).OfType<osu.Framework.Graphics.Sprites.SpriteText>().Any(t => t.Text.ToString().Contains("SOMSAI Tournament Room")))
                    throw new Exception("Animated multiplayer-style custom room listing did not open");
                snapshot("03-customs", () => descendants(customs).OfType<RoundedButton>().First(b => b.Text.ToString().Contains("Создать комнату")).TriggerClick());
                break;
            case 102:
                var customsCreate = descendants(dashboard).First(d => d.Name == "somsai-customs-overlay");
                var createPanel = (Drawable)customsCreate.GetType().GetField("createPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(customsCreate)!;
                if (!createPanel.IsPresent || createPanel.Alpha <= 0)
                    throw new Exception("Create-room form did not slide down inside the custom lounge");
                if (descendants(customsCreate).OfType<osu.Framework.Graphics.Sprites.SpriteText>().Any(t => t.Text.ToString().Contains("Select beatmap", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("SOMSAI custom creation must not offer beatmap selection");
                snapshot("04-create-room", () => customsCreate.GetType().GetMethod("HidePanel", BindingFlags.Instance | BindingFlags.Public)!.Invoke(customsCreate, null));
                break;
            case 103:
                descendants(dashboard).OfType<RoundedButton>().First(b => b.Text.ToString() == "Рейтинг").TriggerClick();
                break;
            case 104:
                var ranking = (Drawable)stack.CurrentScreen;
                if (!descendants(ranking).OfType<ClickableContainer>().Any())
                    throw new Exception("Leaderboard player rows must open player profiles");
                snapshot("05-ranking", () => ((OsuScreen)stack.CurrentScreen).Exit());
                break;
            case 105:
                descendants(dashboard).OfType<ClickableContainer>().First(d => d.Name == "somsai-recent-match").TriggerClick();
                break;
            case 106:
                var history = (Drawable)stack.CurrentScreen;
                var historyTexts = descendants(history).OfType<osu.Framework.Graphics.Sprites.SpriteText>().Select(t => t.Text.ToString()).ToArray();
                if (!historyTexts.Any(t => t.Contains("950")))
                    throw new Exception("Match browser did not render per-player results: " + string.Join(" | ", descendants((Drawable)stack.CurrentScreen).OfType<osu.Framework.Graphics.Sprites.SpriteText>().Select(t => t.Text.ToString())));
                if (historyTexts.Any(t => t.StartsWith("Рейтинг ")) || !historyTexts.Any(t => t.Contains("Красная команда")) || !historyTexts.Any(t => t.Contains("Синяя команда")))
                    throw new Exception("Match history must show both coloured teams without ranked-table buttons");
                if (descendants(history).OfType<ClickableContainer>().Count() < 4)
                    throw new Exception("Match participants and map events must be clickable");
                if (descendants(history).Any(d => d.Name == "somsai-player-bot" && d is ClickableContainer))
                    throw new Exception("Bot identities in match history must never be clickable");
                if (!descendants(history).Any(d => d.Name == "somsai-player-human" && d is ClickableContainer))
                    throw new Exception("Human identities in match history must open player profiles");
                if (!historyTexts.Any(t => t.Contains("98.76%") || t.Contains("98,76%")) || !historyTexts.Any(t => t.Contains("638x")) || !historyTexts.Any(t => t.Contains("GREAT 543")))
                    throw new Exception("Detailed match rows must show accuracy, max combo and hit statistics: " + string.Join(" | ", historyTexts));
                if (!historyTexts.Any(t => t.Contains("Δ +150")))
                    throw new Exception("Match result delta must include an explicit plus or minus sign");
                Console.WriteLine("PASS: bottom-aligned matchmaking, animated customs/ranking, hidden scrollbars and signed clickable match history.");
                snapshot("06-history", () => { Passed = true; Exit(); });
                break;
        }
    }

    private Drawable firstOceanBubble() => oceanPreview!.Children.First(d => d.Name == "somsai-ocean-bubble");

    private void setOceanTime(double seconds) => oceanPreview!.GetType().GetField("elapsed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(oceanPreview, seconds);

    private void checkOcean()
    {
        var art = descendants(dashboard).OfType<osu.Framework.Graphics.Sprites.Sprite>().Where(s => s.Name is "somsai-ocean-art" or "somsai-ocean-logo").ToArray();
        int expectedArtwork = dashboard.Title.Contains("Матч", StringComparison.Ordinal) ? 1 : 2;
        if (art.Length != expectedArtwork || art.Any(s => s.Texture == null || s.Texture.Width < 100)) throw new Exception("Embedded SOMSAI artwork did not load");
        var bubbles = descendants(dashboard).Where(d => d.Name == "somsai-ocean-bubble").ToArray();
        if (bubbles.Length != 34) throw new Exception("Ocean animation must have a fixed particle budget");
        Console.WriteLine("PASS: embedded background, transparent logo and bounded ocean animation on " + dashboard.Title);
    }

    private void checkBubbleCoverage()
    {
        var originals = bubbleTransition!.Children.Where(d => d.Name == "somsai-transition-bubble").ToArray();
        float unit = Math.Max(bubbleTransition.DrawWidth / 10, bubbleTransition.DrawHeight / 6);
        var originalDiameters = originals.Select(d => d.DrawWidth * d.Scale.X).OrderBy(d => d).ToArray();
        if (originals.Length != 104 || Math.Abs(originalDiameters[0] - unit * .75f) > .1f || Math.Abs(originalDiameters[^1] - unit * 1.5f) > .1f)
            throw new Exception("The original 104 transition bubbles and their size range must be preserved");
        var circles = bubbleTransition.Children.Where(d => d.IsPresent && d.Name is "somsai-transition-bubble" or "somsai-transition-gap-bubble")
            .Select(d => (d.Position, Radius: d.DrawWidth * d.Scale.X / 2 - 1)).ToArray();
        // Inspect the actual animated geometry, independently of the solid background layer.
        // Include all four edges and corners, where sparse random placement used to leave holes.
        for (int y = 0; y <= 120; y++)
        for (int x = 0; x <= 200; x++)
        {
            var point = new Vector2(bubbleTransition.DrawWidth * x / 200, bubbleTransition.DrawHeight * y / 120);
            if (!circles.Any(c => (point - c.Position).LengthSquared <= c.Radius * c.Radius))
                throw new Exception($"Gap between transition bubbles at {point}, size {bubbleTransition.DrawSize}");
        }
        Console.WriteLine($"PASS: original 104 sizes preserved; {circles.Length - 104} gap fillers; full coverage at {bubbleTransition.DrawSize}.");
    }

    private void snapshot(string name, Action after)
    {
        if (!visual) { after(); return; }
        afterCapture = after;
        capture = saveScreenshot(name);
    }

    private async Task saveScreenshot(string name)
    {
        using var image = await Host.TakeScreenshotAsync();
        string path = Path.Combine(output, name + ".png");
        await image.SaveAsPngAsync(path);
        Console.WriteLine("SCREENSHOT: " + path);
    }

    private void setDashboardState(string json)
    {
        typeof(SomsAiScreen).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dashboard,
            Newtonsoft.Json.JsonConvert.DeserializeObject<SomsAiState>(json));
        typeof(SomsAiScreen).GetMethod("render", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dashboard, null);
    }

    private void checkMapBoard()
    {
        var board = (Drawable)typeof(SomsAiScreen).GetField("mapBoard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dashboard)!;
        var cards = descendants(board).Where(d => d.GetType().Name == "MapCard").ToArray();
        if (cards.Length != 15) throw new Exception("Entire pool must have 15 cards");
        var bounds = board.ScreenSpaceDrawQuad.AABB;
        foreach (var card in cards)
        {
            var box = card.ScreenSpaceDrawQuad.AABB;
            if (!card.IsLoaded || box.Width <= 0 || box.Left < bounds.Left - 1 || box.Right > bounds.Right + 1)
                throw new Exception($"Map card outside board: {box} / {bounds}");
        }
        if (descendants(board).OfType<osu.Game.Overlays.BeatmapSet.Buttons.PreviewButton>().Count() != 15)
            throw new Exception("Each map requires its native audio preview");
        Console.WriteLine($"PASS: separate tournament screen, 15 loaded cards with preview controls fit width {stack.Width}.");
    }

    private void checkDashboardBounds()
    {
        var bounds = dashboard.ScreenSpaceDrawQuad.AABB;
        var masked = typeof(Drawable).GetProperty("IsMaskedAway", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var buttons = descendants(dashboard).OfType<RoundedButton>()
            .Where(button => button.IsPresent && button.IsLoaded && !(bool)masked.GetValue(button)!).ToArray();
        if (buttons.Length == 0) throw new Exception("Dashboard must contain live controls");
        foreach (var button in buttons)
        {
            var box = button.ScreenSpaceDrawQuad.AABB;
            // Scroll containers defer layout of masked children. Check them when they enter the viewport.
            if (box.Top >= bounds.Bottom || box.Bottom <= bounds.Top) continue;
            if (box.Width <= 0 || box.Left < bounds.Left || box.Right > bounds.Right)
                throw new Exception($"SOMSAI control escapes dashboard: {button.Text}, {box}, {bounds}");
            foreach (var text in descendants(button).OfType<osu.Game.Graphics.Sprites.OsuSpriteText>())
                if (text.DrawWidth > button.DrawWidth - 2)
                    throw new Exception($"SOMSAI caption clipped: {text.Text} ({text.DrawWidth} > {button.DrawWidth})");
        }
        Console.WriteLine($"PASS: SOMSAI dashboard width {stack.Width}: {buttons.Length} actions fit, including forms/pool choices.");
    }

    private void checkQueueBounds()
    {
        nativeSelected.Value = new MatchmakingPool { Id = 1 };
        nativeConnected.Value = !nativeConnected.Value;
        if (controls.Enabled.Value || controls.Action != null || controls.Text.ToString() != "Иди в обычный лазер")
            throw new Exception("Native LoadComplete/reconnect re-enabled Ranked");
        foreach (var text in descendants(controls).OfType<osu.Framework.Graphics.Sprites.SpriteText>())
            if (text.DrawWidth > controls.DrawWidth - 2)
                throw new Exception("Disabled Ranked caption clipped: " + text.DrawWidth);
        Console.WriteLine($"PASS: actual native Ranked button loaded; disabled after selection/reconnect; caption fits in {queuePanel.Width}px panel.");
    }

    private static IEnumerable<Drawable> descendants(Drawable drawable)
    {
        yield return drawable;
        if (drawable is not CompositeDrawable) yield break;
        var children = (IReadOnlyList<Drawable>)typeof(CompositeDrawable)
            .GetProperty("InternalChildren", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(drawable)!;
        foreach (var child in children)
        foreach (var descendant in descendants(child)) yield return descendant;
    }
}
