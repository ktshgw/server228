using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Game.Rulesets.EnhancedAuth.UI;

internal static class FontChecks
{
    public static void Run()
    {
        using var fonts = new FontStore(new DummyRenderer(), minFilterMode: TextureFilteringMode.Linear);
        var register = typeof(SomsLegacyFont).GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (int i = 0; i < 30; i++) register.Invoke(null, new object[] { fonts });
        var stores = ((IEnumerable)typeof(FontStore).GetField("glyphStores", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fonts)!).Cast<IGlyphStore>().ToArray();
        require(stores.Length == 2, "Repeated font registration duplicated glyph stores");
        Task.WhenAll(stores.Select(store => store.LoadFontAsync())).GetAwaiter().GetResult();

        using var resources = new DllResourceStore(typeof(SomsLegacyFont).Assembly);
        using var stream = resources.GetStream("Resources/StableFonts/manifest.json")!;
        using var manifest = JsonDocument.Parse(stream);
        foreach (var face in manifest.RootElement.GetProperty("fonts").EnumerateArray())
        {
            string name = face.GetProperty("name").GetString()!;
            var sourceGlyphs = face.GetProperty("source_glyphs").EnumerateArray().Select(value => value.GetInt32()).ToHashSet();
            var fallbackGlyphs = face.GetProperty("fallback_glyphs").EnumerateArray().Select(value => value.GetInt32()).ToHashSet();
            require("ABCabc123".All(character => sourceGlyphs.Contains(character)), "Latin glyphs did not come from real Aller");
            require("АБВабвЁёЙй".All(character => fallbackGlyphs.Contains(character)), "Missing Cyrillic was not explicitly supplied");
            foreach (var file in face.GetProperty("files").EnumerateArray())
            {
                using var bytes = resources.GetStream("Resources/StableFonts/" + file.GetProperty("file").GetString())!;
                require(Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Embedded bitmap font hash differs");
            }

            int loaded = 0;
            foreach (int code in sourceGlyphs.Concat(fallbackGlyphs))
            {
                var glyph = fonts.Get(name, (char)code);
                require(glyph != null && glyph.Texture != null, $"Native GlyphStore failed for {name} U+{code:X4}");
                require(float.IsFinite(glyph!.XAdvance) && glyph.XAdvance >= 0 && Math.Abs(glyph.Baseline - .8f) < .001, "Invalid glyph metrics or mismatched baseline");
                require(glyph.Width > 0 && glyph.Height > 0 && glyph.Width < 2 && glyph.Height < 2, "Invalid glyph raster bounds");
                loaded++;
            }
            require(loaded == face.GetProperty("glyph_count").GetInt32(), "Not all generated glyphs loaded");
        }

        var regular = SomsLegacyFont.Font(24);
        var bold = SomsLegacyFont.Font(36, true);
        require(regular.FontName == "SomsAller-Regular" && regular.Size == 24 && bold.FontName == "SomsAller-Bold" && bold.Size == 36, "Legacy font API selected a native font or changed requested size");
        require(fonts.Get("SomsAller-Regular", 'A')!.GetKerning(fonts.Get("SomsAller-Regular", 'V')!) != 0, "Original Aller kerning was lost");
        Console.WriteLine("PASS: both original Aller font atlases, 1194 native glyph decodes, explicit Cyrillic fallback, baseline, kerning and 30 idempotent font registrations.");
    }

    private static void require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
