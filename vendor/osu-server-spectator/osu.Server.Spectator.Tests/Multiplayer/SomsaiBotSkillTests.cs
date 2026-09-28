using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using osu.Game.Beatmaps;
using osu.Game.IO;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Spectator.Hubs.Multiplayer.Standard;
using osu.Server.Spectator.Services;
using SomsAi.Shared;
using Xunit;

namespace osu.Server.Spectator.Tests.Multiplayer;

public class SomsaiBotSkillTests
{
    [Fact]
    public void AttemptUsesFormSlotModifierAndDivisionBounds()
    {
        var profile = new BotSkillProfile
        {
            MinimumScore = 750_000, MinimumAccuracy = 93.5, MatchForm = .80, Consistency = 0,
            SlotModifiers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["DT1"] = .18, ["DT2"] = -.18 },
        };
        var aim = BotSkillModel.Plan(profile, "dt1", 1);
        var tapping = BotSkillModel.Plan(profile, "DT2", 1);
        Assert.True(aim.Performance > tapping.Performance + .3);
        Assert.InRange(aim.TargetScore, 750_000, 1_000_000);
        Assert.InRange(tapping.TargetAccuracy, .935, 1);
    }

    [Fact]
    public void MatchFormStaysStableWhileMapVarianceChanges()
    {
        var profile = new BotSkillProfile { MinimumScore = 700_000, MinimumAccuracy = 90, MatchForm = .8, Consistency = .04 };
        var attempts = Enumerable.Range(0, 100).Select(seed => BotSkillModel.Plan(profile, "NM1", seed)).ToArray();
        Assert.All(attempts, attempt => Assert.InRange(attempt.Performance, .76, .84));
        Assert.True(attempts.Select(a => a.Performance).Distinct().Count() > 90);
    }

    [Fact]
    public void FreemodRequirementsMatchSoloAndTeamRules()
    {
        Assert.False(SomsaiMatchController.MeetsFreemodRequirement(new[] { Array.Empty<string>() }));
        Assert.False(SomsaiMatchController.MeetsFreemodRequirement(new[] { new[] { "EZ" } }));
        Assert.True(SomsaiMatchController.MeetsFreemodRequirement(new[] { new[] { "HD" } }));
        Assert.True(SomsaiMatchController.MeetsFreemodRequirement(new[] { new[] { "HR" } }));
        Assert.True(SomsaiMatchController.MeetsFreemodRequirement(new[] { new[] { "EZ", "HD" }, new[] { "HD", "HR" } }));
        Assert.False(SomsaiMatchController.MeetsFreemodRequirement(new[] { new[] { "HD", "HR" }, new[] { "HR" } }));
        Assert.False(SomsaiMatchController.MeetsFreemodRequirement(new[] { new[] { "HD" }, Array.Empty<string>() }));
    }

    [Fact]
    public void NativeScoreTracksPlanProducesRealisticJudgementsAndRewinds()
    {
        var (ruleset, map) = createMap(1000);
        var profile = new BotSkillProfile
        {
            Division = "DIAMOND III", MinimumScore = 750_000, MinimumAccuracy = 93.5,
            MatchForm = .78, Consistency = 0, Archetype = "tapping",
            SlotModifiers = new Dictionary<string, double> { ["NM5"] = .1 },
        };
        var sim = new SomsAiBotSimulation(ruleset, map, Array.Empty<Mod>(), SomsAiBotLevel.Hard, 7, 123, profile, mapSlot: "NM5");
        sim.Advance(double.PositiveInfinity);
        long score = sim.Processor.TotalScore.Value;
        Assert.InRange(Math.Abs(score - sim.Attempt.TargetScore), 0, 70_000);
        Assert.InRange(sim.Processor.Accuracy.Value, profile.MinimumAccuracy / 100, 1);
        var info = new ScoreInfo();
        sim.Processor.PopulateScore(info);
        int miss = info.Statistics.GetValueOrDefault(HitResult.Miss);
        int meh = info.Statistics.GetValueOrDefault(HitResult.Meh);
        int ok = info.Statistics.GetValueOrDefault(HitResult.Ok);
        Assert.InRange(miss, 0, 60);
        Assert.InRange(meh, 0, 40);
        Assert.InRange(ok, 0, 140);
        Assert.True(ok >= meh);
        long before = sim.Processor.TotalScore.Value;
        sim.Advance(-10_000);
        Assert.Equal(0, sim.Processor.TotalScore.Value);
        sim.Advance(double.PositiveInfinity);
        Assert.Equal(before, sim.Processor.TotalScore.Value);
        sim.Processor.Dispose();
    }

    [Fact]
    public void NinetyNinePlusPlansNeverCreateMehsOrMisses()
    {
        var (ruleset, map) = createMap(500);
        var profile = new BotSkillProfile { MinimumScore = 800_000, MinimumAccuracy = 99.2, MatchForm = 1, Consistency = 0 };
        var sim = new SomsAiBotSimulation(ruleset, map, Array.Empty<Mod>(), SomsAiBotLevel.Expert, 8, 7, profile, mapSlot: "NM1");
        sim.Advance(double.PositiveInfinity);
        var info = new ScoreInfo();
        sim.Processor.PopulateScore(info);
        Assert.Equal(0, info.Statistics.GetValueOrDefault(HitResult.Miss));
        Assert.Equal(0, info.Statistics.GetValueOrDefault(HitResult.Meh));
        Assert.Equal(sim.Processor.MaximumCombo, sim.Processor.HighestCombo.Value);
        sim.Processor.Dispose();
    }

    private static (OsuRuleset Ruleset, IBeatmap Map) createMap(int count)
    {
        var ruleset = new OsuRuleset();
        string header = "osu file format v14\n[General]\nMode:0\n[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n[TimingPoints]\n0,300,4,2,1,50,1,0\n[HitObjects]\n";
        string raw = header + string.Join("\n", Enumerable.Range(0, count).Select(i => $"256,192,{1000 + i * 150},1,0,0:0:0:0:"));
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var reader = new LineBufferedReader(bytes);
        var source = osu.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        return (ruleset, new FlatWorkingBeatmap(source).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>()));
    }
}
