#nullable enable
using System.Runtime.CompilerServices;
using osu.Framework.Graphics.Sprites;
using osu.Framework.IO.Stores;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>Aller from the local stable resource pack, used only by explicitly opted-in UI text.</summary>
public static class SomsLegacyFont
{
    private static readonly ConditionalWeakTable<FontStore, object> registered = new();

    public static FontUsage Font(float size, bool bold = false) =>
        new("SomsAller", size, bold ? "Bold" : "Regular");

    internal static void Register(FontStore fonts)
    {
        lock (registered)
        {
            if (registered.TryGetValue(fonts, out _)) return;
            var resources = new ResourceStore<byte[]>(new DllResourceStore(typeof(SomsLegacyFont).Assembly));
            // BMFont v3 and atlases use the framework's native glyph path. They do not require an
            // installed system font, and cannot affect Torus or any native text's font selection.
            fonts.AddTextureSource(new TimedExpiryGlyphStore(resources, "Resources/StableFonts/SomsAller-Regular"));
            fonts.AddTextureSource(new TimedExpiryGlyphStore(resources, "Resources/StableFonts/SomsAller-Bold"));
            registered.Add(fonts, new object());
        }
    }
}
