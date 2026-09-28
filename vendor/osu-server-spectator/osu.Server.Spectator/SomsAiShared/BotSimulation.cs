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

/// <summary>Native score simulation calibrated to a match-scoped SOMSAI profile.</summary>
public sealed class SomsAiBotSimulation
{
    public static readonly string[] Labels = { "Easy · 6 digit", "Medium · 5 digit", "Hard · 4 digit", "Top 1000 · 3–2 digit", "mrekk · always FC" };
    public static readonly string[] LevelKeys = { "easy", "medium", "hard", "top1000", "mrekk" };
    public static readonly SomsAiBotLevel[] SelectableLevels = { SomsAiBotLevel.Easy, SomsAiBotLevel.Medium, SomsAiBotLevel.Hard, SomsAiBotLevel.Expert, SomsAiBotLevel.Mrekk };
    private static readonly string[] names = { "Bubble Rookie", "Coral Swimmer", "Reef Hunter", "Deep Sea Ace", "Deep Sea Ace", "mrekk" };
    public static string NameFor(SomsAiBotLevel level) => names[(int)level];

    public ScoreProcessor Processor { get; }
    public double EffectiveSkill { get; }
    public BotAttemptPlan Attempt { get; }
    private readonly JudgementResult[] results;
    private int applied;
    public int ObjectCount => results.Length;
    public int AppliedCount => applied;

    public SomsAiBotSimulation(Ruleset ruleset, IBeatmap beatmap, IReadOnlyList<Mod> mods, SomsAiBotLevel level, double stars, int seed,
                               BotSkillProfile? profile = null, double? aimRatio = null, BotTechnicalFeatures? technical = null,
                               double? sliderControl = null, string? mapSlot = null)
    {
        Processor = ruleset.CreateScoreProcessor();
        Processor.Mods.Value = mods;
        var autoplay = new List<JudgementResult>();
        Processor.NewJudgement += capture;
        Processor.ApplyBeatmap(beatmap);
        Processor.NewJudgement -= capture;
        foreach (var mod in mods.OfType<IApplicableToScoreProcessor>()) mod.ApplyToScoreProcessor(Processor);
        void capture(JudgementResult result) => autoplay.Add(result);

        profile ??= BotSkillModel.Fallback(level.ToString());
        Attempt = BotSkillModel.Plan(profile, mapSlot, seed);
        EffectiveSkill = Attempt.Performance;
        results = autoplay.OrderBy(result => result.HitObject.GetEndTime()).ToArray();
        var maximum = results.Select(result => result.Type).ToArray();

        // Search accuracy plans through the native processor and retain the
        // score closest to the division target. This keeps slider/combo rules
        // identical to a real client score instead of estimating score maths.
        HitResult[]? best = null;
        long bestDistance = long.MaxValue;
        double floor = Math.Clamp(profile.MinimumAccuracy / 100, .5, 1);
        double centre = Math.Clamp(Attempt.TargetAccuracy, floor, 1);
        var qualities = Enumerable.Range(0, 33).Select(i => floor + (1 - floor) * i / 32d)
            .Append(centre).Distinct().OrderBy(value => value).ToArray();
        for (int trial = 0; trial < qualities.Length; trial++)
        {
            for (int i = 0; i < results.Length; i++) results[i].Type = maximum[i];
            makePlan(qualities[trial], seed ^ (trial * 7919), Attempt.Performance);
            foreach (var result in results) Processor.ApplyResult(result);
            long score = Processor.TotalScore.Value;
            long distance = Math.Abs(score - Attempt.TargetScore);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = results.Select(result => result.Type).ToArray();
            }
            for (int i = results.Length - 1; i >= 0; i--) Processor.RevertResult(results[i]);
        }
        if (best != null)
            for (int i = 0; i < results.Length; i++) results[i].Type = best[i];

        void makePlan(double accuracy, int planSeed, double performance)
        {
            var eligible = Enumerable.Range(0, results.Length)
                .Where(i => maximum[i] is >= HitResult.Great and <= HitResult.Perfect
                            && results[i].HitObject.HitWindows?.IsHitResultAllowed(HitResult.Ok) == true)
                .ToList();
            if (eligible.Count == 0) return;
            var random = new Random(planSeed);
            shuffle(eligible, random);
            int count = eligible.Count;
            double error = Math.Max(0, 1 - accuracy);
            bool fc = accuracy >= .99 || (accuracy >= .975 && random.NextDouble() < Math.Clamp((performance - .55) * 1.5, 0, .85));
            int misses = fc ? 0 : (int)Math.Round(count * .025 * error / .075);
            if (!fc && accuracy < .985 && misses == 0) misses = 1;
            int mehs = accuracy >= .99 ? 0 : (int)Math.Round(count * .015 * error / .075);
            double loss = count * 3 * error;
            int oks = (int)Math.Round(Math.Max(0, loss - misses * 3 - mehs * 2.5) / 2);
            while (misses + mehs + oks > count && oks > 0) oks--;
            while (misses + mehs + oks > count && mehs > 0) mehs--;
            int cursor = 0;
            for (; cursor < misses && cursor < count; cursor++)
                results[eligible[cursor]].Type = results[eligible[cursor]].Judgement.MinResult;
            for (int end = Math.Min(count, cursor + mehs); cursor < end; cursor++)
                if (results[eligible[cursor]].HitObject.HitWindows?.IsHitResultAllowed(HitResult.Meh) == true)
                    results[eligible[cursor]].Type = HitResult.Meh;
            for (int end = Math.Min(count, cursor + oks); cursor < end; cursor++)
                results[eligible[cursor]].Type = HitResult.Ok;
        }
    }

    private static void shuffle<T>(IList<T> list, Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
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
