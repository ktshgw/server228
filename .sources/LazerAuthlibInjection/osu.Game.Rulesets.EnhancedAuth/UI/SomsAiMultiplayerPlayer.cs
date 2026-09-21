#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Screens.OnlinePlay.Multiplayer;
using osu.Game.Screens.Ranking;
using osuTK;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

// Native multiplayer supplies fail-through gameplay, score submission and result navigation.
public sealed partial class SomsAiMultiplayerPlayer : MultiplayerPlayer
{
    private readonly SomsAiMatch match;
    private readonly Action<SomsAiMatch> confirmed;

    public SomsAiMultiplayerPlayer(Room room, PlaylistItem item, MultiplayerRoomUser[] users, SomsAiMatch match, Action<SomsAiMatch> confirmed)
        : base(room, item, users) => (this.match, this.confirmed) = (match, confirmed);

    protected override ResultsScreen CreateResults(ScoreInfo score)
        => new SomsAiMultiplayerResultsScreen(score, Room.RoomID!.Value, PlaylistItem, match, confirmed, externalOutcome: true) { IsLocalPlay = true };
}

public sealed partial class SomsAiMultiplayerResultsScreen : MultiplayerResultsScreen
{
    private readonly SomsAiMatch match;
    private readonly Action<SomsAiMatch> confirmed;
    private readonly CancellationTokenSource lifetime = new();
    private GetSomsAiStateRequest? pending;
    private bool disposed;
    private readonly bool externalOutcome;

    public SomsAiMultiplayerResultsScreen(ScoreInfo score, long roomId, PlaylistItem item, SomsAiMatch match, Action<SomsAiMatch> confirmed, bool externalOutcome = false)
        : base(score, roomId, item) => (this.match, this.confirmed, this.externalOutcome) = (match, confirmed, externalOutcome);

    protected override async Task<ScoreInfo[]> FetchScores()
    {
        SomsAiMatch? settled = null;
        JObject? round = null;
        // Bot results and human score processing can finish after the native ResultsReady message.
        // Only the authoritative round may decide a winner; never use incomplete live totals.
        for (int attempt = 0; attempt < 60 && !lifetime.IsCancellationRequested; attempt++)
        {
            var completion = new TaskCompletionSource<SomsAiState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new GetSomsAiStateRequest(match.RulesetId, match.VariantId);
            pending = request;
            request.Success += result => completion.TrySetResult(result);
            request.Failure += error => completion.TrySetException(error);
            API.Queue(request);
            try
            {
                var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token).ConfigureAwait(false);
                if (result.Match?.Id == match.Id)
                {
                    settled = result.Match;
                    round = settled.History.FirstOrDefault(r => r.Value<long>("playlist_item_id") == PlaylistItem.ID);
                    if (round != null || settled.IsFinished) break;
                }
                else break;
            }
            catch (OperationCanceledException) { return Array.Empty<ScoreInfo>(); }
            catch { request.Cancel(); }
            finally { pending = null; }
            try { await Task.Delay(2000, lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return Array.Empty<ScoreInfo>(); }
        }

        if (lifetime.IsCancellationRequested) return Array.Empty<ScoreInfo>();
        if (settled != null && (round != null || settled.IsFinished))
            Schedule(() =>
            {
                if (disposed) return;
                confirmed(settled);
                if (!externalOutcome || !settled.IsFinished) AddInternal(new SomsAiOutcomeAnimation(settled, Score!.UserID, round));
            });
        var scores = (await base.FetchScores().ConfigureAwait(false)).Where(s => s.UserID != Score!.UserID).ToList();
        if (round != null && settled != null)
        {
            foreach (var entry in (round["players"] as JArray ?? new JArray()).OfType<JObject>().Where(p => p.Value<bool>("is_bot") && !p.Value<bool>("missing")))
            {
                int id = entry.Value<int>("user_id");
                var player = settled.Teams.SelectMany(t => t.Members).FirstOrDefault(p => p.Id == id);
                if (player == null) continue;
                scores.Add(BotScore(entry, player, Score!, PlaylistItem.RequiredMods, API.Endpoints.APIUrl));
            }
        }
        var ordered = scores.Append(Score!).OrderByDescending(s => s.TotalScore).ThenBy(s => s.UserID).ToArray();
        for (int i = 0; i < ordered.Length; i++) ordered[i].Position = i + 1;
        if (!disposed)
        {
            Schedule(() =>
            {
                if (disposed) return;
                ScorePanelList.GetPanelForScore(Score!).ScorePosition.Value = Score!.Position;
            });
        }
        return scores.ToArray();
    }

    internal static ScoreInfo BotScore(JObject entry, SomsPlayer player, ScoreInfo local, APIMod[] requiredMods, string apiUrl)
    {
        var ruleset = local.Ruleset.CreateInstance();
        return new ScoreInfo(local.BeatmapInfo, local.Ruleset)
        {
            User = new APIUser { Id = player.Id, Username = player.OfficialUsername ?? player.Username,
                AvatarUrl = new Uri(new Uri(apiUrl), player.AvatarUrl ?? $"/users/{player.Id}/avatar").AbsoluteUri },
            BeatmapHash = local.BeatmapHash, Date = local.Date,
            TotalScore = entry.Value<long>("score"), Accuracy = entry.Value<double>("accuracy"), MaxCombo = entry.Value<int>("max_combo"),
            Passed = entry.Value<bool?>("passed") ?? true,
            Rank = Enum.TryParse<ScoreRank>(entry.Value<string>("rank"), out var rank) ? rank : ScoreRank.A,
            Mods = requiredMods.Select(m => m.ToMod(ruleset)).ToArray(),
            Statistics = entry["statistics"]?.ToObject<Dictionary<HitResult, int>>() ?? new(),
            MaximumStatistics = entry["maximum_statistics"]?.ToObject<Dictionary<HitResult, int>>() ?? new(),
        };
    }

    protected override Task<ScoreInfo[]> FetchNextPage(int direction) => Task.FromResult(Array.Empty<ScoreInfo>());

    protected override void Dispose(bool isDisposing)
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        pending?.Cancel();
        base.Dispose(isDisposing);
    }
}

// A short result reveal sits over the existing multiplayer panels, then gives them back.
public sealed partial class SomsAiOutcomeAnimation : Container
{
    public SomsAiOutcomeAnimation(SomsAiMatch match, int localId, JObject? round = null)
    {
        RelativeSizeAxes = Axes.Both;
        Depth = -1000;
        int? localTeam = match.Teams.FirstOrDefault(t => t.Members.Any(p => p.Id == localId))?.Id;
        int? winner = match.IsFinished ? match.WinnerTeamId : round?.Value<int?>("winner_team_id");
        bool? won = localTeam == null || winner == null ? null : localTeam == winner;
        var scores = round?["team_scores"]?.ToObject<long[]>() ?? Array.Empty<long>();
        long difference = localTeam == 1
            ? scores.ElementAtOrDefault(1) - scores.ElementAtOrDefault(0)
            : scores.ElementAtOrDefault(0) - scores.ElementAtOrDefault(1);
        var colour = won == true ? Colour4.FromHex("6EE6A5") : won == false ? Colour4.FromHex("FF6978") : Colour4.FromHex("9BBBD7");
        string title = match.Stage == "cancelled" ? "МАТЧ ОТМЕНЁН" : won == true ? "ПОБЕДА" : won == false ? "ПОРАЖЕНИЕ" : "НИЧЬЯ";
        var band = new Container
        {
            RelativeSizeAxes = Axes.X, Height = 210, Anchor = Anchor.Centre, Origin = Anchor.Centre,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = ColourInfo.GradientHorizontal(colour.Opacity(.08f), colour.Opacity(.3f)) },
                new Box { RelativeSizeAxes = Axes.X, Height = 2, Colour = colour },
                new Box { RelativeSizeAxes = Axes.X, Height = 2, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Colour = colour },
                new OsuSpriteText { Anchor = Anchor.Centre, Origin = Anchor.Centre, Y = -16, Text = title, Font = OsuFont.Torus.With(size: 62, weight: FontWeight.Bold), Colour = colour },
                new OsuSpriteText { Anchor = Anchor.Centre, Origin = Anchor.Centre, Y = 45,
                    Text = match.IsFinished ? $"Матч завершён · {match.Wins.ElementAtOrDefault(0)} : {match.Wins.ElementAtOrDefault(1)}" : $"Разница очков · {difference:+#,0;-#,0;+0}",
                    Font = OsuFont.GetFont(size: 22) },
            },
        };
        Children = new Drawable[] { new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Black.Opacity(.75f) }, band };
        band.Scale = new Vector2(1.06f);
        OnLoadComplete += _ =>
        {
            this.FadeInFromZero(250).Delay(2400).FadeOut(450).Expire();
            band.ScaleTo(1, 600, Easing.OutQuint);
        };
    }
}
