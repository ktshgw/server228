#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Rulesets.Mods;
using osuTK;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

// Convenience inputs only: no additional mod settings are serialised or sent to the server.
public static class SomsRateConversion
{
    public static double Preempt(double ar) => ar <= 5 ? 1800 - 120 * ar : 1950 - 150 * ar;
    public static double ApproachRate(double ar, double rate)
    {
        double preempt = Preempt(ar) / rate;
        return preempt >= 1200 ? (1800 - preempt) / 120 : (1950 - preempt) / 150;
    }

    public static double ForBpm(double bpm, double target) => target / bpm;
    public static double ForApproachRate(double ar, double target) => Preempt(ar) / Preempt(target);
    public static double Clamp(double rate, double minimum, double maximum) => Math.Clamp(rate, minimum, maximum);
}

public partial class SomsRateFields : FillFlowContainer
{
    private readonly ModDoubleTime mod;
    private readonly FormTextBox bpm = new() { Caption = "BPM карты", SelectAllOnFocus = true, LengthLimit = 16 };
    private readonly FormTextBox ar = new() { Caption = "AR карты", SelectAllOnFocus = true, LengthLimit = 16 };
    private IBindable<WorkingBeatmap> working = null!;
    private IBindable<IReadOnlyList<Mod>> selected = null!;
    private IBindable<RulesetInfo> ruleset = null!;
    private BindableNumber<double> speed = null!;
    private ModSettingChangeTracker? tracker;
    private double baseBpm;
    private double baseAr;

    public SomsRateFields(ModDoubleTime mod)
    {
        this.mod = mod;
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
        Direction = FillDirection.Vertical;
        Spacing = new Vector2(0, 8);
        Children = new Drawable[] { bpm, ar };
    }

    [BackgroundDependencyLoader]
    private void load(IBindable<WorkingBeatmap> beatmap, IBindable<IReadOnlyList<Mod>> mods, IBindable<RulesetInfo> currentRuleset)
    {
        working = beatmap.GetBoundCopy();
        selected = mods.GetBoundCopy();
        ruleset = currentRuleset.GetBoundCopy();
        speed = mod.SpeedChange.GetBoundCopy();
        speed.BindValueChanged(_ => refreshValues());
        speed.BindDisabledChanged(_ => refreshValues());
        working.BindValueChanged(_ => refreshMap());
        ruleset.BindValueChanged(_ => refreshMap());
        selected.BindValueChanged(_ =>
        {
            tracker?.Dispose();
            tracker = new ModSettingChangeTracker(selected.Value) { SettingChanged = _ => Scheduler.AddOnce(refreshMap) };
            refreshMap();
        }, true);
        bpm.OnCommit += (_, _) => commit(bpm, false);
        ar.OnCommit += (_, _) => commit(ar, true);
    }

    private void refreshMap()
    {
        baseBpm = working.Value.BeatmapInfo.BPM;
        var difficulty = new BeatmapDifficulty(working.Value.BeatmapInfo.Difficulty);
        foreach (var adjustment in selected.Value.OfType<IApplicableToDifficulty>())
            adjustment.ApplyToDifficulty(difficulty);
        baseAr = difficulty.ApproachRate;
        // AR has no gameplay meaning in taiko/mania.
        ar.Alpha = ruleset.Value.OnlineID is 0 or 2 ? 1 : 0;
        refreshValues();
    }

    private void refreshValues()
    {
        if (IsDisposed) return;
        bool available = baseBpm > 0 && double.IsFinite(baseBpm);
        bpm.ReadOnly = !available || speed.Disabled;
        ar.ReadOnly = !available || speed.Disabled || SomsRateConversion.Preempt(baseAr) <= 0;
        bpm.Current.Value = available ? (baseBpm * speed.Value).ToString("0.##", CultureInfo.InvariantCulture) : "—";
        ar.Current.Value = available ? SomsRateConversion.ApproachRate(baseAr, speed.Value).ToString("0.##", CultureInfo.InvariantCulture) : "—";
    }

    private void commit(FormTextBox field, bool approachRate)
    {
        if (field.ReadOnly || speed.Disabled || !double.TryParse(field.Current.Value.Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double target) || !double.IsFinite(target))
        {
            refreshValues();
            return;
        }
        double minimum = approachRate ? SomsRateConversion.ApproachRate(baseAr, speed.MinValue) : baseBpm * speed.MinValue;
        double maximum = approachRate ? SomsRateConversion.ApproachRate(baseAr, speed.MaxValue) : baseBpm * speed.MaxValue;
        target = Math.Clamp(target, minimum, maximum);
        double rate = approachRate ? SomsRateConversion.ForApproachRate(baseAr, target) : SomsRateConversion.ForBpm(baseBpm, target);
        speed.Value = SomsRateConversion.Clamp(rate, speed.MinValue, speed.MaxValue);
        // Show the actual result after the native slider's 0.01x quantisation.
        refreshValues();
    }

    protected override void Dispose(bool isDisposing)
    {
        tracker?.Dispose();
        working?.UnbindAll();
        selected?.UnbindAll();
        ruleset?.UnbindAll();
        speed?.UnbindAll();
        base.Dispose(isDisposing);
    }
}
