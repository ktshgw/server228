using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
using osu.Game.Online.Chat;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Screens;
using osuTK;

internal sealed partial class LoadGame : OsuGameBase
{
    public bool Passed { get; private set; }
    private readonly bool visual;
    private readonly string profile;
    private readonly DialogOverlay dialogs = new();
    private readonly ChannelManager channels;
    private OsuScreenStack stack = null!;
    private SomsAiScreen lobby = null!;
    private SomsAiScreen match = null!;
    private SomsAiState server = fixture(51);
    private ApplySomsAiActionRequest? pendingLeave;
    private JObject? leaveBody;
    private int stage, leaveRequests, reads, readsOnExit;
    private double nextStep, stableUntil;
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public LoadGame(bool visual, string output, string profile)
    {
        this.visual = visual; this.profile = profile;
        API = new DummyAPIAccess(); API.LocalUser.Value.Id = 42;
        channels = new ChannelManager(API);
        ((DummyAPIAccess)API).HandleRequest = request =>
        {
            if (request is GetSomsAiStateRequest stateRequest)
            {
                reads++;
                var response = JsonConvert.DeserializeObject<SomsAiState>(JsonConvert.SerializeObject(server))!;
                Scheduler.AddDelayed(() => success(stateRequest, response), 30);
                return true;
            }
            if (request is ApplySomsAiActionRequest action)
            {
                var body = (JObject)typeof(ApplySomsAiActionRequest).GetField("body", flags)!.GetValue(action)!;
                if (body.Value<string>("action") != "leave_match") throw new Exception("Unexpected test action " + body);
                pendingLeave = action; leaveBody = body; leaveRequests++;
                return true;
            }
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
        [FrameworkSetting.WindowedSize] = new System.Drawing.Size(1280, 720),
    };
    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
        dependencies.CacheAs<IDialogOverlay>(dialogs); dependencies.Cache(channels);
        dependencies.Cache(new OverlayColourProvider(OverlayColourScheme.Plum));
        return dependencies;
    }
    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (visual) { Host.Window.Hide(); var active = (Bindable<bool>)Host.IsActive; active.UnbindAll(); active.Value = true; }
        Ruleset.Value = RulesetStore.AvailableRulesets.First();
        Add(dialogs); Add(channels); Add((DummyAPIAccess)API);
        Add(stack = new OsuScreenStack { RelativeSizeAxes = Axes.Both });
        stack.Push(lobby = new SomsAiScreen());
    }
    protected override void Update()
    {
        if (visual) ((Bindable<bool>)IsActive).Value = true;
        base.Update();
        if (stack?.CurrentScreen is not SomsAiScreen { IsLoaded: true } current || Time.Current < nextStep) return;
        nextStep = Time.Current + 350;
        var intro = field<Drawable?>(current, "matchIntro");
        if (intro is { IsAlive: true, IsPresent: true }) return;
        switch (stage)
        {
            case 0:
                if (current is not SomsAiMatchScreen) return;
                match = current;
                match.Exit();
                stage++; break;
            case 1:
                if (!answer(false)) return;
                stage++; break;
            case 2:
                if (!ReferenceEquals(current, match)) throw new Exception("Cancelling Back left match");
                match.Exit(); stage++; break;
            case 3:
                if (!answer(true)) return;
                stableUntil = Time.Current + 4200; readsOnExit = reads; stage++; break;
            case 4:
                if (Time.Current < stableUntil) return;
                if (!ReferenceEquals(stack.CurrentScreen, lobby)) throw new Exception("Match reopened after confirmed Back");
                if (reads < readsOnExit + 2) throw new Exception("Exit was not tested across repeated server polls");
                if (leaveRequests != 0) throw new Exception("Back unexpectedly forfeited match");
                Console.WriteLine("PASS confirmed Back remains in lobby across repeated active-match polls; cancel keeps match");
                descendants(lobby).OfType<RoundedButton>().Single(b => b.Text.ToString() == "Перейти в матч").TriggerClick();
                stage++; break;
            case 5:
                if (current is not SomsAiMatchScreen) return;
                match = current;
                Console.WriteLine("PASS explicit match resume still opens the same match");
                invoke(match, "leaveArenaMatch"); stage++; break;
            case 6:
                if (!answer(false)) return;
                stage++; break;
            case 7:
                if (!ReferenceEquals(current, match) || leaveRequests != 0) throw new Exception("Cancelling surrender changed match");
                invoke(match, "leaveArenaMatch"); stage++; break;
            case 8:
                if (!answer(true)) return;
                stage++; break;
            case 9:
                if (pendingLeave == null) return;
                if (leaveBody!.Value<int>("match_id") != 51 || leaveBody.ContainsKey("expected_revision"))
                    throw new Exception("Leave must name its match without a racing draft revision");
                if (!ReferenceEquals(current, match)) throw new Exception("Exited before server accepted surrender");
                pendingLeave.Fail(new Exception("Fixture: leave rejected")); pendingLeave = null;
                stage++; break;
            case 10:
                if (!ReferenceEquals(current, match)) throw new Exception("Failed leave exited match");
                invoke(match, "leaveArenaMatch"); stage++; break;
            case 11:
                if (!answer(true)) return;
                stage++; break;
            case 12:
                if (pendingLeave == null) return;
                invoke(match, "action", "leave_match", null, null, null);
                if (leaveRequests != 2) throw new Exception("Repeated leave submitted twice");
                server.Match!.Stage = "ended"; server.Match.WinnerTeamId = 1;
                success(pendingLeave, new JObject()); pendingLeave = null;
                stableUntil = Time.Current + 3000; stage++; break;
            case 13:
                if (Time.Current < stableUntil) return;
                if (!ReferenceEquals(current, lobby)) throw new Exception("Successful surrender stayed on the match/result screen");
                Console.WriteLine("PASS surrender waits for server, tolerates failures and concurrent revisions, submits once and returns to lobby");
                descendants(lobby).OfType<RoundedButton>().Single(b => b.Text.ToString() == "Открыть результат последнего матча").TriggerClick();
                stage++; break;
            case 14:
                if (current is not SomsAiMatchScreen) return;
                match = current;
                field<RoundedButton>(match, "arenaPrimary").TriggerClick(); stage++; break;
            case 15:
                if (pendingLeave == null) return;
                server.Match = null;
                success(pendingLeave, new JObject()); pendingLeave = null;
                stage++; break;
            case 16:
                if (!ReferenceEquals(current, lobby)) return;
                Console.WriteLine("PASS saved final result can be reopened and explicitly closed");
                server = fixture(52); invoke(lobby, "refresh"); stage++; break;
            case 17:
                if (current is not SomsAiMatchScreen || field<int?>(current, "boundMatchId") != 52) return;
                Console.WriteLine("PASS a different new match still opens automatically");
                Passed = true; Host.Exit(); stage++; break;
        }
    }
    private bool answer(bool confirm)
    {
        var dialog = dialogs.CurrentDialog;
        if (dialog?.IsLoaded != true) return false;
        dialog.Buttons.ElementAt(confirm ? 0 : 1).TriggerClick();
        return true;
    }
    private static T field<T>(object obj, string name) => (T)typeof(SomsAiScreen).GetField(name, flags)!.GetValue(obj)!;
    private static void invoke(object obj, string name, params object?[] args) => typeof(SomsAiScreen).GetMethod(name, flags)!.Invoke(obj, args);
    private static void success<T>(APIRequest<T> request, T response) where T : class =>
        typeof(APIRequest<T>).GetMethod("TriggerSuccess", flags, new[] { typeof(T) })!.Invoke(request, new object[] { response });
    private static SomsAiState fixture(int id) => new()
    {
        Match = new SomsAiMatch
        {
            Id = id, Format = "1v1", Stage = "banning", Revision = 7, TurnUserId = 42, OwnerId = 42,
            Teams = new()
            {
                new() { Id = 0, CaptainId = 42, Name = "Captain", Members = new() { new() { Id = 42, Username = "Captain" } } },
                new() { Id = 1, CaptainId = 43, Name = "Opponent", Members = new() { new() { Id = 43, Username = "Opponent", IsBot = true } } },
            },
        },
    };
    private static IEnumerable<Drawable> descendants(Drawable drawable)
    {
        yield return drawable;
        if (drawable is not CompositeDrawable) yield break;
        foreach (var child in (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", flags)!.GetValue(drawable)!)
            foreach (var node in descendants(child)) yield return node;
    }
}
