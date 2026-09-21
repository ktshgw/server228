using System;
using System.IO;
using System.Linq;
using System.Text;
using osu.Game.Beatmaps;
using osu.Game.IO;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Server.Spectator.Services;
using SomsAi.Shared;
using Xunit;

namespace osu.Server.Spectator.Tests.Multiplayer;

public class SomsaiBotSkillTests
{
    [Fact]
    public void TechnicalChartDescriptorsMatchServerAndApplyAcrossMods()
    {
        var assembly = typeof(SomsaiBotSkillTests).Assembly;
        using var reader = new StreamReader(assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("bot-tech.osu")))!);
        var features = BotTechnicalFeatures.Parse(reader.ReadToEnd());
        Assert.Equal(1, features.Rhythm, 8);
        Assert.Equal(.7141577458747999, features.Angles, 8);
        Assert.Equal(.6692406440674795, features.SliderTech, 8);
        foreach (var mods in new[] { Array.Empty<string>(), new[] { "HD" }, new[] { "HR" }, new[] { "HD", "HR" }, new[] { "DT" } })
        {
            var plain = new BotMapSample { Mods = mods, Length = 180, Ability = 7, Rhythm = 0, Angles = 0, SliderTech = 0 };
            var tech = new BotMapSample { Mods = mods, Length = 180, Ability = 7, Rhythm = features.Rhythm, Angles = features.Angles, SliderTech = features.SliderTech };
            var specialist = new BotSkillProfile { Rank = 3500, Samples = Enumerable.Repeat(tech, 6).ToArray() };
            var farmer = new BotSkillProfile { Rank = 3500, Samples = Enumerable.Repeat(plain, 6).ToArray() };
            Assert.True(BotSkillModel.ComfortFor(specialist, tech, "hard") > BotSkillModel.ComfortFor(farmer, tech, "hard") + .3);
        }
    }

    [Fact]
    public void RankContinuityAndMapOverload()
    {
        Assert.True(BotSkillModel.RankSkill(1200, "hard") > BotSkillModel.RankSkill(8500, "hard") + 1);
        Assert.InRange(Math.Abs(BotSkillModel.RankSkill(9999, "hard") - BotSkillModel.RankSkill(10000, "medium")), 0, .001);
        double skill = BotSkillModel.RankSkill(8500, "hard");
        Assert.True(BotSkillModel.ErrorRates(9, skill).Miss > BotSkillModel.ErrorRates(4, skill).Miss * 100);
    }

    [Fact]
    public void AimDtAndStreamDtAreDifferentSkills()
    {
        var aim = new BotMapSample { Mods = new[] { "DT" }, AimRatio = .85, Stamina = .15, Length = 100, Ability = 7, Quality = 1 };
        var stream = new BotMapSample { Mods = new[] { "DT" }, AimRatio = .25, Stamina = .75, Length = 100, Ability = 7, Quality = 1 };
        var specialist = new BotSkillProfile { Rank = 3500, Samples = Enumerable.Repeat(aim, 6).ToArray() };
        Assert.True(BotSkillModel.ComfortFor(specialist, aim, "hard") > BotSkillModel.ComfortFor(specialist, stream, "hard") + .3);
        var both = new BotSkillProfile { Rank = 3500, Samples = new[] { aim, stream, aim, stream, aim, stream } };
        Assert.InRange(Math.Abs(BotSkillModel.ComfortFor(both, aim, "hard") - BotSkillModel.ComfortFor(both, stream, "hard")), 0, .01);
    }

    [Fact]
    public void NativeScoresFollowRankAndComfortRangeAndRewindExactly()
    {
        var ruleset = new OsuRuleset();
        string header = "osu file format v14\n[General]\nMode:0\n[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:8\nApproachRate:9\nSliderMultiplier:1.4\nSliderTickRate:1\n[TimingPoints]\n0,300,4,2,1,50,1,0\n[HitObjects]\n";
        string raw = header + string.Join("\n", Enumerable.Range(0, 400).Select(i => $"256,192,{1000 + i * 150},1,0,0:0:0:0:"));
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var reader = new LineBufferedReader(bytes);
        var source = osu.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        var map = new FlatWorkingBeatmap(source).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());
        (double Score, double Acc) average(int rank, double stars)
        {
            double score = 0, accuracy = 0;
            for (int seed = 0; seed < 60; seed++)
            {
                var sim = new SomsAiBotSimulation(ruleset, map, Array.Empty<Mod>(), SomsAiBotLevel.Hard, stars, seed, new BotSkillProfile { Rank = rank });
                sim.Advance(double.PositiveInfinity);
                score += sim.Processor.TotalScore.Value;
                accuracy += sim.Processor.Accuracy.Value;
                long before = sim.Processor.TotalScore.Value;
                sim.Advance(-10000);
                Assert.Equal(0, sim.Processor.TotalScore.Value);
                sim.Advance(double.PositiveInfinity);
                Assert.Equal(before, sim.Processor.TotalScore.Value);
                sim.Processor.Dispose();
            }
            return (score / 60, accuracy / 60);
        }
        var strong = average(1200, 7);
        var weak = average(8500, 7);
        var easy = average(8500, 4);
        var overload = average(8500, 9);
        Assert.True(strong.Score > weak.Score && strong.Acc > weak.Acc);
        Assert.True(easy.Score > overload.Score * 2);
        Assert.InRange(easy.Acc, .993, 1);
        Assert.True(overload.Acc < .9);
        var mrekk = new SomsAiBotSimulation(ruleset, map, Array.Empty<Mod>(), SomsAiBotLevel.Mrekk, 15, 21);
        mrekk.Advance(double.PositiveInfinity);
        Assert.Equal(mrekk.Processor.MaximumCombo, mrekk.Processor.HighestCombo.Value);
        mrekk.Processor.Dispose();
    }
}
