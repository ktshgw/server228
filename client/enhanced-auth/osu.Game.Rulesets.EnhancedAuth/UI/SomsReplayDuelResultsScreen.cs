#nullable enable
using System;
using System.Threading.Tasks;
using osu.Game.Beatmaps;
using osu.Game.Online.Rooms;
using osu.Game.Scoring;
using osu.Game.Screens;
using osu.Game.Screens.Backgrounds;
using osu.Game.Screens.OnlinePlay.Multiplayer;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

// Reuse the actual multiplayer results and its score panels/statistics/animations.
// This duel has two local results, so no room leaderboard is requested.
public sealed partial class SomsReplayDuelResultsScreen : MultiplayerResultsScreen
{
    private readonly ScoreInfo opponent;
    private readonly WorkingBeatmap working;

    public SomsReplayDuelResultsScreen(ScoreInfo score, ScoreInfo opponent, WorkingBeatmap working)
        : base(score.DeepClone(), 0, new PlaylistItem(working.BeatmapInfo) { RulesetID = score.Ruleset.OnlineID })
    {
        this.opponent = opponent;
        this.working = working;
        IsLocalPlay = true;
        int comparison = score.TotalScore.CompareTo(opponent.TotalScore);
        Score!.Position = comparison >= 0 ? 1 : 2;
        opponent.Position = comparison <= 0 ? 1 : 2;
    }

    protected override Task<ScoreInfo[]> FetchScores() => Task.FromResult(new[] { opponent });
    protected override Task<ScoreInfo[]> FetchNextPage(int direction) => Task.FromResult(Array.Empty<ScoreInfo>());
    protected override BackgroundScreen CreateBackground() => new BackgroundScreenBeatmap(working);
}
