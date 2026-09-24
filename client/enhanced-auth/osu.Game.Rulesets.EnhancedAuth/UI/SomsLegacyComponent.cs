#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Animations;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Threading;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Skinning;
using osuTK;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>Owns only the added UI and its subscriptions, never a user's skin or native controls.</summary>
public abstract partial class SomsLegacyComponent : SkinReloadableDrawable
{
    private Bindable<bool>? preference;
    private ScheduledDelegate? refresh;
    private bool pendingRefresh, refreshWhenVisible;
    private readonly List<(Drawable Drawable, float Alpha, bool AlwaysPresent)> hidden = new();
    protected bool LegacyEnabled => preference?.Value == true && SomsClientPreferences.Enabled;
    protected ISkinSource? Skin { get; private set; }
    private TextureStore? interfaceTextures;

    protected SomsLegacyComponent()
    {
        RelativeSizeAxes = Axes.Both;
        AlwaysPresent = true;
        Alpha = 0;
    }

    [BackgroundDependencyLoader]
    private void loadStableInterfaceResources(TextureStore textures, FontStore fonts)
    {
        interfaceTextures = textures;
        SomsLegacyFont.Register(fonts);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        preference = SomsClientPreferences.Instance.LegacyInterface.GetBoundCopy();
        preference.BindValueChanged(_ => RequestRefresh(), true);
    }

    protected override void SkinChanged(ISkinSource skin)
    {
        base.SkinChanged(skin);
        Skin = skin;
        if (interfaceTextures != null) SomsStableInterfaceResources.Register(skin, interfaceTextures);
        if (preference != null) RequestRefresh(120, visibleOnly: true);
    }

    protected void RequestRefresh(double delay = 0, bool visibleOnly = false)
    {
        if (SomsDrawableLifecycle.IsDisposed(this)) return;
        refresh?.Cancel();
        // A skin event must not postpone a pending enable/disable restoration of
        // hidden native screens until the user happens to visit them again.
        if (pendingRefresh && !refreshWhenVisible) { visibleOnly = false; delay = 0; }
        pendingRefresh = true;
        refreshWhenVisible = visibleOnly;
        refresh = Scheduler.AddDelayed(rebuildIfReady, delay);
    }

    private void rebuildIfReady()
    {
        refresh = null;
        if (!pendingRefresh || SomsDrawableLifecycle.IsDisposed(this)) return;
        if (refreshWhenVisible && LegacyEnabled)
            for (Drawable? parent = Parent; parent != null; parent = parent.Parent)
                if (parent.Alpha <= 0) return;
        pendingRefresh = false;
        RestoreNative();
        ClearInternal();
        Alpha = 0;
        if (LegacyEnabled && Skin != null) Rebuild(Skin);
    }

    protected abstract void Rebuild(ISkinSource skin);

    protected void HideNative(IEnumerable<Drawable> drawables, bool keepUpdating = false)
    {
        foreach (var drawable in drawables.Distinct())
        {
            if (drawable == this || SomsDrawableLifecycle.IsDisposed(drawable)) continue;
            hidden.Add((drawable, drawable.Alpha, drawable.AlwaysPresent));
            SomsLegacyInputPatch.Block(drawable);
            drawable.AlwaysPresent = keepUpdating;
            drawable.Alpha = 0;
        }
    }

    protected void RestoreNative()
    {
        foreach (var entry in hidden)
        {
            SomsLegacyInputPatch.Restore(entry.Drawable);
            if (SomsDrawableLifecycle.IsDisposed(entry.Drawable)) continue;
            entry.Drawable.Alpha = entry.Alpha;
            entry.Drawable.AlwaysPresent = entry.AlwaysPresent;
        }
        hidden.Clear();
        RestoreLayout();
    }

    protected virtual void RestoreLayout() { }

    protected override void Update()
    {
        base.Update();
        if (pendingRefresh && refresh == null) rebuildIfReady();
        // Native hover/entrance transforms may continue while their appearance is replaced.
        // Preserve their clocks, children, actions and layout; hide only the visual roots.
        for (int i = 0; i < hidden.Count; i++)
        {
            var entry = hidden[i];
            if (SomsDrawableLifecycle.IsDisposed(entry.Drawable)) continue;
            if (entry.Drawable.Alpha > 0) hidden[i] = (entry.Drawable, entry.Drawable.Alpha, entry.AlwaysPresent);
            entry.Drawable.Alpha = 0;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        refresh?.Cancel();
        preference?.UnbindAll();
        RestoreNative();
        base.Dispose(isDisposing);
    }

    public static Drawable? NaturalSpriteFor(ISkin skin, string name)
    {
        // Use the legacy loader's provider selection, @2x scale and animation clock.
        // Looking up frames through the entire skin chain can append default frames
        // to a shorter user animation, or replace a static user image with default animation.
        return SomsStableInterfaceResources.GetSprite(skin, name);
    }

    public static Drawable? SpriteFor(ISkin skin, string name, FillMode fill = FillMode.Fit)
    {
        var sprite = NaturalSpriteFor(skin, name);
        if (sprite == null) return null;
        sprite.RelativeSizeAxes = Axes.Both;
        sprite.Size = Vector2.One;
        sprite.FillMode = fill;
        sprite.Anchor = Anchor.Centre;
        sprite.Origin = Anchor.Centre;
        return sprite;
    }

    public static Vector2 NaturalSpriteSize(Drawable drawable)
    {
        // TextureAnimation creates its display sprite only during dependency loading.
        // The first frame is already available when arranging a new skin scene.
        var texture = drawable is TextureAnimation animation && animation.FrameCount > 0
            ? animation.CurrentFrame : (drawable as Sprite)?.Texture;
        return texture == null ? Vector2.Zero : new Vector2(texture.DisplayWidth, texture.DisplayHeight);
    }
}

public sealed partial class SomsLegacyTexture : SomsLegacyComponent
{
    private readonly string asset;
    private readonly Drawable[] originals;
    private readonly FillMode fill;
    public bool HasTexture => LegacyEnabled && Alpha > 0;

    public SomsLegacyTexture(string asset, IEnumerable<Drawable>? originals = null, FillMode fill = FillMode.Fit)
    {
        this.asset = asset;
        this.originals = originals?.ToArray() ?? Array.Empty<Drawable>();
        this.fill = fill;
        Name = "soms-legacy-" + asset;
    }

    protected override void Rebuild(ISkinSource skin)
    {
        var sprite = SpriteFor(skin, asset, fill);
        if (sprite == null) return;
        AddInternal(sprite);
        HideNative(originals);
        Alpha = 1;
    }
}
