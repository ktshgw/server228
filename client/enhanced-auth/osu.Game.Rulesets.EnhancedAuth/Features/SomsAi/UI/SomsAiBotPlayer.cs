#nullable enable
using System;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Screens;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Screens.Ranking;
using osuTK;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public sealed record SomsAiBotResult(long PlayerScore, double PlayerAccuracy, int PlayerCombo, long BotScore, double BotAccuracy, int BotCombo, bool AccuracyBattle)
{
    public int Winner => AccuracyBattle ? PlayerAccuracy.CompareTo(BotAccuracy) : PlayerScore.CompareTo(BotScore);
}

/// <summary>Local practice. Deliberately inherits Player, which has no online submission path.</summary>
public sealed partial class SomsAiBotPlayer : Player
{
    [Cached(typeof(IGameplayLeaderboardProvider))]
    private readonly EmptyGameplayLeaderboardProvider leaderboard = new();
    private readonly SomsAiBotLevel level;
    private readonly int seed;
    private readonly Action<SomsAiBotResult> completed;
    private SomsAiBotSimulation bot = null!;
    private readonly OsuSpriteText live = new() { Font = OsuFont.GetFont(size: 18), Colour = SomsAiOceanTheme.Cream };
    private bool finished;

    public SomsAiBotPlayer(SomsAiBotLevel level, int seed, Action<SomsAiBotResult> completed)
        : base(new PlayerConfiguration { ShowResults = false, AllowRestart = false, ShowLeaderboard = false })
    {
        this.level = level; this.seed = seed; this.completed = completed;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        // The playable map is ready here; DrawableRuleset.Objects is populated later during child loading.
        if (GameplayState == null) return;
        double stars = GameplayState.Ruleset.CreateDifficultyCalculator(Beatmap.Value).Calculate(GameplayState.Mods).StarRating;
        bot = new SomsAiBotSimulation(GameplayState.Ruleset, GameplayState.Beatmap, GameplayState.Mods, level, stars, seed);
        AddInternal(bot.Processor);
        AddInternal(new Container
        {
            Name = "somsai-bot-live-score", Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
            Position = new Vector2(12, 0), Size = new Vector2(275, 108),
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = SomsAiOceanTheme.Ink, Alpha = .85f },
                new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding(12), Child = live },
            },
        });
    }

    protected override bool CheckModsAllowFailure() => false;
    protected override Task ImportScore(Score score) => Task.CompletedTask;
    protected override ResultsScreen CreateResults(ScoreInfo score) => throw new InvalidOperationException("Practice results return to the bot lobby.");

    protected override void Update()
    {
        base.Update();
        if (bot == null || finished || !this.IsCurrentScreen()) return;
        bot.Advance(GameplayClockContainer.CurrentTime);
        var p = bot.Processor;
        live.Text = $"{SomsAiBotSimulation.NameFor(level)} · BOT\n{p.TotalScore.Value:N0}  ·  {p.Accuracy.Value:P2}\n{p.Combo.Value}x  |  Вы: {ScoreProcessor.TotalScore.Value:N0}";
        if (!GameplayState.HasPassed) return;
        finished = true;
        bot.Advance(double.PositiveInfinity);
        var result = new SomsAiBotResult(ScoreProcessor.TotalScore.Value, ScoreProcessor.Accuracy.Value,
            ScoreProcessor.HighestCombo.Value, p.TotalScore.Value, p.Accuracy.Value, p.HighestCombo.Value, level == SomsAiBotLevel.Mrekk);
        Scheduler.AddDelayed(() => completed(result), 1200);
    }
}
