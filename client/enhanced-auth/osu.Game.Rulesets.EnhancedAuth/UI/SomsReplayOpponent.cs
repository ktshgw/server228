#nullable enable
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Graphics.Containers;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;
using osu.Game.Rulesets.UI.Scrolling;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Skinning;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>A real replay playfield driven by the human player's map clock.</summary>
public sealed partial class SomsReplayOpponent : CompositeDrawable, ISamplePlaybackDisabler
{
    private readonly WorkingBeatmap working;
    private readonly Score replay;
    private readonly IGameplayClock master;
    private DependencyContainer dependencies = null!;
    private DrawableRuleset drawable = null!;
    public GameplayState State { get; private set; } = null!;
    public ScoreProcessor Processor => State.ScoreProcessor;
    public IBindable<bool> SamplePlaybackDisabled { get; } = new BindableBool(true);
    public override bool HandlePositionalInput => false;
    public override bool HandleNonPositionalInput => false;
    // Handle*Input affects this drawable only. Block the whole simulation subtree
    // from mouse/keyboard routing so it cannot take hover or the user's cursor.
    public override bool PropagatePositionalInputSubTree => false;
    public override bool PropagateNonPositionalInputSubTree => false;

    public SomsReplayOpponent(WorkingBeatmap working, Score replay, IGameplayClock master)
    {
        this.working = working;
        this.replay = replay;
        this.master = master;
        RelativeSizeAxes = Axes.Both;
        Name = "soms-replay-duel-simulation";
        Alpha = 0;
        AlwaysPresent = true; // keep judging replay frames without rendering a second playfield.
    }

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        => dependencies = new DependencyContainer(base.CreateChildDependencies(parent));

    [BackgroundDependencyLoader]
    private void load(OsuConfigManager config)
    {
        var ruleset = replay.ScoreInfo.Ruleset.CreateInstance();
        var mods = replay.ScoreInfo.Mods.Select(m => m.DeepClone()).ToArray();
        var playable = working.GetPlayableBeatmap(ruleset.RulesetInfo, mods);
        var processor = ruleset.CreateScoreProcessor();
        processor.Mods.Value = mods;
        processor.ApplyBeatmap(playable);
        var health = ruleset.CreateHealthProcessor(playable.HitObjects[0].StartTime);
        health.ApplyBeatmap(playable);
        State = new GameplayState(playable, ruleset, mods, replay, processor, health, working.Storyboard);
        drawable = ruleset.CreateDrawableRulesetWith(playable, mods);
        dependencies.CacheAs(State);
        dependencies.CacheAs(processor);
        dependencies.CacheAs(health);
        dependencies.CacheAs(drawable);
        dependencies.CacheAs<IGameplayClock>(master);
        if (drawable is IDrawableScrollingRuleset scrolling) dependencies.CacheAs(scrolling.ScrollingInfo);
        drawable.FrameStableComponents.Children = new Drawable[] { processor, health };
        drawable.NewResult += result => { processor.ApplyResult(result); health.ApplyResult(result); State.ApplyResult(result); };
        drawable.RevertResult += result => { processor.RevertResult(result); health.RevertResult(result); };
        processor.OnLoadComplete += _ =>
        {
            foreach (var mod in mods.OfType<IApplicableToScoreProcessor>()) mod.ApplyToScoreProcessor(processor);
        };
        health.OnLoadComplete += _ =>
        {
            foreach (var mod in mods.OfType<IApplicableToHealthProcessor>()) mod.ApplyToHealthProcessor(health);
        };
        var skin = new RulesetSkinProvidingContainer(ruleset, playable, working.Skin)
        {
            RelativeSizeAxes = Axes.Both,
            Child = new ScalingContainer(ScalingMode.Gameplay) { Child = drawable },
        };
        config.BindWith(OsuSetting.BeatmapSkins, skin.BeatmapSkins);
        config.BindWith(OsuSetting.BeatmapColours, skin.BeatmapColours);
        config.BindWith(OsuSetting.BeatmapHitsounds, skin.BeatmapHitsounds);
        InternalChild = skin;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        drawable.SetReplayScore(replay);
        ((IBindable<bool>)drawable.IsPaused).BindTo(master.IsPaused);
    }
}
