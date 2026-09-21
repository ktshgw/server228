#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Utils;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public partial class SomsMapPerformance : CompositeDrawable
{
    // The debounce and completed calculation must keep updating while disabled.
    // Otherwise setting Alpha=0 strands the subsequent enable callback forever.
    public SomsMapPerformance() => AlwaysPresent = true;
    [Resolved] private IBindable<WorkingBeatmap> working { get; set; } = null!;
    [Resolved] private IBindable<RulesetInfo> ruleset { get; set; } = null!;
    [Resolved] private IBindable<IReadOnlyList<Mod>> mods { get; set; } = null!;
    private Bindable<bool> enabled = null!;
    private ModSettingChangeTracker? tracker;
    private CancellationTokenSource? cancellation;
    private ScheduledDelegate? pending;
    private int generation;
    private bool disposed;
    internal Action? UpdateHeaderLayout;
    public Func<StarRatingDisplay?>? DifficultySource { get; init; }
    private StarRatingDisplay? difficulty;
    private readonly Box[] accuracyBackgrounds = Enumerable.Range(0, 4).Select(_ => new Box { RelativeSizeAxes = Axes.Both }).ToArray();
    private readonly OsuSpriteText[] accuracyLabels = new[] { "95%", "98%", "99%", "100%" }.Select(text => new OsuSpriteText
    { Text = text, Anchor = Anchor.Centre, Origin = Anchor.Centre, Font = OsuFont.GetFont(size: 13, weight: FontWeight.Bold) }).ToArray();
    private readonly OsuSpriteText[] values = Enumerable.Range(0, 4).Select(_ => new OsuSpriteText { Text = "—", Font = OsuFont.GetFont(size: 18, weight: FontWeight.Bold) }).ToArray();
    private readonly OsuSpriteText caption = new() { Text = "pp за FC · оценка", Font = OsuFont.GetFont(size: 13) };

    [BackgroundDependencyLoader]
    private void load(OverlayColourProvider colours)
    {
        Masking = true; CornerRadius = 8;
        InternalChildren = new Drawable[]
        {
            // Opaque theme background: marquee text must pass behind the card,
            // without showing through the pp values. The whole card is above the header.
            new Box { RelativeSizeAxes = Axes.Both, Colour = colours.Background5, Depth = 1 },
            new FillFlowContainer { RelativeSizeAxes = Axes.Both, Direction = FillDirection.Vertical, Padding = new MarginPadding { Horizontal = 10, Vertical = 8 },
                Spacing = new Vector2(0, 4), Children = new Drawable[]
                {
                    caption,
                    new FillFlowContainer { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new Vector2(12, 0),
                        ChildrenEnumerable = Enumerable.Range(0, 4).Select(i => new FillFlowContainer
                        { Width = 48, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new Vector2(0, 3), Children = new Drawable[]
                            { new CircularContainer { Name = "soms-pp-accuracy-" + i, Size = new Vector2(48, 20), Masking = true,
                                Children = new Drawable[] { accuracyBackgrounds[i], accuracyLabels[i] } }, values[i] } }) },
                } },
        };
        enabled = SomsClientPreferences.Instance.ShowMapPP.GetBoundCopy();
        enabled.BindValueChanged(_ => changed(), true);
        working.BindValueChanged(_ => changed());
        ruleset.BindValueChanged(_ => changed());
        mods.BindValueChanged(_ => { tracker?.Dispose(); tracker = new ModSettingChangeTracker(mods.Value) { SettingChanged = _ => changed() }; changed(); }, true);
    }

    protected override void Update()
    {
        base.Update();
        UpdateHeaderLayout?.Invoke();
        difficulty ??= DifficultySource?.Invoke();
        if (difficulty?.IsLoaded != true) return;
        // Follow the native pill's displayed colours, including its transitions
        // and contrast at high star ratings, without a second difficulty calculation.
        for (int i = 0; i < accuracyLabels.Length; i++)
        {
            accuracyBackgrounds[i].Colour = difficulty.DisplayedDifficultyColour;
            accuracyLabels[i].Colour = difficulty.DisplayedDifficultyTextColour;
        }
    }

    private void changed()
    {
        if (disposed) return;
        generation++;
        cancellation?.Cancel();
        pending?.Cancel();
        pending = Scheduler.AddDelayed(() =>
        {
            Alpha = enabled.Value ? 1 : 0;
            if (!enabled.Value) return;
            foreach (var value in values) value.Text = "…";
            var beatmap = working.Value;
            var info = ruleset.Value;
            var selected = mods.Value.Select(mod => mod.DeepClone()).ToArray();
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            _ = calculate(beatmap, info, selected, generation, cancellation.Token);
        }, 200);
    }

    private async Task calculate(WorkingBeatmap beatmap, RulesetInfo info, Mod[] selected, int version, CancellationToken token)
    {
        try
        {
            var result = await Task.Run(() => Calculate(beatmap, info, selected, token), token).ConfigureAwait(false);
            if (disposed || token.IsCancellationRequested) return;
            Schedule(() =>
            {
                if (disposed || version != generation) return;
                caption.Text = info.ShortName == "fruits" ? "pp · оценка" : "pp за FC · оценка";
                for (int i = 0; i < values.Length; i++) values[i].Text = double.IsFinite(result[i]) ? $"{result[i]:0}" : "—";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Logger.Log($"[SOMS!] Map pp preview unavailable: {e.GetType().Name}", level: LogLevel.Debug);
            if (!disposed) Schedule(() => { if (version == generation) foreach (var value in values) value.Text = "—"; });
        }
    }

    public static double[] Calculate(WorkingBeatmap beatmap, RulesetInfo info, Mod[] selected, CancellationToken token)
    {
        if (selected.Any(mod => !mod.Ranked)) return new double[4];
        var instance = info.CreateInstance();
        var calculator = instance.CreatePerformanceCalculator();
        if (calculator == null) return Enumerable.Repeat(double.NaN, 4).ToArray();
        var playable = beatmap.GetPlayableBeatmap(info, selected, token);
        var difficulty = instance.CreateDifficultyCalculator(beatmap).Calculate(selected, token);
        using var processor = instance.CreateScoreProcessor();
        processor.Mods.Value = selected;
        processor.ApplyBeatmap(playable);
        var maximum = processor.MaximumStatistics;
        return new[] { .95, .98, .99, 1 }.Select(accuracy =>
        {
            token.ThrowIfCancellationRequested();
            var statistics = new Dictionary<HitResult, int>(maximum);
            // Full combo, all slider bonuses, with lower object judgements. Counts are
            // discrete; the displayed accuracy is the target rather than a fictional score.
            var source = info.ShortName == "mania" ? HitResult.Perfect : HitResult.Great;
            var target = info.ShortName == "mania" ? HitResult.Good : HitResult.Ok;
            if (info.ShortName == "fruits") { source = HitResult.SmallTickHit; target = HitResult.SmallTickMiss; }
            double total = maximum.Sum(pair => processor.GetBaseScoreForResult(pair.Key) * pair.Value);
            double difference = processor.GetBaseScoreForResult(source) - processor.GetBaseScoreForResult(target);
            int lower = difference > 0 ? Math.Clamp((int)Math.Round(total * (1 - accuracy) / difference), 0, statistics.GetValueOrDefault(source)) : 0;
            statistics[source] = statistics.GetValueOrDefault(source) - lower;
            statistics[target] = statistics.GetValueOrDefault(target) + lower;
            var score = new ScoreInfo { Ruleset = info, BeatmapInfo = beatmap.BeatmapInfo, Mods = selected,
                MaxCombo = processor.MaximumCombo, Accuracy = total > 0 ? statistics.Sum(pair => processor.GetBaseScoreForResult(pair.Key) * pair.Value) / total : 1,
                Statistics = statistics, MaximumStatistics = new Dictionary<HitResult, int>(maximum) };
            return calculator.Calculate(score, difficulty).Total;
        }).ToArray();
    }

    protected override void Dispose(bool isDisposing)
    {
        disposed = true;
        generation++;
        pending?.Cancel(); cancellation?.Cancel(); cancellation?.Dispose(); tracker?.Dispose();
        enabled?.UnbindAll(); working?.UnbindAll(); ruleset?.UnbindAll(); mods?.UnbindAll();
        base.Dispose(isDisposing);
    }
}
