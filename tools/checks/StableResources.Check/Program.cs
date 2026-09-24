using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Animations;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Game.Audio;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Skinning;

internal static class Program
{
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("client-directory plugin-dll");
        string client = Path.GetFullPath(args[0]), plugin = Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = name.Name == "osu.Game.Rulesets.EnhancedAuth" ? plugin : Path.Combine(client, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        foreach (string assembly in new[] { "osu.Framework", "osu.Game.Resources", "osu.Game" })
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(client, assembly + ".dll"));
        return run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int run()
    {
        var renderer = new DummyRenderer();
        using var textures = new TextureStore(renderer);
        using var nativeResources = new DllResourceStore(Assembly.Load("osu.Game.Resources"));
        using var nativeTextures = new TextureStore(renderer,
            new TextureLoaderStore(new NamespacedResourceStore<byte[]>(nativeResources, "Skins/Legacy")));

        // Give the real installed DefaultLegacySkin its real installed texture store, without
        // constructing a profile, Realm, an API client, or opening the user's live game data.
        var builtin = (DefaultLegacySkin)RuntimeHelpers.GetUninitializedObject(typeof(DefaultLegacySkin));
        typeof(Skin).GetField("<Textures>k__BackingField", flags)!.SetValue(builtin, nativeTextures);
        builtin.Configuration = new SkinConfiguration();
        var defaults = new Source(builtin);
        register(defaults, textures);

        using var resources = new DllResourceStore(typeof(SomsStableInterfaceResources).Assembly);
        using var manifestStream = resources.GetStream("Resources/StableInterface/manifest.json")!;
        using var manifest = JsonDocument.Parse(manifestStream);
        int checkedAssets = 0;
        foreach (var asset in manifest.RootElement.GetProperty("assets").EnumerateArray())
        {
            string key = asset.GetProperty("key").GetString()!, file = asset.GetProperty("file").GetString()!;
            string name = key.Replace("@2x", "");
            using var bytes = resources.GetStream("Resources/StableInterface/" + file)!;
            require(Convert.ToHexString(SHA256.HashData(bytes)).Equals(asset.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Embedded SHA mismatch: " + key);
            using var drawable = SomsLegacyComponent.NaturalSpriteFor(defaults, name);
            require(drawable is Sprite { Texture: not null }, "Default stable resource did not load: " + key);
            var texture = ((Sprite)drawable!).Texture!;
            var packed = textures.Get("Resources/StableInterface/" + key)!;
            var nativeProperty = typeof(Texture).GetProperty("NativeTexture", flags)!;
            require(ReferenceEquals(nativeProperty.GetValue(texture), nativeProperty.GetValue(packed)), "Native default overrode the stable resource: " + key);
            require(texture.DisplayWidth == asset.GetProperty("logical_width").GetSingle() && texture.DisplayHeight == asset.GetProperty("logical_height").GetSingle(), "Wrong @2x display size: " + key);
            checkedAssets++;
        }
        require(checkedAssets >= 100, "The real resource manifest was not checked");
        require(builtin.GetTexture("ranking-A") != null, "Installed legacy skin fixture is empty");
        Console.WriteLine($"PASS: {checkedAssets} embedded source hashes, native runtime texture decodes, stable priority and @2x display sizes.");

        var custom = new CustomSkin { FrameRate = 12 };
        var source = new Source(custom, builtin);
        register(source, textures);
        custom.Textures["selection-mods"] = renderer.CreateTexture(1, 1);
        using (var hidden = SomsLegacyComponent.NaturalSpriteFor(source, "selection-mods"))
            require(hidden is Sprite sprite && ReferenceEquals(sprite.Texture, custom.Textures["selection-mods"]), "A 1x1 custom override was replaced");
        require(SomsLegacyComponent.NaturalSpriteFor(source, "selection-mods-over") == null, "Hover resurrected a deliberately hidden/custom button");
        custom.Textures["selection-mods-over"] = renderer.CreateTexture(2, 3);
        using (var customHover = SomsLegacyComponent.NaturalSpriteFor(source, "selection-mods-over"))
            require(customHover is Sprite sprite && ReferenceEquals(sprite.Texture, custom.Textures["selection-mods-over"]), "Custom hover was lost");

        custom.Textures["menu-button-play-0"] = renderer.CreateTexture(40, 20);
        custom.Textures["menu-button-play-1"] = renderer.CreateTexture(41, 21);
        using (var animation = SomsLegacyComponent.NaturalSpriteFor(source, "menu-button-play"))
        {
            require(animation is TextureAnimation { FrameCount: 2 }, "Custom animation was replaced or acquired fallback frames");
            require(Math.Abs(((TextureAnimation)animation!).DefaultFrameLength - 1000d / 12) < .001, "Skin AnimationFramerate was ignored");
        }

        var otherProvider = new CustomSkin();
        otherProvider.Textures["menu-osu-0"] = renderer.CreateTexture(100, 100);
        otherProvider.Textures["menu-osu-1"] = renderer.CreateTexture(100, 100);
        otherProvider.Textures["menu-osu-2"] = renderer.CreateTexture(100, 100);
        custom.Textures["menu-osu"] = renderer.CreateTexture(5, 5);
        var staticSource = new Source(custom, otherProvider, builtin);
        register(staticSource, textures);
        using (var staticArt = SomsLegacyComponent.NaturalSpriteFor(staticSource, "menu-osu"))
            require(staticArt is Sprite sprite && ReferenceEquals(sprite.Texture, custom.Textures["menu-osu"]), "Default animation replaced a static custom image");
        custom.Textures.Remove("menu-osu");
        custom.Textures["menu-osu-0"] = renderer.CreateTexture(4, 4);
        custom.Textures["menu-osu-1"] = renderer.CreateTexture(4, 4);
        using (var shortAnimation = SomsLegacyComponent.NaturalSpriteFor(staticSource, "menu-osu"))
            require(shortAnimation is TextureAnimation { FrameCount: 2 }, "Third fallback frame leaked into a two-frame user animation");
        Console.WriteLine("PASS: transparent 1x1 override, custom hover, static-versus-animation priority, one-provider frames and AnimationFramerate.");

        using (var secondTextures = new TextureStore(new DummyRenderer()))
        {
            using var first = (Sprite)SomsLegacyComponent.NaturalSpriteFor(defaults, "menu-osu")!;
            for (int i = 0; i < 30; i++) register(defaults, secondTextures);
            using var second = (Sprite)SomsLegacyComponent.NaturalSpriteFor(defaults, "menu-osu")!;
            var nativeTextureProperty = typeof(Texture).GetProperty("NativeTexture", flags)!;
            require(!ReferenceEquals(nativeTextureProperty.GetValue(first.Texture), nativeTextureProperty.GetValue(second.Texture)), "Renderer texture leaked into another host");
            int copies = secondTextures.GetAvailableResources().Count(name => name.EndsWith("StableInterface.menu-osu@2x.png", StringComparison.Ordinal) || name.EndsWith("StableInterface/menu-osu@2x.png", StringComparison.Ordinal));
            require(copies == 1, "Repeated registration duplicated a resource store");
        }
        var weak = abandonedRegistry();
        for (int i = 0; i < 5 && weak.Any(reference => reference.IsAlive); i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        require(weak.All(reference => !reference.IsAlive), "Static resource registry retained a disposed skin or host texture store");
        Console.WriteLine("PASS: 30 repeated registrations, renderer isolation and weak lifetime after disposal.");
        FontChecks.Run();
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] abandonedRegistry()
    {
        var renderer = new DummyRenderer();
        using var textures = new TextureStore(renderer);
        var skin = new Source(new CustomSkin());
        register(skin, textures);
        using var art = SomsLegacyComponent.NaturalSpriteFor(skin, "menu-osu");
        require(art != null, "Disposable host did not load fallback");
        return new[] { new WeakReference(skin), new WeakReference(textures) };
    }

    private static void register(ISkin source, TextureStore textures) => typeof(SomsStableInterfaceResources)
        .GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { source, textures });
    private static void require(bool condition, string message) { if (!condition) throw new Exception(message); }
}

internal sealed class Source(params ISkin[] skins) : ISkinSource
{
    public event Action? SourceChanged { add { } remove { } }
    public IEnumerable<ISkin> AllSources => skins;
    public ISkin? FindProvider(Func<ISkin, bool> lookup) => skins.FirstOrDefault(lookup);
    public Drawable? GetDrawableComponent(ISkinComponentLookup lookup) => null;
    public ISample? GetSample(ISampleInfo info) => null;
    public Texture? GetTexture(string name, WrapMode s, WrapMode t) => skins.Select(skin => skin.GetTexture(name, s, t)).FirstOrDefault(texture => texture != null);
    public IBindable<TValue>? GetConfig<TLookup, TValue>(TLookup lookup) where TLookup : notnull where TValue : notnull => skins.Select(skin => skin.GetConfig<TLookup, TValue>(lookup)).FirstOrDefault(value => value != null);
}

internal sealed class CustomSkin : ISkin
{
    public Dictionary<string, Texture> Textures { get; } = new();
    public int FrameRate;
    public Drawable? GetDrawableComponent(ISkinComponentLookup lookup) => null;
    public ISample? GetSample(ISampleInfo info) => null;
    public Texture? GetTexture(string name, WrapMode s, WrapMode t) => Textures.GetValueOrDefault(name);
    public IBindable<TValue>? GetConfig<TLookup, TValue>(TLookup lookup) where TLookup : notnull where TValue : notnull =>
        lookup is SkinConfiguration.LegacySetting.AnimationFramerate && typeof(TValue) == typeof(int)
            ? (IBindable<TValue>)(object)new Bindable<int>(FrameRate) : null;
}
