#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Extensions;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Rooms;
using osu.Game.Online.Solo;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Screens.Ranking;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

// Submit the human play through the same token and score endpoints as solo.
// The opponent is only a clock-driven replay simulation, never a submitting player.
public sealed partial class SomsReplayDuelPlayer : SubmittingPlayer
{
    [Cached(typeof(IGameplayLeaderboardProvider))]
    private readonly DuelLeaderboard leaderboard = new();
    private readonly Score replay;
    private readonly APIUser localUser;
    private SomsReplayOpponent opponent = null!;
    private double nextTextUpdate;

    public SomsReplayDuelPlayer(Score replay, APIUser localUser)
        : base(new PlayerConfiguration { AllowRestart = false, ShowLeaderboard = true })
    {
        this.replay = replay;
        this.localUser = localUser;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        if (!LoadedBeatmapSuccessfully) return;
        GameplayState.Score.ScoreInfo.User = localUser;
        ScoreProcessor.ApplyNewJudgementsWhenFailed = true;
        // Only the native live leaderboard is visible. The replay continues to
        // run offscreen using the same map clock for accurate scores and combo.
        AddInternal(opponent = new SomsReplayOpponent(Beatmap.Value, replay, GameplayClockContainer));
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (!LoadedBeatmapSuccessfully) return;
        leaderboard.Items.Add(new GameplayLeaderboardScore(GameplayState, true, GameplayLeaderboardScore.ComboDisplayMode.Current));
        leaderboard.Items.Add(new GameplayLeaderboardScore(opponent.State, false, GameplayLeaderboardScore.ComboDisplayMode.Current));
        leaderboard.Sort();
    }

    // Match native multiplayer: keep playing after failure, but preserve F rank
    // and failed status for submission instead of treating it as a passing play.
    protected override void PerformFail() => ScoreProcessor.FailScore(Score.ScoreInfo);

    protected override APIRequest<APIScoreToken>? CreateTokenRequest()
    {
        var map = Beatmap.Value.BeatmapInfo;
        if (map.OnlineID <= 0 || map.Status == BeatmapOnlineStatus.LocallyModified || !Ruleset.Value.IsLegacyRuleset()) return null;
        return new CreateSoloScoreRequest(map, Ruleset.Value.OnlineID, Game.VersionHash);
    }

    protected override bool ShouldExitOnTokenRetrievalFailure(Exception exception) => false;
    protected override APIRequest<MultiplayerScore> CreateSubmissionRequest(Score score, long token)
        => new SubmitSoloScoreRequest(score.ScoreInfo, token, score.ScoreInfo.BeatmapInfo!.OnlineID);

    protected override ResultsScreen CreateResults(ScoreInfo score)
    {
        var rival = replay.ScoreInfo.DeepClone();
        opponent.Processor.PopulateScore(rival);
        return new SomsReplayDuelResultsScreen(score, rival, Beatmap.Value);
    }

    protected override void UpdateAfterChildren()
    {
        base.UpdateAfterChildren();
        if (opponent == null || !this.IsCurrentScreen()) return;
        if (Time.Current >= nextTextUpdate)
        {
            nextTextUpdate = Time.Current + 100;
            leaderboard.Sort();
        }
    }

    private sealed class DuelLeaderboard : IGameplayLeaderboardProvider
    {
        public BindableList<GameplayLeaderboardScore> Items { get; } = new();
        public IBindableList<GameplayLeaderboardScore> Scores => Items;
        public void Sort()
        {
            int rank = 1;
            foreach (var score in Items.OrderByDescending(s => s.TotalScore.Value).ThenBy(s => s.Tracked ? 1 : 0))
            {
                score.Position.Value = rank;
                score.DisplayOrder.Value = rank++;
            }
        }
    }
}
