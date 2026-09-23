#nullable enable
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Audio;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.API;
using osu.Game.Online.Rooms;
using osu.Game.Overlays;
using osu.Game.Overlays.Dialog;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>A native tournament lobby and draft screen. All authority remains on the server.</summary>
public partial class SomsAiScreen : SomsNativeMatchScreen, IPreviewTrackOwner
{
    public override string Title => partyOnly ? "Пати SOMS!" : matchOnly ? "SOMSAI · Матч" : "SOMSAI";
    private readonly bool partyOnly;
    private readonly bool matchOnly;
    private readonly int? boundMatchId;
    private readonly FillFlowContainer ratingPanel = Flow();
    private readonly FillFlowContainer partyPanel = Flow();
    private readonly FillFlowContainer matchPanel = Flow();
    private readonly FillFlowContainer queuePanel = Flow();
    private readonly FillFlowContainer customPanel = Flow();
    private readonly FillFlowContainer recentPanel = Flow();
    private readonly FormTextBox inviteTarget = new() { Caption = "Ник игрока", PlaceholderText = "Точный ник в SOMS!", LengthLimit = 32 };
    private readonly FormTextBox customName = new() { Caption = "Название комнаты", PlaceholderText = "Турнир SOMSAI", LengthLimit = 80 };
    private readonly FormDropdown<int> variant = new() { Caption = "Количество клавиш osu!mania", Items = new[] { 4, 7 }, Current = { Value = 4 } };
    [Resolved] private IDialogOverlay dialogs { get; set; } = null!;
    [Resolved(canBeNull: true)] private PreviewTrackManager? previews { get; set; }
    private SomsAiState state = new();
    private GetSomsAiStateRequest? stateRequest;
    private ApplySomsAiActionRequest? actionRequest;
    private ScheduledDelegate? polling;
    private string? lastRenderedState;
    private string? lastParty;
    private string? lastNativePlaylist;
    private long? joiningRoom;
    private bool leaving;
    private bool pendingReady;
    private bool preparingAction;
    private double stateRequestedAt;
    private double actionRequestedAt;
    private bool exitConfirmed;
    private int? lastOpenedMatchId;
    private int variantId => Ruleset.Value.OnlineID == 3 ? variant.Current.Value : 0;
    protected override long? ActiveRoomId => matchOnly ? state.Match?.RoomId : null;

    public SomsAiScreen(bool partyOnly = false) => this.partyOnly = partyOnly;
    protected SomsAiScreen(SomsAiState initial)
    {
        state = initial;
        matchOnly = true;
        boundMatchId = initial.Match?.Id;
        if (initial.Match?.VariantId is 4 or 7) variant.Current.Value = initial.Match.VariantId;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        if (matchOnly) buildMatchScreen();
        else buildDashboard();
        // Show usable controls and placeholders immediately, even while the first state request is in flight.
        render();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Client.RoomUpdated += onNativeRoomUpdated;
        polling = (matchOnly ? GlobalScheduler : Scheduler).AddDelayed(refresh, 2000, true);
    }

    private void onNativeRoomUpdated()
    {
        if (!Alive || !matchOnly || !this.IsCurrentScreen()) return;
        updateArenaRoom();
        var playlist = OwnsCurrentRoom ? Client.Room!.Playlist.FirstOrDefault(p => p.ID == Client.Room.Settings.PlaylistItemId) : null;
        string signature = playlist == null ? "" : $"{playlist.ID}:{string.Join(',', playlist.AllowedMods.Select(m => m.Acronym))}:{string.Join(',', Client.LocalUser?.Mods.Select(m => m.Acronym) ?? Array.Empty<string>())}";
        if (signature != lastNativePlaylist)
        {
            lastNativePlaylist = signature;
            renderMatch();
        }
        tryReady();
    }

    public override void OnEntering(ScreenTransitionEvent e)
    {
        base.OnEntering(e);
        showOcean();
        if (matchOnly) render();
        if (matchOnly) playMatchIntro();
        refresh();
    }

    public override void OnSuspending(ScreenTransitionEvent e)
    {
        stateRequest?.Cancel();
        stateRequest = null;
        stopPreviews();
        oceanContent.Hide();
        base.OnSuspending(e);
    }

    public override void OnResuming(ScreenTransitionEvent e)
    {
        base.OnResuming(e);
        showOcean();
        if (matchOnly) { lastRenderedState = null; render(); }
        refresh();
    }

    private void refresh()
    {
        if (!Alive || leaving || (!this.IsCurrentScreen() && (!matchOnly || !OwnsCurrentRoom))) return;
        if (stateRequest != null)
        {
            if (Time.Current - stateRequestedAt > 15000)
            {
                var stalled = stateRequest;
                stateRequest = null;
                stalled.Cancel();
                StatusText.Text = "Сервер не ответил. Повторное подключение…";
            }
            return;
        }
        if (actionRequest != null)
        {
            if (Time.Current - actionRequestedAt > (creatingRoom ? 45000 : 15000))
            {
                var stalled = actionRequest;
                actionRequest = null;
                stalled.Cancel();
                setCreatingRoom(false);
                StatusText.Text = "Ответ на действие задержался. Проверяем его результат на сервере…";
            }
            return;
        }
        var request = stateRequest = new GetSomsAiStateRequest(matchOnly ? state.Match!.RulesetId : Ruleset.Value.OnlineID, matchOnly ? state.Match!.VariantId : variantId);
        stateRequestedAt = Time.Current;
        request.Success += result => OnUpdateThread(() =>
        {
            if (stateRequest != request) return;
            stateRequest = null;
            if (!this.IsCurrentScreen())
            {
                if (result.Match?.Id == boundMatchId)
                {
                    state = result;
                    showConfirmedMatchOutcome(result.Match!, Api.LocalUser.Value.Id);
                }
                return;
            }
            if (matchOnly && result.Match?.Id != boundMatchId)
            {
                stopPreviews();
                exitConfirmed = true;
                _ = RunOperation(async () =>
                {
                    if (OwnsCurrentRoom) await Client.LeaveRoom().ConfigureAwait(false);
                    OnUpdateThread(() => this.Exit());
                });
                return;
            }
            state = result;
            render();
            if (!partyOnly && !matchOnly && state.Match is { IsFinished: false } activeMatch && activeMatch.Id != lastOpenedMatchId)
            {
                openMatch();
                return;
            }
            if (matchOnly) playMatchIntro();
            if (matchOnly && CanJoinNativeMatch(state.Match)) joinMatchRoom();
            tryReady();
        });
        request.Failure += exception => OnUpdateThread(() =>
        {
            if (stateRequest != request) return;
            stateRequest = null;
            StatusText.Text = "SOMSAI: " + exception.Message;
        });
        Api.Queue(request);
    }

    private void openMatch()
    {
        if (state.Match == null || !this.IsCurrentScreen()) return;
        // Opening a match is navigation, not a side effect of every status poll.
        // Remember it before pushing so Back can resume this lobby without re-entry.
        lastOpenedMatchId = state.Match.Id;
        this.Push(new SomsAiMatchScreen(state));
    }

    private void render()
    {
        if (matchOnly)
        {
            string matchSignature = JsonConvert.SerializeObject(state.Match);
            if (matchSignature != lastRenderedState) { lastRenderedState = matchSignature; renderMatch(); }
            updateStatus();
            return;
        }
        string partySignature = JsonConvert.SerializeObject(new { state.Party, state.Invites, state.Ratings, selectedFormat });
        if (lastParty != partySignature)
        {
            lastParty = partySignature;
            renderParty();
        }
        if (partyOnly) { StatusText.Text = "Вернитесь в SOMSAI и выберите 2v2. Поиск запускает капитан."; return; }
        string signature = JsonConvert.SerializeObject(state);
        if (signature == lastRenderedState) { updateStatus(); return; }
        lastRenderedState = signature;
        renderRecentMatches();
        renderSearch();
        renderCustoms();
        renderMatch();
        updateStatus();
    }

    private void updateStatus()
    {
        if (creatingRoom) return;
        if (state.Match is { } match && (matchOnly || !match.IsFinished))
        {
            string deadline = match.Deadline is { } end ? $" · {Math.Max(0, (int)(end - DateTimeOffset.UtcNow).TotalSeconds)} сек." : "";
            StatusText.Text = stageLabel(match.Stage) + deadline;
        }
        else if (state.Queue is { } queue)
        {
            string elapsed = queue.JoinedAt is { } start ? $" · {(DateTimeOffset.UtcNow - start):mm\\:ss}" : "";
            StatusText.Text = $"Ищем соперников {queue.Format}{elapsed}";
        }
        else StatusText.Text = "";
    }

    private void renderMatch()
    {
        matchPanel.Clear();
        if (!matchOnly)
        {
            if (state.Match is { IsFinished: false })
            {
                matchCard.Show();
                matchPanel.Add(Button("Перейти в матч", openMatch));
            }
            else
                matchCard.Hide();
            return;
        }
        if (state.Match is not { } match) return;
        int localId = Api.LocalUser.Value.OnlineID;
        renderMatchHeader(match);
        setArenaPrimary("Ожидание");
        switch (match.Stage)
        {
            case "pool_select":
                renderPoolVote(match, localId);
                break;
            case "waiting":
                if (match.Ranked) setArenaPrimary("Принять матч", () => action("ready"));
                else matchPanel.Add(Paragraph("Матч начнётся автоматически, когда обе команды заполнятся."));
                break;
            case "ready":
                var playlist = Client.Room?.Playlist.FirstOrDefault(p => p.ID == Client.Room.Settings.PlaylistItemId);
                if (playlist != null && OwnsCurrentRoom && playlist.AllowedMods.Any())
                {
                    matchPanel.Add(Text("Ваши FreeMod: " + (Client.LocalUser?.Mods.Any() == true ? string.Join(" + ", Client.LocalUser.Mods.Select(m => m.Acronym)) : "NM"), 18));
                    var allowed = playlist.AllowedMods.Select(m => m.Acronym).ToHashSet();
                    foreach (var mods in new[] { Array.Empty<string>(), new[] { "HD" }, new[] { "HR" }, new[] { "HD", "HR" } })
                    {
                        if (!mods.All(allowed.Contains)) continue;
                        var selection = mods.Select(acronym => new APIMod { Acronym = acronym }).ToArray();
                        matchPanel.Add(new ArenaButton { Text = "FreeMod: " + (mods.Length == 0 ? "NM" : string.Join(" + ", mods)),
                            Action = () => _ = RunOperation(() => Client.ChangeUserMods(selection)) });
                    }
                }
                bool ready = match.Teams.SelectMany(t => t.Members).Any(p => p.Id == localId && p.Ready);
                setArenaPrimary(ready ? "Снять готовность" : pendingReady ? "Отменить ожидание карты" : "Готов к карте", () =>
                {
                    if (pendingReady) { pendingReady = false; renderMatch(); return; }
                    if (ready)
                    {
                        action("unready", afterSuccess: () => _ = RunOperation(() => Client.ChangeState(MultiplayerUserState.Idle)));
                    }
                    else { pendingReady = true; tryReady(); renderMatch(); }
                });
                break;
            case "results":
                matchPanel.Add(Text("Результат карты принят. Ожидаем следующий выбор…", 18));
                break;
            case "ended":
                setArenaPrimary("Закрыть результат", () => action("leave_match"));
                matchPanel.Add(Text("Матч завершён", 24));
                if (match.WinnerTeamId is { } winner)
                {
                    var winningTeam = match.Teams.FirstOrDefault(team => team.Id == winner);
                    matchPanel.Add(Paragraph("Победитель: " + (winningTeam != null ? teamName(winningTeam) : $"команда {winner + 1}"), 25));
                }
                else matchPanel.Add(Text("Ничья", 22));
                foreach (var change in match.RatingChanges.OrderByDescending(change => change.UserId == localId))
                {
                    var player = match.Teams.SelectMany(team => team.Members).FirstOrDefault(player => player.Id == change.UserId);
                    var line = Paragraph($"{player?.Username ?? $"#{change.UserId}"}{(change.UserId == localId ? " (вы)" : "")} · {change.Before:0.##} → {change.After:0.##} MMR · {change.Delta:+0.##;-0.##;0} · вклад {change.Impact}/100", 20);
                    if (change.UserId == localId) line.Colour = new Color4(123, 224, 208, 255);
                    matchPanel.Add(line);
                }
                break;
            case "cancelled":
                setArenaPrimary("Выйти из матча", () => action("leave_match"));
                matchPanel.Add(Text("Матч отменён", 24));
                break;
        }
        renderRoundHistory(match, localId);
        showConfirmedMatchOutcome(match, localId);
        if (!string.IsNullOrEmpty(match.Reason)) matchPanel.Add(Paragraph(match.Reason, 18));
        updateArena(match, localId);
        updateMapBoard(match, localId);
    }

    private void tryReady()
    {
        if (!pendingReady || actionRequest != null) return;
        if (state.Match?.Stage != "ready") { pendingReady = false; return; }
        string? selectedSlot = SelectedSlotId(state.Match);
        var slot = state.Match.Slots.FirstOrDefault(s => s.Id == selectedSlot);
        var playlist = Client.Room?.Playlist.FirstOrDefault(p => p.ID == Client.Room.Settings.PlaylistItemId);
        if (!OwnsCurrentRoom || !BeatmapReady || playlist == null || (slot != null &&
            (playlist.BeatmapID != slot.BeatmapId || !HasBeatmapRevision(slot.BeatmapId, slot.Checksum))))
        {
            StatusText.Text = "Загружаем выбранную карту. Готовность отправится автоматически.";
            return;
        }
        pendingReady = false;
        action("ready", afterSuccess: () => _ = RunOperation(() => Client.ChangeState(MultiplayerUserState.Ready)));
    }

    private void joinMatchRoom()
    {
        var match = state.Match!;
        if (match.RoomId == null || joiningRoom != null || !Client.IsConnected.Value || Client.Room?.RoomID == match.RoomId) return;
        if (Client.Room != null) { StatusText.Text = "Сначала выйдите из другой мультиплеерной комнаты."; return; }
        long roomId = match.RoomId.Value;
        joiningRoom = roomId;
        _ = RunOperation(async () =>
        {
            try
            {
                await Client.JoinRoom(new Room { RoomID = roomId }, match.Password).ConfigureAwait(false);
                if ((!Alive || leaving) && Client.Room?.RoomID == roomId)
                    await Client.LeaveRoom().ConfigureAwait(false);
            }
            finally { OnUpdateThread(() => joiningRoom = null); }
        });
    }

    internal static bool CanJoinNativeMatch(SomsAiMatch? match) => match is { IsFinished: false, RoomId: > 0 }
        && match.Format is "1v1" or "2v2" or "3v3" or "4v4"
        && match.Teams.Count == 2 && match.Teams.All(t => t.Members.Count == match.Format[0] - '0');

    internal static string? SelectedSlotId(SomsAiMatch match) => match.MapSlot switch
    {
        JObject slot => slot.Value<string>("id"),
        { Type: JTokenType.String } slot => slot.Value<string>(),
        _ => null,
    };

    private void createCustom(string format)
    {
        if (creatingRoom || actionRequest != null || preparingAction) return;
        string rank = customRankBand.Current.Value == "ARCHSOM"
            ? "ARCHSOM"
            : $"{customRankBand.Current.Value} {customRankDivision.Current.Value}";
        string[] levels = SomsAiBotSimulation.LevelKeys;
        action("custom_create", new JObject { ["format"] = format, ["name"] = customName.Current.Value, ["target_rank"] = rank,
                ["with_bots"] = customWithBots.Current.Value,
                ["bot_level"] = levels[Math.Max(0, Array.IndexOf(SomsAiBotSimulation.Labels, customBotLevel.Current.Value))] },
            afterSuccess: () => customsOverlay?.HidePanel());
    }

    private void updateCustomRankDivision()
    {
        customRankDivision.Current.Disabled = creatingRoom || customRankBand.Current.Value == "ARCHSOM";
        if (customRankBand.Current.Value == "ARCHSOM") customRankDivision.Current.Value = "I";
    }

    private void setCustomRank(double rating)
    {
        string band;
        string division;

        if (rating >= 3000)
        {
            band = "ARCHSOM";
            division = "I";
        }
        else if (rating < 600)
        {
            band = "BRONZE";
            division = "I";
        }
        else
        {
            int bandIndex = Math.Clamp((int)(rating / 500) - 1, 0, 4);
            int divisionIndex = Math.Clamp((int)((rating - (500 + bandIndex * 500)) / 100), 0, 4);

            band = new[] { "BRONZE", "SILVER", "GOLD", "PLATINUM", "DIAMOND" }[bandIndex];
            division = customRankDivisions[divisionIndex];
        }

        customRankDivision.Current.Disabled = false;
        customRankDivision.Current.Value = division;
        customRankBand.Current.Value = band;
        updateCustomRankDivision();
    }

    private void action(string name, JObject? fields = null, int? revision = null, Action? afterSuccess = null)
    {
        if (!Alive || leaving || actionRequest != null || preparingAction) return;
        if (name is "queue_join" or "custom_create" or "custom_join" && state.Match is { IsFinished: true } && Client.Room?.RoomID == state.Match.RoomId)
        {
            preparingAction = true;
            if (name == "custom_create") setCreatingRoom(true);
            StatusText.Text = "Закрываем завершённую комнату…";
            _ = RunOperation(async () =>
            {
                bool completed = false;
                try { await Client.LeaveRoom().ConfigureAwait(false); completed = true; }
                finally
                {
                    OnUpdateThread(() =>
                    {
                        preparingAction = false;
                        setCreatingRoom(false);
                        if (completed) action(name, fields, revision, afterSuccess);
                    });
                }
            });
            return;
        }
        stateRequest?.Cancel();
        stateRequest = null;
        var body = fields ?? new JObject();
        body["action"] = name;
        body["ruleset_id"] = Ruleset.Value.OnlineID;
        body["variant_id"] = variantId;
        if (state.Match is { } match && name is "custom_join" or "custom_start" or "ban" or "pick" or "pool_vote" or "ready" or "unready" or "leave_match"
            && (body.Value<int?>("match_id") is not { } targetMatch || targetMatch == match.Id))
        {
            body["match_id"] ??= match.Id;
            // Readiness may change concurrently. Leaving the explicitly named match
            // must also remain possible when a bot picks or another player gets ready.
            if (name is not ("ready" or "unready" or "pool_vote" or "leave_match")) body["expected_revision"] = revision ?? match.Revision;
            body["ruleset_id"] = match.RulesetId;
            body["variant_id"] = match.VariantId;
        }
        var request = actionRequest = new ApplySomsAiActionRequest(body);
        if (name == "leave_match") StatusText.Text = "Выходим из матча…";
        if (name == "custom_create")
        {
            setCreatingRoom(true);
            StatusText.Text = customWithBots.Current.Value ? "Создаём комнату и подбираем профили ботов…" : "Создаём комнату…";
        }
        actionRequestedAt = Time.Current;
        request.Success += response => OnUpdateThread(() =>
        {
            if (actionRequest != request) return;
            actionRequest = null;
            setCreatingRoom(false);
            afterSuccess?.Invoke();
            if (name == "leave_match" && matchOnly)
            {
                // The server retains the final result for the lobby's result button.
                // A successful explicit leave should close this screen immediately.
                exitConfirmed = true;
                pendingReady = false;
                this.Exit();
                return;
            }
            if (name is "queue_join" or "custom_create" or "custom_join") lastOpenedMatchId = null;
            StatusText.Text = "Обновление…";
            refresh();
        });
        request.Failure += error => OnUpdateThread(() =>
        {
            if (actionRequest != request) return;
            actionRequest = null;
            setCreatingRoom(false);
            StatusText.Text = error.Message;
            lastRenderedState = null;
        });
        Api.Queue(request);
    }

    public override bool OnExiting(ScreenExitEvent e)
    {
        if (actionRequest != null || preparingAction)
        {
            StatusText.Text = "Дождитесь ответа на действие, затем можно выйти.";
            return true;
        }
        if (matchOnly && !exitConfirmed && state.Match is { IsFinished: false, Stage: not "waiting" })
        {
            dialogs.Push(new SomsAiOceanConfirmDialog("Покинуть экран матча? У вас будет 120 секунд, чтобы вернуться через SOMSAI. Затем команда проиграет.", () => { exitConfirmed = true; this.Exit(); }));
            return true;
        }
        if (base.OnExiting(e)) return true;
        oceanContent.FadeOut(180);
        stopPreviews();
        leaving = true;
        stateRequest?.Cancel();
        stateRequest = null;
        // The accepted match remains resumable from SOMSAI. Only an explicit "leave match" abandons it.
        if (OwnsCurrentRoom) _ = RunOperation(Client.LeaveRoom);
        if (!matchOnly && !partyOnly && (state.Match == null || state.Match.IsFinished || state.Queue != null))
            Api.Queue(new ApplySomsAiActionRequest(new JObject { ["action"] = "queue_leave", ["ruleset_id"] = Ruleset.Value.OnlineID, ["variant_id"] = variantId }));
        return false;
    }

    protected override void Dispose(bool isDisposing)
    {
        if (!Alive) return;
        stopPreviews();
        polling?.Cancel();
        if (Client != null) Client.RoomUpdated -= onNativeRoomUpdated;
        stateRequest?.Cancel();
        actionRequest?.Cancel();
        stateRequest = null;
        actionRequest = null;
        base.Dispose(isDisposing);
    }

    private static string stageLabel(string stage) => stage switch
    {
        "waiting" => "Подтверждение участников",
        "pool_select" => "Выбор турнирного пула",
        "banning" => "Баны карт",
        "picking" => "Выбор карты",
        "ready" => "Готовность к карте",
        "playing" => "Идёт игра",
        "results" => "Результаты карты",
        "ended" => "Матч завершён",
        "cancelled" => "Матч отменён",
        _ => "Обновление матча",
    };

    internal static string SlotCaption(SomsAiSlot slot)
    {
        string metadata = string.IsNullOrWhiteSpace(slot.Artist) ? slot.Title : $"{slot.Artist} — {slot.Title}";
        if (!string.IsNullOrWhiteSpace(slot.Version)) metadata += $" [{slot.Version}]";
        return $"{(string.IsNullOrWhiteSpace(slot.Label) ? slot.Id : slot.Label)} · {metadata} · {slot.Stars:0.00}★ · {slot.Status}";
    }

}
