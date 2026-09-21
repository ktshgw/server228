#nullable enable
using System;
using System.Text.RegularExpressions;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

internal readonly record struct SomsAiRank(string Name, string Asset, Color4 Colour)
{
    public static SomsAiRank FromRating(double rating)
    {
        if (rating >= 3000) return new("ARCHSOM", "archsom", new Color4(255, 98, 103, 255));
        int step = Math.Clamp((int)Math.Floor(rating / 100) - 5, 0, 24);
        int tier = step / 5;
        string[] tiers = { "BRONZE", "SILVER", "GOLD", "PLATINUM", "DIAMOND" };
        string[] assets = { "bronze", "silver", "gold", "plat", "diamond" };
        string[] numerals = { "I", "II", "III", "IV", "V" };
        Color4[] colours = { new(220, 158, 109, 255), new(204, 218, 235, 255), new(255, 213, 103, 255),
            new(126, 239, 214, 255), new(139, 212, 255, 255) };
        return new($"{tiers[tier]} {numerals[step % 5]}", assets[tier] + (step % 5 + 1), colours[tier]);
    }

    private static readonly Regex botSuffix = new(@"\s*[\[(]bot\s+[a-zA-Z0-9_-]+[\])]\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static string DisplayName(string name) => name.IndexOf("bot ", StringComparison.OrdinalIgnoreCase) < 0 ? name : botSuffix.Replace(name, " [BOT]");
}

internal sealed partial class SomsAiRankPlaque : Sprite
{
    private readonly SomsAiRank rank;
    public SomsAiRankPlaque(double rating)
    {
        rank = SomsAiRank.FromRating(rating);
        Name = "somsai-rank-" + rank.Asset;
        RelativeSizeAxes = Axes.X;
        Height = 140;
        FillMode = FillMode.Fit;
    }
    [BackgroundDependencyLoader]
    private void load(TextureStore textures) => Texture = SomsAiOceanTheme.GetTexture(textures, "Ranks/" + rank.Asset);
}
