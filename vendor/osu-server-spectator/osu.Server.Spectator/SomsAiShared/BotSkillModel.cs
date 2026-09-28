#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SomsAi.Shared;

/// <summary>A freshly rolled, match-scoped competitive identity.</summary>
public sealed class BotSkillProfile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("rating")] public int Rating { get; set; } = 1500;
    [JsonPropertyName("division")] public string Division { get; set; } = "GOLD I";
    [JsonPropertyName("minimum_score")] public int MinimumScore { get; set; } = 700_000;
    [JsonPropertyName("minimum_accuracy")] public double MinimumAccuracy { get; set; } = 91;
    [JsonPropertyName("archetype")] public string Archetype { get; set; } = "allrounder";
    [JsonPropertyName("weakness")] public string? Weakness { get; set; }
    [JsonPropertyName("match_form")] public double MatchForm { get; set; } = .80;
    [JsonPropertyName("consistency")] public double Consistency { get; set; } = .04;
    [JsonPropertyName("freemod_preference")] public string? FreemodPreference { get; set; }
    [JsonPropertyName("tiebreaker_preference")] public string? TiebreakerPreference { get; set; }
    [JsonPropertyName("slot_modifiers")] public Dictionary<string, double> SlotModifiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public readonly record struct BotAttemptPlan(double Performance, long TargetScore, double TargetAccuracy);

public static class BotSkillModel
{
    public static BotSkillProfile Fallback(string level) => level.ToLowerInvariant() switch
    {
        "easy" => profile(700_000, 81, .76, "BRONZE I", 500),
        "medium" => profile(700_000, 87.5, .80, "SILVER I", 1050),
        "hard" => profile(700_000, 91, .82, "GOLD I", 1550),
        "expert" or "impossible" or "top1000" => profile(800_000, 94.5, .84, "DIAMOND V", 2950),
        "mrekk" => profile(800_000, 99.7, 1, "ARCHSOM", 3600),
        _ => profile(700_000, 91, .80, "GOLD I", 1550),
    };

    public static BotAttemptPlan Plan(BotSkillProfile profile, string? slot, int seed)
    {
        var random = new Random(seed);
        double modifier = slot != null && profile.SlotModifiers.TryGetValue(slot, out double value) ? value : 0;
        double spread = Math.Clamp(profile.Consistency, 0, .12);
        // Average two rolls to avoid a uniform distribution with too many extreme games.
        double variance = ((random.NextDouble() + random.NextDouble()) / 2 - .5) * 2 * spread;
        double performance = Math.Clamp(profile.MatchForm + modifier + variance, 0, 1);
        long score = (long)Math.Round(profile.MinimumScore + performance * (1_000_000 - profile.MinimumScore));
        double floor = Math.Clamp(profile.MinimumAccuracy / 100, .50, 1);
        double accuracy = floor + Math.Pow(performance, .9) * (1 - floor);
        return new(performance, Math.Clamp(score, profile.MinimumScore, 1_000_000), Math.Clamp(accuracy, floor, 1));
    }

    private static BotSkillProfile profile(int score, double accuracy, double form, string division, int rating) => new()
    {
        MinimumScore = score, MinimumAccuracy = accuracy, MatchForm = form,
        Division = division, Rating = rating, Archetype = "allrounder",
    };
}
