#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Scoring;
using SomsAi.Shared;

#if SOMSAI_SPECTATOR
namespace osu.Server.Spectator.Services;
#else
namespace osu.Game.Rulesets.EnhancedAuth.UI;
#endif

public enum SomsAiBotLevel { Easy, Medium, Hard, Expert, Impossible, Mrekk }

/// <summary>Native scoring driven by rank, score history and actual modded map difficulty.</summary>
public sealed class SomsAiBotSimulation
{
    // Keep enum values for saved callers; expose only the merged top-1000 tier.
    public static readonly string[] Labels = { "Легко · 6 digit", "Средне · 5 digit", "Сложно · 4 digit", "Топ 1000 · 3–2 digit", "mrekk · всегда FC" };
    public static readonly string[] LevelKeys = { "easy", "medium", "hard", "top1000", "mrekk" };
    public static readonly SomsAiBotLevel[] SelectableLevels = { SomsAiBotLevel.Easy, SomsAiBotLevel.Medium, SomsAiBotLevel.Hard, SomsAiBotLevel.Expert, SomsAiBotLevel.Mrekk };
    private static readonly string[] names = { "Bubble Rookie", "Coral Swimmer", "Reef Hunter", "Deep Sea Ace", "Deep Sea Ace", "mrekk" };
    public static string NameFor(SomsAiBotLevel level) => names[(int)level];
    public ScoreProcessor Processor { get; }
    public double EffectiveSkill { get; }
    private readonly JudgementResult[] results;
    private int applied;
    public int ObjectCount => results.Length;
    public int AppliedCount => applied;

    public SomsAiBotSimulation(Ruleset ruleset, IBeatmap beatmap, IReadOnlyList<Mod> mods, SomsAiBotLevel level, double stars, int seed,
                               BotSkillProfile? profile = null, double? aimRatio = null, BotTechnicalFeatures? technical = null, double? sliderControl = null)
    {
        Processor = ruleset.CreateScoreProcessor();
        Processor.Mods.Value = mods;
        var autoplay = new List<JudgementResult>();
        Processor.NewJudgement += capture;
        Processor.ApplyBeatmap(beatmap);
        Processor.NewJudgement -= capture;
        foreach (var mod in mods.OfType<IApplicableToScoreProcessor>()) mod.ApplyToScoreProcessor(Processor);
        void capture(JudgementResult result) => autoplay.Add(result);

        double rate = mods.OfType<IApplicableToRate>().Aggregate(1d, (value, mod) => mod.ApplyToRate(0, value));
        double first = beatmap.HitObjects.First().StartTime;
        double end = beatmap.HitObjects.Max(hit => hit.GetEndTime());
        double length = Math.Max(1, (end - first - beatmap.TotalBreakTime) / 1000 / rate);
        double bpm = 60000 / Math.Max(1, beatmap.GetMostCommonBeatLength()) * rate;
        double nps = beatmap.HitObjects.Count / length;
        double ar = beatmap.Difficulty.ApproachRate;
        double preempt = (ar < 5 ? 1800 - 120 * ar : 1200 - 150 * (ar - 5)) / rate;
        ar = preempt > 1200 ? (1800 - preempt) / 120 : 5 + (1200 - preempt) / 150;
        double speedRatio = aimRatio.HasValue ? 1 - aimRatio.Value : Math.Min(.8, nps / Math.Max(1, bpm / 60) / 6);
        var target = new BotMapSample
        {
            Stars = stars, Bpm = bpm, Nps = nps, Ar = ar, Cs = beatmap.Difficulty.CircleSize, Length = length,
            AimRatio = 1 - speedRatio, Stamina = speedRatio * Math.Sqrt(length / 120),
            Rhythm = technical?.Rhythm, Angles = technical?.Angles, SliderTech = technical?.SliderTech, SliderControl = sliderControl,
            SliderRatio = (double)beatmap.HitObjects.Count(hit => hit.GetType().Name == "Slider") / beatmap.HitObjects.Count,
            Mods = mods.Select(m => m.Acronym == "NC" ? "DT" : m.Acronym == "DC" ? "HT" : m.Acronym)
                .Where(m => m is not ("NF" or "CL" or "SD" or "PF" or "MR" or "SO")).Distinct().OrderBy(m => m).ToArray(),
        };
        EffectiveSkill = BotSkillModel.ComfortFor(profile, target, level.ToString());
        double usualLength = profile?.Samples is { Length: > 0 } samples ? samples.Average(s => s.Length) : 180;
        var random = new Random(seed);
        bool fc = level == SomsAiBotLevel.Mrekk;
        double form = .96 + random.NextDouble() * .08;
        double mrekkImperfect = .004 + random.NextDouble() * .022;
        double[] starts = beatmap.HitObjects.Select(hit => hit.StartTime).OrderBy(t => t).ToArray();
        int left = 0, right = 0;
        results = autoplay.OrderBy(result => result.HitObject.GetEndTime()).ToArray();
        foreach (var result in results)
        {
            var maximum = result.Type;
            if (maximum is HitResult.None or HitResult.IgnoreHit) continue;
            double time = result.HitObject.GetEndTime();
            while (left < starts.Length && starts[left] < time - 600) left++;
            while (right < starts.Length && starts[right] <= time + 600) right++;
            double density = (right - left) / 1.2 * rate / Math.Max(.1, nps);
            double localStrain = Math.Clamp(Math.Log2(Math.Max(.25, density)) * .28, -.5, .8)
                * Math.Clamp((stars - EffectiveSkill + 2) / 2, 0, 1);
            double fatigue = Math.Min(.35, Math.Max(0, length / Math.Max(30, usualLength) - 1) * .15 * target.Stamina)
                * Math.Clamp((time - first) / Math.Max(1, end - first), 0, 1);
            var chances = BotSkillModel.ErrorRates(stars + localStrain + fatigue, EffectiveSkill);
            double miss = fc ? 0 : chances.Miss * form;
            double imperfect = fc ? mrekkImperfect : chances.Imperfect * form;
            double roll = random.NextDouble();
            if (roll < miss) result.Type = result.Judgement.MinResult;
            else if (maximum is >= HitResult.Meh and <= HitResult.Perfect && roll < miss + imperfect)
            {
                var allowed = new[] { HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh }
                    .Where(hit => hit < maximum && result.HitObject.HitWindows?.IsHitResultAllowed(hit) == true).ToArray();
                if (allowed.Length > 0)
                    result.Type = allowed[!fc && stars > EffectiveSkill + 1 && random.NextDouble() < .35 ? allowed.Length - 1 : 0];
            }
        }
    }

    public void Advance(double time)
    {
        while (applied > 0 && results[applied - 1].HitObject.GetEndTime() > time)
            Processor.RevertResult(results[--applied]);
        while (applied < results.Length && results[applied].HitObject.GetEndTime() <= time)
            Processor.ApplyResult(results[applied++]);
    }
}
