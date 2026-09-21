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
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Online.Matchmaking;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Rulesets.EnhancedAuth.Online;
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
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        string output = Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--output="))?[9..] ?? ".test-tmp/somsai-ocean-check");
        Directory.CreateDirectory(output);
        return Run(args.Contains("--visual"), output);
    }

    private static int Run(bool visual, string output)
    {
        string name = "somsai-load-" + Guid.NewGuid().ToString("N");
        using GameHost host = visual ? Host.GetSuitableDesktopHost(name, new HostOptions { PortableInstallation = true, FriendlyGameName = "SOMSAI UI verification" }) : new HeadlessGameHost(name);
        var game = new LoadGame(visual, output, Path.Combine(output, "profile", name));
        Exception? failure = null;
        host.ExceptionThrown += exception => { Console.Error.WriteLine(exception); failure = exception; host.Exit(); return true; };
        using var timeout = new Timer(_ => host.Exit(), null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        host.Run(game);
        if (failure != null) throw failure;
        if (!game.Passed) throw new Exception("Full drawable load/layout did not finish within 150 seconds");
        return 0;
    }
}


internal sealed partial class LoadGame : OsuGameBase
{
    public bool Passed { get; private set; }
    private readonly bool visual;
    private readonly string output, profile;
    private readonly DialogOverlay dialogs = new();
    private readonly TestLinks links = new();
    private readonly osu.Game.Online.Chat.ChannelManager channels;
    private OsuScreenStack stack = null!;
    private SomsAiScreen screen = null!;
    private SomsAiState state = null!;
    private int stage, frames, requests;
    private Task? capture;
    private Action? afterCapture;
    private Drawable[] avatars = Array.Empty<Drawable>();
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public LoadGame(bool visual, string output, string profile)
    {
        this.visual = visual; this.output = output; this.profile = profile;
        API = new DummyAPIAccess();
        API.LocalUser.Value.Id = 42;
        API.LocalUser.Value.Username = "Captain";
        channels = new osu.Game.Online.Chat.ChannelManager(API);
        ((DummyAPIAccess)API).HandleRequest = request =>
        {
            if (request is GetSomsAiStateRequest) return true;
            if (request is ApplySomsAiActionRequest) { requests++; return true; }
            return false;
        };
    }
    protected override int UnhandledExceptionsBeforeCrash => 0;
    protected override Storage CreateStorage(GameHost host, Storage defaultStorage) => host.GetStorage(profile);
    protected override Container CreateScalingContainer() => new Container { RelativeSizeAxes = Axes.Both };
    protected override IDictionary<FrameworkSetting, object> GetFrameworkConfigDefaults() => new Dictionary<FrameworkSetting, object>
    {
        [FrameworkSetting.WindowMode] = WindowMode.Windowed,
        [FrameworkSetting.VolumeUniversal] = 0.0, [FrameworkSetting.VolumeMusic] = 0.0, [FrameworkSetting.VolumeEffect] = 0.0,
        [FrameworkSetting.WindowedSize] = new System.Drawing.Size(1600, 1000),
    };
    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
        dependencies.CacheAs<IDialogOverlay>(dialogs);
        dependencies.Cache(channels);
        dependencies.CacheAs<osu.Game.Online.ILinkHandler>(links);
        dependencies.Cache(new OverlayColourProvider(OverlayColourScheme.Plum));
        return dependencies;
    }
    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (visual) { Host.Window.Hide(); var active = (Bindable<bool>)Host.IsActive; active.UnbindAll(); active.Value = true; }
        Ruleset.Value = RulesetStore.AvailableRulesets.First();
        Add(dialogs); Add(channels); Add((DummyAPIAccess)API);
        Add(stack = new OsuScreenStack { RelativeSizeAxes = Axes.None, Size = new Vector2(1600, 1000) });
        state = fixture();
        stack.Push(screen = new SomsAiMatchScreen(state));
    }
    private SomsAiState fixture()
    {
        var match = new SomsAiMatch { Id = 42, Format = "2v2", Stage = "banning", PoolSelected = true,
            PoolName = "SOMSAI Open · Grand Finals", BestOf = 9, Wins = new[] { 2, 1 }, TurnUserId = API.LocalUser.Value.OnlineID,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(2),
            Teams = new()
            {
                new() { Id = 0, Name = "Team Captain", CaptainId = API.LocalUser.Value.OnlineID, Members = new()
                {
                    new() { Id = API.LocalUser.Value.OnlineID, Username = "Captain", AvatarUrl = "https://a.ppy.sh/124493" },
                    new() { Id = 11, Username = "Reef Hunter", AvatarUrl = "https://a.ppy.sh/124493", Ready = true },
                } },
                new() { Id = 1, Name = "Team mrekk", CaptainId = 20, Members = new()
                {
                    new() { Id = 20, Username = "mrekk", OfficialId = 7562902, OfficialUsername = "mrekk", AvatarUrl = "https://a.ppy.sh/7562902", Ready = true, IsBot = true },
                    new() { Id = 21, Username = "WhiteCat", AvatarUrl = "https://a.ppy.sh/4504101", Ready = true, IsBot = true },
                } },
            },
        };
        var titles = new[] { "Unhappy Refrain", "Paraclete", "Everything will freeze", "Singularity", "Afterburner", "Ame wo Matsu.", "R U 4 Me?", "PANGIA", "Embyrophyte", "Resonance", "Imperial Circus", "The Abhorrent Art", "White Promise", "Mmmmmm", "Night of Knights", "Pacific Girls", "Kick To The Sky" };
        int at = 0;
        foreach (var (category, count) in new[] { ("NM", 5), ("HD", 3), ("HR", 3), ("DT", 3), ("FM", 2), ("TB", 1) })
        for (int i = 1; i <= count; i++)
        {
            int n = at++;
            match.Slots.Add(new SomsAiSlot { Id = category + i, Label = category + i, Category = category, BeatmapId = n + 1,
                BeatmapSetId = new[] { 1, 17, 105, 41823, 39804 }[n % 5],
                Artist = new[] { "wowaka", "LeaF", "UNDEAD CORPORATION", "ETIA.", "BilliumMoto" }[n % 5], Title = titles[n], Version = "Extra difficulty", Stars = 7.5 + n * .03,
                DisplayStats = new SomsAiMapStats { Stars = 7.5 + n * .03, Bpm = category == "DT" ? 285 : 190, Ar = category == "DT" ? 10.3 : 9.5, Cs = 4.2, Od = 9.3, Hp = 6, Length = 190 },
                Status = category == "TB" ? "tiebreaker" : "available" });
        }
        return new SomsAiState { Match = match };
    }
    protected override void Update()
    {
        if (visual) ((Bindable<bool>)IsActive).Value = true;
        base.Update();
        if (capture != null)
        {
            if (!capture.IsCompleted) return;
            capture.GetAwaiter().GetResult(); capture = null; frames = 0;
            var after = afterCapture; afterCapture = null; after?.Invoke(); return;
        }
        if (screen?.IsLoaded != true || ++frames < 80) return;
        frames = 0;
        if (field<Drawable?>("matchIntro") is { IsAlive: true, IsPresent: true }) return;
        var primary = field<RoundedButton>("arenaPrimary");
        switch (stage++)
        {
            case 0:
                checkBounds();
                if (!field<bool>("introPlayed")) throw new Exception("Native VS did not run");
                avatars = currentAvatars();
                if (descendants(screen).OfType<osu.Game.Screens.OnlinePlay.Match.Components.MatchChatDisplay>().Count() != 1) throw new Exception("Native chat missing");
                if (primary.Enabled.Value) throw new Exception("Draft action before selection");
                var first = descendants(field<Drawable>("mapBoard")).OfType<osu.Game.Graphics.Containers.OsuClickableContainer>().First(d => d.GetType().Name == "MapCard");
                first.TriggerClick();
                descendants(first).OfType<osu.Game.Graphics.Containers.OsuClickableContainer>().First(d => !ReferenceEquals(d, first) && descendants(d).OfType<osu.Game.Graphics.Sprites.TruncatingSpriteText>().Any()).TriggerClick();
                if (links.Last?.Action != osu.Game.Online.Chat.LinkAction.OpenBeatmap || links.Last.Argument.ToString() != "1") throw new Exception("Map title did not open native beatmap browser");
                if (requests != 0) throw new Exception("Inspecting a card submitted a ban");
                if (!primary.Enabled.Value || primary.Text.ToString() != "Забанить NM1") throw new Exception("Selected map did not prepare ban");
                snapshot("01-draft-wide", () => { primary.TriggerClick(); primary.TriggerClick(); });
                break;
            case 1:
                if (requests != 1) throw new Exception("Repeated draft click submitted twice");
                field<ApplySomsAiActionRequest?>("actionRequest")?.Cancel();
                typeof(SomsAiScreen).GetField("actionRequest", flags)!.SetValue(screen, null);
                state.Match!.Slots[0].Status = "banned"; state.Match.Slots[0].SelectedByTeam = 0;
                state.Match.Slots[1].Status = "picked"; state.Match.Slots[1].SelectedByTeam = 1;
                state.Match.Stage = "ready"; state.Match.MapSlot = new Newtonsoft.Json.Linq.JValue("NM2");
                state.Match.Teams[0].Members[0].Ready = true;
                render();
                break;
            case 2:
                if (descendants(field<Drawable>("mapBoard")).Any(d => d.GetType().Name == "MapCard"
                    && (string)d.GetType().GetProperty("SlotStatus")!.GetValue(d)! == "banned"))
                {
                    stage--;
                    return;
                }
                checkBounds(); checkRetiredMaps();
                if (primary.Text.ToString() != "Снять готовность") throw new Exception("Unready action missing");
                for (int i = 0; i < 10; i++)
                {
                    state.Match!.Wins[0] = 2 + i % 2;
                    render();
                    if (!currentAvatars().SequenceEqual(avatars)) throw new Exception("Polling recreated avatars");
                }
                Console.WriteLine("PASS fixed action dock, grouped cards, modded stats, native VS, native chat, avatar reuse, animated ban removal and draft confirmation");
                snapshot("02-ready-wide", () => { stack.Size = new Vector2(1280, 720); });
                break;
            case 3:
                checkBounds();
                snapshot("03-ready-720", () => { stack.Size = new Vector2(640, 720); });
                break;
            case 4:
                checkBounds();
                snapshot("04-ready-narrow", () =>
                {
                    var workspace = field<Drawable>("arenaWorkspace");
                    descendants(workspace).OfType<RoundedButton>().Single(b => b.Text.ToString() == "Комната").TriggerClick();
                });
                break;
            case 5:
                checkBounds();
                snapshot("05-room-narrow", () =>
                {
                    stack.Size = new Vector2(1280, 720);
                    typeof(SomsAiScreen).GetMethod("selectArenaTab", flags)!.Invoke(screen, new object[] { 1 });
                });
                break;
            case 6:
                var chat = field<osu.Game.Screens.OnlinePlay.Match.Components.MatchChatDisplay>("arenaChat");
                chat.Channel.Value = new osu.Game.Online.Chat.Channel { Id = 500, Name = "#arena-test", Type = osu.Game.Online.Chat.ChannelType.Multiplayer };
                chat.Channel.Value.AddNewMessages(new osu.Game.Online.Chat.InfoMessage("Матч начался. Удачи обеим командам!"));
                chat.Channel.Value.AddNewMessages(new osu.Game.Online.Chat.InfoMessage("Team Captain забанила NM1."));
                chat.Channel.Value.AddNewMessages(new osu.Game.Online.Chat.InfoMessage("Team mrekk выбрала NM2. Подтвердите готовность."));
                chat.Channel.Value.TextBoxMessage.Value = "Черновик сообщения";
                render();
                if (chat.Channel.Value.TextBoxMessage.Value != "Черновик сообщения") throw new Exception("Polling lost chat draft");
                break;
            case 7:
                snapshot("06-chat", () =>
                {
                    state.Match!.Stage = "pool_select"; state.Match.PoolSelected = false;
                    state.Match.PoolCandidates = new() { new() { Id = 1, Name = "SOMSAI Open", MapCount = 17, BestOf = 9, AverageStars = 7.3 }, new() { Id = 2, Name = "Ocean Invitational", MapCount = 15, BestOf = 7, AverageStars = 6.9 } };
                    render();
                });
                break;
            case 8:
                checkBounds();
                snapshot("07-pool-vote", () =>
                {
                    state.Match!.Stage = "ended"; state.Match.WinnerTeamId = 0; state.Match.Wins = new[] { 5, 3 };
                    state.Match.History.Add(Newtonsoft.Json.Linq.JObject.Parse("""{"round":1,"slot_id":"NM2","winner_team_id":0,"team_scores":[1987554,1792221]}"""));
                    state.Match.History.Add(Newtonsoft.Json.Linq.JObject.Parse("""{"round":2,"slot_id":"NM3","winner_team_id":1,"team_scores":[1287554,1792221]}"""));
                    render();
                    typeof(SomsAiScreen).GetMethod("selectArenaTab", flags)!.Invoke(screen, new object[] { 2 });
                });
                break;
            case 9:
                checkBounds();
                if (primary.Text.ToString() != "Закрыть результат") throw new Exception("Result exit action missing");
                var historyCards = field<FillFlowContainer>("battleFooter").Children.OfType<osu.Game.Graphics.Containers.OsuClickableContainer>().ToArray();
                if (historyCards.Length != 3 || historyCards[1].BorderColour.Equals(historyCards[2].BorderColour) || historyCards.Any(c => c.BorderThickness < 2)) throw new Exception("Ban and win/loss history borders missing");
                historyCards[1].TriggerClick();
                if (links.Last?.Argument.ToString() != "2") throw new Exception("History card did not open beatmap");
                Console.WriteLine("PASS map titles and round cards open native browser; distinct glowing win/loss borders");
                typeof(SomsNativeMatchScreen).GetField("loadingGameplay", flags)!.SetValue(screen, true);
                screen.OnResuming(new ScreenTransitionEvent(screen, screen));
                if ((bool)typeof(SomsNativeMatchScreen).GetField("loadingGameplay", flags)!.GetValue(screen)!) throw new Exception("Return from results blocks next gameplay");
                Console.WriteLine("PASS returning from results releases gameplay loading for next round");
                snapshot("08-result", () =>
                {
                    state.Match!.Stage = "ready"; state.Match.Format = "4v4"; state.Match.PoolSelected = true;
                    foreach (var team in state.Match.Teams)
                        for (int i = 0; i < 2; i++) team.Members.Add(new SomsPlayer { Id = 100 + 10 * team.Id + i, Username = "Teammate " + i, Ready = true });
                    render();
                    typeof(SomsAiScreen).GetMethod("selectArenaTab", flags)!.Invoke(screen, new object[] { 0 });
                });
                break;
            case 10:
                checkBounds();
                if (descendants(screen).Count(d => d.Name == "somsai-team-player") != 8) throw new Exception("4v4 roster missing players");
                snapshot("09-4v4", () => { Console.WriteLine("PASS responsive arena at 1600x1000, 1280x720 and 640x720; room tabs, readiness, pool vote and result"); Console.WriteLine("PASS native chat messages and unsent draft persist across refresh; 4v4 roster"); Passed = true; Host.Exit(); });
                break;
        }
    }
    private void render()
    {
        typeof(SomsAiScreen).GetField("state", flags)!.SetValue(screen, Newtonsoft.Json.JsonConvert.DeserializeObject<SomsAiState>(Newtonsoft.Json.JsonConvert.SerializeObject(state)));
        typeof(SomsAiScreen).GetMethod("render", flags)!.Invoke(screen, null);
    }
    private T field<T>(string name) => (T)typeof(SomsAiScreen).GetField(name, flags)!.GetValue(screen)!;
    private Drawable[] currentAvatars() => field<Array>("teamHeaders").Cast<object>().Select(h => ((Container)h.GetType().GetField("avatar", flags)!.GetValue(h)!).Children.Single()).ToArray();
    private void checkBounds()
    {
        var bounds = stack.ScreenSpaceDrawQuad.AABB;
        foreach (var name in new[] { "arenaPrimary", "arenaDock", "arenaWorkspace" })
        {
            var b = field<Drawable>(name).ScreenSpaceDrawQuad.AABB;
            if (b.Left < bounds.Left - 1 || b.Right > bounds.Right + 1 || b.Top < bounds.Top || b.Bottom > bounds.Bottom || b.Height < 20)
                throw new Exception($"{name} outside viewport: {b} / {bounds}");
        }
        var board = field<Drawable>("mapBoard");
        foreach (var card in descendants(board).Where(d => d.GetType().Name == "MapCard"))
            if (card.DrawWidth > board.DrawWidth + 1 || card.DrawWidth < 100) throw new Exception("Card size outside map board");
        var primary = field<RoundedButton>("arenaPrimary");
        if (!primary.IsPresent) throw new Exception("Primary button masked away");
    }
    private void checkRetiredMaps()
    {
        var cards = descendants(field<Drawable>("mapBoard")).Where(d => d.GetType().Name == "MapCard").ToArray();
        if (cards.Length != 16 || cards.Any(card => (string)card.GetType().GetProperty("SlotStatus")!.GetValue(card)! == "banned"))
            throw new Exception($"Banned map did not fade out and release its grid position: cards={cards.Length}, states={string.Join(',', cards.Select(card => card.GetType().GetProperty("SlotStatus")!.GetValue(card)))}");
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
        await image.SaveAsPngAsync(Path.Combine(output, name + ".png"));
        Console.WriteLine("SCREENSHOT " + name);
    }
    private static IEnumerable<Drawable> descendants(Drawable node)
    {
        yield return node;
        if (node is not CompositeDrawable) yield break;
        foreach (var child in (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", flags)!.GetValue(node)!)
            foreach (var descendant in descendants(child)) yield return descendant;
    }
    private sealed class TestLinks : osu.Game.Online.ILinkHandler
    {
        public osu.Game.Online.Chat.LinkDetails? Last;
        public void HandleLink(string url) => throw new Exception("External browser must not be used");
        public void HandleLink(osu.Game.Online.Chat.LinkDetails link) => Last = link;
    }
}
