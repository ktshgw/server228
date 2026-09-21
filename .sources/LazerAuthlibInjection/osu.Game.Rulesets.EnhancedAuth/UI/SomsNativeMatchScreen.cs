#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Screens;
using osu.Game.Screens.OnlinePlay;
using osu.Game.Screens.OnlinePlay.Matchmaking.Match.Gameplay;
using osu.Game.Screens.OnlinePlay.Multiplayer;
using osuTK;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>Native multiplayer loading/submission shared by both SOMS match formats.</summary>
public abstract partial class SomsNativeMatchScreen : OsuScreen
{
    [Resolved] protected MultiplayerClient Client { get; private set; } = null!;
    [Resolved] protected IAPIProvider Api { get; private set; } = null!;
    [Resolved] private BeatmapManager beatmaps { get; set; } = null!;
    [Resolved] private RulesetStore rulesets { get; set; } = null!;
    [Resolved(CanBeNull = true)] private OsuGame? hostGame { get; set; }
    [Resolved] private GameHost gameHost { get; set; } = null!;
    private Sample? startSample;
    private bool returningForNextMap;
    [Cached] private readonly OverlayColourProvider colourProvider = new(OverlayColourScheme.Plum);
    protected readonly OsuSpriteText StatusText = Text("Подключение…", 17);
    protected readonly FillFlowContainer Body = Flow();
    protected readonly CancellationTokenSource Lifetime = new();
    private SomsMatchBeatmapTracker? tracker;
    private bool loadingGameplay;
    private bool closed;
    protected bool Alive => !closed && !SomsDrawableLifecycle.IsDisposed(this);
    protected Scheduler GlobalScheduler => gameHost.UpdateThread.Scheduler;
    protected abstract long? ActiveRoomId { get; }
    protected bool OwnsCurrentRoom => ActiveRoomId.HasValue && Client.Room?.RoomID == ActiveRoomId;
    public override bool ShowFooter => true;
    public override bool? ApplyModTrackAdjustments => false;

    [BackgroundDependencyLoader]
    private void load(AudioManager audio)
    {
        startSample = audio.Samples.Get("SongSelect/confirm-selection");
        BuildLayout();
    }

    protected virtual void BuildLayout()
    {
        InternalChild = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Padding = new MarginPadding { Horizontal = 40, Top = 24, Bottom = 65 },
            Child = new OsuScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = Body,
            },
        };
        Body.Add(Text(Title, 34));
        Body.Add(StatusText);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Client.LoadRequested += loadGameplay;
        Client.UserStateChanged += userStateChanged;
        Client.RoomUpdated += roomUpdated;
        roomUpdated();
    }

    protected void OnUpdateThread(Action action)
    {
        if (!Alive) return;
        // A suspended Screen is expired, so its own scheduler cannot receive
        // server events while gameplay/results are on top of it.
        if (KeepMultiplayerResults && hostGame != null) gameHost.UpdateThread.Scheduler.Add(() => { if (Alive) action(); });
        else Schedule(() => { if (Alive) action(); });
    }

    protected async Task RunOperation(Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            OnUpdateThread(() => StatusText.Text = "Не удалось выполнить действие: " + exception.GetBaseException().Message);
        }
    }

    private void roomUpdated()
    {
        if (!Alive || !OwnsCurrentRoom) return;
        if (tracker == null)
            AddInternal(tracker = new SomsMatchBeatmapTracker(() => OwnsCurrentRoom && Alive));
    }

    protected bool BeatmapReady => tracker?.Availability.Value.State == DownloadState.LocallyAvailable;
    protected bool HasBeatmapRevision(int beatmapId, string checksum) => FindBeatmapRevision(beatmaps, beatmapId, checksum) != null;

    internal static BeatmapInfo? FindBeatmapRevision(BeatmapManager manager, int beatmapId, string checksum)
    {
        if (string.IsNullOrEmpty(checksum)) return manager.QueryOnlineBeatmapId(beatmapId);
        string normalised = checksum.ToLowerInvariant();
        return manager.QueryBeatmap(map => map.OnlineID == beatmapId && map.MD5Hash == normalised);
    }

    private void loadGameplay() => OnUpdateThread(() =>
    {
        var room = Client.Room;
        if (room == null || room.RoomID != ActiveRoomId) return;
        if (!this.IsCurrentScreen())
        {
            if (KeepMultiplayerResults)
            {
                returningForNextMap = true;
                this.MakeCurrent();
                OnUpdateThread(loadGameplay);
            }
            return;
        }
        if (loadingGameplay) return;
        var item = room.Playlist.FirstOrDefault(p => p.ID == room.Settings.PlaylistItemId);
        if (item == null) { StatusText.Text = "Ожидание выбранной карты…"; return; }
        var localBeatmap = FindBeatmapRevision(beatmaps, item.BeatmapID, item.BeatmapChecksum);
        var ruleset = rulesets.GetRuleset(item.RulesetID);
        if (localBeatmap == null || ruleset == null) { StatusText.Text = "Выбранная карта ещё не загружена."; return; }
        Ruleset.Value = ruleset;
        Beatmap.Value = beatmaps.GetWorkingBeatmap(localBeatmap);
        var rulesetInstance = ruleset.CreateInstance();
        Mods.Value = item.RequiredMods.Concat(Client.LocalUser?.Mods ?? Array.Empty<APIMod>())
            .DistinctBy(mod => mod.Acronym).Select(mod => mod.ToMod(rulesetInstance)).ToArray();
        loadingGameplay = true;
        returningForNextMap = false;
        startSample?.Play();
        var users = room.Users.ToArray();
        this.Push(new MultiplayerPlayerLoader(() => CreateGameplay(new Room(room), new PlaylistItem(item), users)));
    });

    protected virtual MultiplayerPlayer CreateGameplay(Room room, PlaylistItem item, MultiplayerRoomUser[] users)
        => new ScreenGameplay(room, item, users);

    protected virtual bool KeepMultiplayerResults => false;

    private void userStateChanged(MultiplayerRoomUser user, MultiplayerUserState state)
    {
        if (!KeepMultiplayerResults && Alive && loadingGameplay && Client.Room?.RoomID == ActiveRoomId && user.Equals(Client.LocalUser) && state == MultiplayerUserState.Idle)
            OnUpdateThread(() => this.MakeCurrent());
    }

    public override void OnResuming(ScreenTransitionEvent e)
    {
        base.OnResuming(e);
        if (returningForNextMap) { loadingGameplay = false; return; }
        // Player and loader both become invalid after completion. A results screen
        // can therefore be the direct predecessor when returning to this lobby.
        if (KeepMultiplayerResults && loadingGameplay)
        {
            loadingGameplay = false;
            if (OwnsCurrentRoom && Client.LocalUser is { } user && user.State != MultiplayerUserState.Idle)
                _ = RunOperation(() => user.State is MultiplayerUserState.Playing or MultiplayerUserState.Loaded or MultiplayerUserState.ReadyForGameplay
                    ? Client.AbortGameplay() : Client.ChangeState(MultiplayerUserState.Idle));
            return;
        }
        if (e.Last is not MultiplayerPlayerLoader loader) return;
        loadingGameplay = false;
        if (Client.Room?.RoomID != ActiveRoomId) return;
        _ = RunOperation(() => loader.GameplayPassed ? Client.ChangeState(MultiplayerUserState.Idle) : Client.AbortGameplay());
    }

    protected override void Dispose(bool isDisposing)
    {
        // An async load failure can dispose a screen before its parent releases it.
        if (closed) return;
        closed = true;
        Lifetime.Cancel();
        if (Client != null)
        {
            Client.LoadRequested -= loadGameplay;
            Client.UserStateChanged -= userStateChanged;
            Client.RoomUpdated -= roomUpdated;
        }
        base.Dispose(isDisposing);
        Lifetime.Dispose();
    }

    internal static FillFlowContainer Flow() => new()
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Direction = FillDirection.Vertical,
        Spacing = new Vector2(0, 10),
    };

    internal static OsuSpriteText Text(string text, float size = 20) => new() { Text = text, Font = OsuFont.GetFont(size: size) };
    internal static RoundedButton Button(string text, Action action) => new()
    {
        RelativeSizeAxes = Axes.X,
        Height = 42,
        Text = text,
        Action = action,
    };
}

/// <summary>Tracks only the owned room, tolerates its initially empty playlist, and cancels stale downloads.</summary>
public sealed partial class SomsMatchBeatmapTracker : OnlinePlayBeatmapAvailabilityTracker
{
    private readonly Func<bool> isOwnedRoom;
    [Resolved] private MultiplayerClient client { get; set; } = null!;
    [Resolved] private BeatmapLookupCache lookup { get; set; } = null!;
    [Resolved] private BeatmapManager manager { get; set; } = null!;
    [Resolved] private BeatmapModelDownloader downloads { get; set; } = null!;
    [Resolved] private IAPIProvider api { get; set; } = null!;
    [Resolved] private OsuConfigManager config { get; set; } = null!;
    private CancellationTokenSource? lookupCancellation;
    private IBindable<BeatmapAvailability>? availability;
    private readonly Bindable<BeatmapAvailability> verifiedAvailability = new(BeatmapAvailability.NotDownloaded());
    public override IBindable<BeatmapAvailability> Availability => verifiedAvailability;
    private long? lastItem;
    private int selectedBeatmapId;
    private string selectedChecksum = "";
    private double nextRevisionCheck;
    private bool disposed;

    public SomsMatchBeatmapTracker(Func<bool> isOwnedRoom) => this.isOwnedRoom = isOwnedRoom;
    protected override void LoadComplete()
    {
        base.LoadComplete();
        availability = base.Availability.GetBoundCopy();
        availability.BindValueChanged(onAvailability);
        client.RoomUpdated += onRoomUpdated;
        onRoomUpdated();
    }

    private void onAvailability(ValueChangedEvent<BeatmapAvailability> e)
    {
        updateVerifiedAvailability(e.NewValue);
    }

    private void updateVerifiedAvailability(BeatmapAvailability reported)
    {
        if (disposed || !isOwnedRoom()) return;
        var verified = reported;
        if (reported.State == DownloadState.LocallyAvailable && !hasExpectedRevision())
            verified = BeatmapAvailability.NotDownloaded();
        if (verifiedAvailability.Value.Equals(verified)) return;
        verifiedAvailability.Value = verified;
        observe(client.ChangeBeatmapAvailability(verified));
    }

    private bool hasExpectedRevision() => selectedBeatmapId > 0
        && SomsNativeMatchScreen.FindBeatmapRevision(manager, selectedBeatmapId, selectedChecksum) != null;

    protected override void Update()
    {
        base.Update();
        // The base tracker can remain LocallyAvailable while another revision is
        // imported. Verify the room checksum even if its broad online-ID state did not change.
        if (Time.Current < nextRevisionCheck || availability == null) return;
        nextRevisionCheck = Time.Current + 1000;
        updateVerifiedAvailability(availability.Value);
    }

    private void onRoomUpdated()
    {
        if (disposed) return;
        if (!isOwnedRoom() || client.Room == null)
        {
            lastItem = null;
            selectedBeatmapId = 0;
            selectedChecksum = "";
            verifiedAvailability.Value = BeatmapAvailability.Unknown();
            lookupCancellation?.Cancel();
            PlaylistItem.Value = null;
            return;
        }
        var item = client.Room.Playlist.FirstOrDefault(p => p.ID == client.Room.Settings.PlaylistItemId);
        if (item == null || (item.ID == lastItem && item.BeatmapChecksum == selectedChecksum)) return;
        lastItem = item.ID;
        selectedBeatmapId = item.BeatmapID;
        selectedChecksum = item.BeatmapChecksum;
        PlaylistItem.Value = new PlaylistItem(item);
        lookupCancellation?.Cancel();
        lookupCancellation?.Dispose();
        lookupCancellation = new CancellationTokenSource();
        if (!hasExpectedRevision())
            _ = download(item.BeatmapID, lookupCancellation.Token);
    }

    private async Task download(int beatmapId, CancellationToken cancellation)
    {
        try
        {
            var map = await lookup.GetBeatmapAsync(beatmapId, cancellation).ConfigureAwait(false);
            if (map?.BeatmapSet == null || cancellation.IsCancellationRequested || disposed) return;
            Schedule(() =>
            {
                if (disposed || cancellation.IsCancellationRequested || !isOwnedRoom()) return;
                // The public build package can have different enum ordinals
                // from the installed lazer release. Resolve the native key by name.
                downloads.Download(map.BeatmapSet, config.Get<bool>(Enum.Parse<OsuSetting>("PreferNoVideo")));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Native availability remains visible; reconnecting can retry the selected map. */ }
    }

    private static async void observe(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* The owner handles reconnects; a stale availability update must not crash gameplay. */ }
    }

    protected override void Dispose(bool isDisposing)
    {
        if (disposed) return;
        disposed = true;
        lookupCancellation?.Cancel();
        lookupCancellation?.Dispose();
        availability?.UnbindAll();
        if (client != null) client.RoomUpdated -= onRoomUpdated;
        base.Dispose(isDisposing);
    }
}
