#nullable enable
using System.Runtime.CompilerServices;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Game.Skinning;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>
/// The small, explicitly selected stable UI resource pack. This is only consulted by our legacy
/// interface, and never inserted into gameplay's skin chain or into the user's skin files.
/// </summary>
public static class SomsStableInterfaceResources
{
    private static readonly ConditionalWeakTable<TextureStore, object> registeredStores = new();
    private static readonly ConditionalWeakTable<ISkin, TextureStore> skinStores = new();

    internal static void Register(ISkin skin, TextureStore textures)
    {
        lock (registeredStores)
        {
            if (!registeredStores.TryGetValue(textures, out _))
            {
                textures.AddTextureSource(new TextureLoaderStore(new DllResourceStore(typeof(SomsStableInterfaceResources).Assembly)));
                registeredStores.Add(textures, new object());
            }

            // The keys are weak: changing skins or disposing a game host must not leave its skin
            // or renderer rooted by this shared helper. Texture ownership remains with that host.
            skinStores.Remove(skin);
            skinStores.Add(skin, textures);
        }
    }

    public static Drawable? GetSprite(ISkin skin, string name, string? fallbackName = null)
    {
        if (name.EndsWith("-over", System.StringComparison.Ordinal))
        {
            string normalName = name[..^5];
            var normalProvider = (skin as ISkinSource)?.FindProvider(source =>
                source.GetTexture(normalName + "-0") != null || source.GetTexture(normalName) != null) ?? skin;
            if (!isBuiltInSkin(normalProvider) &&
                (normalProvider.GetTexture(normalName + "-0") != null || normalProvider.GetTexture(normalName) != null))
            {
                // A skin's deliberately hidden/custom button must not become the default button
                // when hovered simply because that skin does not include an optional -over image.
                return normalProvider.GetAnimation(name, animatable: true, looping: true, applyConfigFrameRate: true);
            }
        }

        // Find one provider first. A custom static image (even transparent 1x1) wins over a default
        // animation, and a short custom animation must never acquire frames from a fallback skin.
        var provider = (skin as ISkinSource)?.FindProvider(source =>
            source.GetTexture(name + "-0") != null || source.GetTexture(name) != null) ?? skin;

        if (!isBuiltInSkin(provider))
        {
            var custom = provider.GetAnimation(name, animatable: true, looping: true, applyConfigFrameRate: true);
            if (custom != null) return custom;
        }

        if (skinStores.TryGetValue(skin, out var textures))
        {
            string defaultName = fallbackName ?? name;
            // Match LegacySkin.GetTexture: Texture.DisplayWidth/Height are original SD pixels.
            // Do not apply the 480/768 screen coordinate conversion here; screens own that scale.
            var texture = textures.Get("Resources/StableInterface/" + defaultName + "@2x");
            float density = 2;
            if (texture == null)
            {
                texture = textures.Get("Resources/StableInterface/" + defaultName);
                density = 1;
            }

            if (texture != null)
            {
                texture.ScaleAdjust = density;
                return new Sprite { Texture = texture };
            }
        }

        return skin.GetAnimation(name, animatable: true, looping: true, applyConfigFrameRate: true);
    }

    private static bool isBuiltInSkin(ISkin skin)
    {
        // A transformer can wrap the default provider. Custom transformers continue to use their
        // own lookups above; only the known native built-ins yield to stable's original interface.
        while (skin is ISkinTransformer transformer) skin = transformer.Skin;
        return skin is DefaultLegacySkin or TrianglesSkin or ArgonSkin or RetroSkin;
    }
}
