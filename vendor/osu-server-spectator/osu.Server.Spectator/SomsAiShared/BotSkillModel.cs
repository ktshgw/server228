#nullable enable
using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace SomsAi.Shared;

public sealed class BotSkillProfile
{
    [JsonPropertyName("rank")] public int? Rank { get; set; }
    [JsonPropertyName("comfort")] public double Comfort { get; set; }
    [JsonPropertyName("samples")] public BotMapSample[] Samples { get; set; } = Array.Empty<BotMapSample>();
}

public sealed class BotMapSample
{
    [JsonPropertyName("stars")] public double Stars { get; set; }
    [JsonPropertyName("mods")] public string[] Mods { get; set; } = Array.Empty<string>();
    [JsonPropertyName("bpm")] public double Bpm { get; set; }
    [JsonPropertyName("nps")] public double Nps { get; set; }
    [JsonPropertyName("ar")] public double Ar { get; set; }
    [JsonPropertyName("cs")] public double Cs { get; set; }
    [JsonPropertyName("length")] public double Length { get; set; }
    [JsonPropertyName("slider_ratio")] public double SliderRatio { get; set; }
    [JsonPropertyName("ability")] public double Ability { get; set; }
    [JsonPropertyName("quality")] public double Quality { get; set; } = 1;
    [JsonPropertyName("aim_ratio")] public double AimRatio { get; set; }
    [JsonPropertyName("stamina")] public double Stamina { get; set; }
    [JsonPropertyName("rhythm")] public double? Rhythm { get; set; }
    [JsonPropertyName("angles")] public double? Angles { get; set; }
    [JsonPropertyName("slider_tech")] public double? SliderTech { get; set; }
    [JsonPropertyName("slider_control")] public double? SliderControl { get; set; }
}

public static class BotSkillModel
{
    // Keep this small numerical model in sync with somsai_bot_skill.py's draft
    // prediction. Actual gameplay always supplies native, mod-adjusted stars.
    public static double RankSkill(int? rank, string level)
    {
        int value = rank is > 0 ? rank.Value : level.ToLowerInvariant() switch
        {
            "easy" => 350000, "hard" => 3500,
            "expert" or "impossible" or "top1000" => 350,
            "mrekk" => 1, _ => 35000,
        };
        return Math.Clamp(11.6 - 1.4 * Math.Log10(value), 2.5, 11);
    }

    public static double ComfortFor(BotSkillProfile? profile, BotMapSample target, string level)
    {
        double baseline = profile?.Comfort is > 0 and < 20 ? profile.Comfort : RankSkill(profile?.Rank, level);
        if (profile?.Samples is not { Length: > 0 } samples) return baseline;
        var nearest = samples.Select(sample => (Sample: sample, Distance: distance(sample, target)))
            .OrderBy(item => item.Distance).Take(6)
            .Select(item => (item.Sample, item.Distance, Weight: Math.Exp(-item.Distance) * item.Sample.Quality)).ToArray();
        double total = nearest.Sum(item => item.Weight);
        double observed = nearest.Sum(item => item.Sample.Ability * item.Weight) / Math.Max(1e-9, total);
        double reference = samples.Select(s => s.Ability).OrderBy(v => v).ElementAt(samples.Length / 2);
        double adjustment = Math.Clamp(observed - reference, -.65, .65) * Math.Min(1, total / 2);
        return baseline + adjustment + Math.Min(.25, total / 12) - Math.Min(.3, nearest[0].Distance * .07);
    }

    private static double distance(BotMapSample sample, BotMapSample target)
    {
        double result = sample.Mods.Except(target.Mods).Count() * .9 + target.Mods.Except(sample.Mods).Count() * .9;
        result += Math.Min(2, Math.Abs(sample.Bpm - target.Bpm) / 90);
        result += Math.Min(2, Math.Abs(sample.Nps - target.Nps) / 5);
        result += Math.Min(2, Math.Abs(sample.Ar - target.Ar) / 3);
        result += Math.Min(2, Math.Abs(sample.Cs - target.Cs) / 3);
        result += Math.Min(2, Math.Abs(sample.SliderRatio - target.SliderRatio) / .6);
        result += Math.Min(2, Math.Abs(sample.AimRatio - target.AimRatio) / .15);
        result += Math.Min(2, Math.Abs(sample.Stamina - target.Stamina) / .5);
        if (sample.Rhythm.HasValue && target.Rhythm.HasValue) result += Math.Min(2, Math.Abs(sample.Rhythm.Value - target.Rhythm.Value) / .2);
        if (sample.Angles.HasValue && target.Angles.HasValue) result += Math.Min(2, Math.Abs(sample.Angles.Value - target.Angles.Value) / .25);
        if (sample.SliderTech.HasValue && target.SliderTech.HasValue) result += Math.Min(2, Math.Abs(sample.SliderTech.Value - target.SliderTech.Value) / .2);
        if (sample.SliderControl.HasValue && target.SliderControl.HasValue) result += Math.Min(2, Math.Abs(sample.SliderControl.Value - target.SliderControl.Value) / .2);
        return result + Math.Abs(Math.Log(Math.Max(1, sample.Length) / Math.Max(1, target.Length))) * .5;
    }

    public static (double Miss, double Imperfect) ErrorRates(double demand, double comfort)
    {
        double overload = Math.Clamp(demand - comfort, -6, 8);
        return (Math.Clamp(.00001 + .001 * Math.Exp(1.5 * overload), .00001, .38),
            Math.Clamp(.002 + .024 * Math.Exp(1.25 * overload), .002, .80));
    }
}
