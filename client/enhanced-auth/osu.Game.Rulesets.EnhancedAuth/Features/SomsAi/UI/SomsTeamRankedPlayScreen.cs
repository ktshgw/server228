#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Database;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Game.Overlays;
using osu.Game.Screens.OnlinePlay.Components;
using osu.Game.Screens.OnlinePlay.Matchmaking.Queue;
using osu.Game.Screens.OnlinePlay.Matchmaking.RankedPlay.Hand;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>The standard ranked protocol with four independent hands and shared team life.</summary>
public sealed partial class SomsTeamRankedPlayScreen : SomsNativeMatchScreen
{
    private readonly long roomId;
    public override string Title => "Ranked 2v2";
    protected override long? ActiveRoomId => roomId;
    [Resolved] private UserLookupCache users { get; set; } = null!;
    [Resolved] private IDialogOverlay dialogs { get; set; } = null!;
    [Resolved(canBeNull: true)] private QueueController? queue { get; set; }
    private readonly FillFlowContainer teamPanel = Flow();
    private readonly OsuSpriteText turnText = Text("", 22);
    private readonly OsuSpriteText instructions = Text("", 18);
    private readonly PlayerHandOfCards hand = new() { RelativeSizeAxes = Axes.X, Height = 310, HoverYOffset = 10 };
    private RoundedButton replace = null!;
    private RoundedButton ready = null!;
    private readonly Dictionary<int, string> names = new();
    private readonly HashSet<int> requestedNames = new();
    private ScheduledDelegate? polling;
    private JObject? teamState;
    private bool pollingNow;
    private bool cardAction;
    private bool discarded;
    private bool exitConfirmed;
    private bool readySent;
    private RankedPlayStage? lastStage;

    public SomsTeamRankedPlayScreen(long roomId) => this.roomId = roomId;

    [BackgroundDependencyLoader]
    private void load()
    {
        Body.Add(Text("Две команды · 2 000 000 HP · отдельная рука у каждого игрока", 18));
        Body.Add(teamPanel);
        Body.Add(turnText);
        Body.Add(instructions);
        Body.Add(hand);
        Body.Add(replace = Button("Оставить карты", discardCards));
        Body.Add(ready = Button("Готов к карте", sendReady));
        Body.Add(Button("Выйти из матча", () => this.Exit()));
        hand.SelectionChanged += selectionChanged;
        hand.PlayCardAction = playCard;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Client.RoomUpdated += roomUpdated;
        polling = Scheduler.AddDelayed(poll, 1000, true);
        roomUpdated();
        poll();
    }

    private void roomUpdated()
    {
        if (!Alive || Client.Room?.RoomID != roomId || Client.Room.MatchState is not RankedPlayRoomState state) return;
        int localId = Api.LocalUser.Value.OnlineID;
        names[localId] = Api.LocalUser.Value.Username;
        foreach (int userId in state.Users.Keys)
            if (requestedNames.Add(userId)) _ = resolveName(userId);
        if (lastStage != state.Stage)
        {
            if (state.Stage == RankedPlayStage.CardDiscard) discarded = false;
            if (state.Stage != RankedPlayStage.GameplayWarmup) readySent = false;
            lastStage = state.Stage;
        }
        if (state.Users.TryGetValue(localId, out var ownState))
        {
            foreach (var existing in hand.Cards.ToArray())
                if (!ownState.Hand.Contains(existing.Item.Card)) hand.RemoveCard(existing.Item);
            foreach (var card in ownState.Hand) hand.AddCard(Client.GetCardWithPlaylistItem(card));
        }
        hand.SelectionMode = state.Stage switch
        {
            RankedPlayStage.CardDiscard when !discarded && !cardAction => HandSelectionMode.Multiple,
            RankedPlayStage.CardPlay when state.ActiveUserId == localId && !cardAction => HandSelectionMode.Single,
            _ => HandSelectionMode.Disabled,
        };
        hand.PlayCardAction = state.Stage == RankedPlayStage.CardPlay && state.ActiveUserId == localId ? playCard : null;
        replace.Enabled.Value = state.Stage == RankedPlayStage.CardDiscard && !discarded && !cardAction;
        ready.Enabled.Value = state.Stage == RankedPlayStage.GameplayWarmup && !readySent;
        StatusText.Text = $"Раунд {state.CurrentRound} · {label(state.Stage)}";
        turnText.Text = state.ActiveUserId is { } active ? $"Ход: {name(active)}{(active == localId ? " (вы)" : "")}" : "";
        instructions.Text = state.Stage switch
        {
            RankedPlayStage.CardDiscard => discarded ? "Замена подтверждена. Ожидаем остальных игроков." : "Выберите карты для замены или оставьте все. У каждого своя рука.",
            RankedPlayStage.CardPlay => state.ActiveUserId == localId ? "Выберите карту из своей руки и нажмите Play." : "Дождитесь своей очереди: A1 → B1 → A2 → B2.",
            RankedPlayStage.GameplayWarmup => "Загрузка выбранной карты. Готовность отправится автоматически.",
            RankedPlayStage.Ended => teamState?.Value<bool?>("cancelled") == true ? "Матч отменён без изменения рейтинга." : "Матч завершён. Итоговый рейтинг указан у игроков.",
            _ => "",
        };
        renderTeams(state);
        if (state.Stage == RankedPlayStage.GameplayWarmup && BeatmapReady) sendReady();
    }

    private async Task resolveName(int userId)
    {
        try
        {
            var user = await users.GetUserAsync(userId, Lifetime.Token).ConfigureAwait(false);
            if (user != null) OnUpdateThread(() => { names[userId] = user.Username; roomUpdated(); });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private string name(int userId) => names.TryGetValue(userId, out string? value) ? value : $"#{userId}";

    private void renderTeams(RankedPlayRoomState state)
    {
        teamPanel.Clear();
        if (teamState?["teams"] is not JArray teams)
        {
            teamPanel.Add(Text("Получаем составы команд…", 18));
            return;
        }
        foreach (JObject team in teams.OfType<JObject>())
        {
            int id = team.Value<int>("id");
            var ids = (team["user_ids"] as JArray)?.Values<int>().ToArray() ?? Array.Empty<int>();
            // The native room event has the latest HP; the supplementary endpoint supplies membership only.
            int life = ids.Select(userId => state.Users.TryGetValue(userId, out var user) ? (int?)user.Life : null).FirstOrDefault(v => v != null) ?? team.Value<int>("life");
            teamPanel.Add(Text($"Команда {(id == 0 ? "A" : "B")} · {life:N0} / 2 000 000 HP", 25));
            foreach (int userId in ids)
            {
                if (!state.Users.TryGetValue(userId, out var user)) continue;
                string rating = state.Stage == RankedPlayStage.Ended ? $"{user.Rating} → {user.RatingAfter}" : user.Rating.ToString();
                teamPanel.Add(Text($"{name(userId)} · {rating} MMR · {user.Hand.Count} карт", 18));
            }
        }
        if (teamState["turn_order"] is JArray order)
            teamPanel.Add(Text("Порядок: " + string.Join(" → ", order.Values<int>().Select(name)), 16));
        if (state.Stage == RankedPlayStage.Ended && teamState.Value<int?>("winning_team_id") is { } winner)
            teamPanel.Add(Text($"Победила команда {(winner == 0 ? "A" : "B")}", 28));
    }

    private void selectionChanged() => replace.Text = hand.Selection.Any() ? $"Replace · заменить {hand.Selection.Count()} карт" : "Оставить карты";

    private void discardCards()
    {
        if (cardAction || discarded || Client.Room?.MatchState is not RankedPlayRoomState { Stage: RankedPlayStage.CardDiscard }) return;
        var selected = hand.Selection.Select(item => item.Card).ToArray();
        cardAction = true;
        roomUpdated();
        _ = RunOperation(async () =>
        {
            try
            {
                await Client.DiscardCards(selected).ConfigureAwait(false);
                OnUpdateThread(() => discarded = true);
            }
            finally { OnUpdateThread(() => { cardAction = false; roomUpdated(); }); }
        });
    }

    private void playCard()
    {
        var card = hand.Selection.FirstOrDefault()?.Card;
        if (card == null || cardAction || Client.Room?.MatchState is not RankedPlayRoomState { Stage: RankedPlayStage.CardPlay } state || state.ActiveUserId != Api.LocalUser.Value.OnlineID) return;
        cardAction = true;
        roomUpdated();
        _ = RunOperation(async () =>
        {
            try { await Client.PlayCard(card).ConfigureAwait(false); }
            finally { OnUpdateThread(() => { cardAction = false; roomUpdated(); }); }
        });
    }

    private void sendReady()
    {
        if (readySent || !BeatmapReady || Client.Room?.MatchState is not RankedPlayRoomState { Stage: RankedPlayStage.GameplayWarmup }) return;
        readySent = true;
        _ = RunOperation(async () =>
        {
            try { await Client.ChangeState(MultiplayerUserState.Ready).ConfigureAwait(false); }
            catch { OnUpdateThread(() => readySent = false); throw; }
        });
    }

    private void poll()
    {
        if (!Alive || pollingNow || !Client.IsConnected.Value || Client.Room?.RoomID != roomId || !this.IsCurrentScreen()) return;
        pollingNow = true;
        _ = RunOperation(async () =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var connection = AccessTools.Property(typeof(OnlineMultiplayerClient), "connection").GetValue(Client) as HubConnection;
                if (connection == null) return;
                string response = await connection.InvokeAsync<string>("SomsRankedTeamState", roomId, timeout.Token).ConfigureAwait(false);
                var result = JObject.Parse(response);
                OnUpdateThread(() => { teamState = result; roomUpdated(); });
            }
            finally { OnUpdateThread(() => pollingNow = false); }
        });
    }

    public override bool OnExiting(ScreenExitEvent e)
    {
        if (!exitConfirmed && Client.Room?.MatchState is RankedPlayRoomState { Stage: not RankedPlayStage.Ended })
        {
            dialogs.Push(new ConfirmExitMultiplayerMatchDialog(() => { exitConfirmed = true; this.Exit(); }));
            return true;
        }
        if (base.OnExiting(e)) return true;
        if (Client.Room?.RoomID == roomId) _ = RunOperation(Client.LeaveRoom);
        return false;
    }

    protected override void Dispose(bool isDisposing)
    {
        polling?.Cancel();
        if (Client != null) Client.RoomUpdated -= roomUpdated;
        hand.SelectionChanged -= selectionChanged;
        base.Dispose(isDisposing);
    }

    private static string label(RankedPlayStage stage) => stage switch
    {
        RankedPlayStage.CardDiscard => "замена карт",
        RankedPlayStage.CardPlay => "выбор карты",
        RankedPlayStage.GameplayWarmup => "подготовка к игре",
        RankedPlayStage.Gameplay => "игра",
        RankedPlayStage.Results => "результаты",
        RankedPlayStage.Ended => "матч завершён",
        _ => "подготовка раунда",
    };
}
