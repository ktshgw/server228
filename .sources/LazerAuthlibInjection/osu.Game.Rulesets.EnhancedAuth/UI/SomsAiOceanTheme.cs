#nullable enable
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Backgrounds;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays.Dialog;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

internal static class SomsAiOceanTheme
{
    public static readonly Color4 Ink = new(5, 47, 62, 255);
    public static readonly Color4 Cream = new(255, 244, 204, 255);
    public static readonly Color4 Aqua = new(109, 225, 228, 255);
    public static readonly Color4 Gold = new(255, 215, 104, 255);
    public static readonly Color4 Muted = new(185, 220, 224, 255);
    public static readonly Color4 Coral = new(255, 152, 127, 255);
    private static readonly ConditionalWeakTable<TextureStore, object> registered = new();

    public static Texture? GetTexture(TextureStore textures, string name)
    {
        lock (registered)
        {
            if (!registered.TryGetValue(textures, out _))
            {
                textures.AddTextureSource(new TextureLoaderStore(new DllResourceStore(typeof(SomsAiOceanTheme).Assembly)));
                registered.Add(textures, new object());
            }
        }
        return textures.Get("Resources/SomsAi/" + name);
    }
}

/// <summary>Screen-owned artwork and a fixed, allocation-free set of moving bubbles.</summary>
internal sealed partial class SomsAiOceanBackdrop : Container
{
    private readonly Func<bool> active;
    private readonly Sprite artwork;
    private readonly InteractiveBubble[] bubbles = new InteractiveBubble[34];
    private double elapsed;
    public override bool HandlePositionalInput => true;
    public override bool HandleNonPositionalInput => false;

    public SomsAiOceanBackdrop(Func<bool> active)
    {
        this.active = active;
        Name = "somsai-ocean-background";
        RelativeSizeAxes = Axes.Both;
        Masking = true;
        Add(new Box { RelativeSizeAxes = Axes.Both, Colour = SomsAiOceanTheme.Ink });
        Add(artwork = new Sprite
        {
            Name = "somsai-ocean-art", RelativeSizeAxes = Axes.Both, FillMode = FillMode.Fill,
            Anchor = Anchor.Centre, Origin = Anchor.Centre, Scale = new Vector2(1.025f),
        });
        for (int i = 0; i < bubbles.Length; i++)
        {
            Add(bubbles[i] = new InteractiveBubble());
        }
    }

    [BackgroundDependencyLoader]
    private void load(TextureStore textures) => artwork.Texture = SomsAiOceanTheme.GetTexture(textures, "ocean-background-matched");

    protected override void Update()
    {
        base.Update();
        if (!active()) return;
        elapsed += Math.Min(Time.Elapsed, 50) / 1000;
        artwork.Position = new Vector2((float)Math.Sin(elapsed * .10) * 4, (float)Math.Sin(elapsed * .14) * 3);
        for (int i = 0; i < bubbles.Length; i++)
        {
            // Decorative bubbles now belong to this animated layer, not the background bitmap.
            // Recycle the same drawables below the seabed after they leave the top edge.
            bool margin = i < 26;
            float x = margin ? i % 2 == 0 ? .025f + i % 7 * .022f : .975f - i % 7 * .022f : .22f + (i * 13 % 19) / 19f * .56f;
            float scale = Math.Min(DrawWidth / 1280, DrawHeight / 720);
            float diameter = (12 + i * 17 % 43) * scale * (margin ? 1 : .65f);
            double progress = (elapsed / (45 + i * 1.5) + i * .137) % 1;
            bubbles[i].Scale = new Vector2(diameter / 100);
            bubbles[i].Position = new Vector2(x * DrawWidth + (float)Math.Sin(elapsed * .28 + i) * 10 * scale,
                (float)(DrawHeight + diameter - progress * (DrawHeight + diameter * 2)));
            bubbles[i].Advance(progress, (float)Math.Min(1, Math.Min(progress * 18, (1 - progress) * 18)) * (margin ? .95f : .42f));
        }
    }

    private sealed partial class InteractiveBubble : ClickableContainer
    {
        private readonly Container visual;
        private readonly Sprite burst = new() { RelativeSizeAxes = Axes.Both, Alpha = 0 };
        private Texture?[] frames = new Texture?[16];
        private bool popped;
        private double previousProgress;
        private double popTime;

        public InteractiveBubble()
        {
            Name = "somsai-ocean-bubble";
            Size = new Vector2(100);
            Origin = Anchor.Centre;
            Action = pop;
            Add(visual = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = 50,
                BorderThickness = 3,
                BorderColour = new Color4(211, 255, 255, 230),
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = ColourInfo.GradientVertical(new Color4(139, 243, 255, 38), new Color4(17, 143, 212, 100)) },
                    new Circle { Position = new Vector2(23, 20), Size = new Vector2(29, 18), Rotation = -35, Colour = new Color4(246, 255, 255, 245) },
                    new Circle { Position = new Vector2(63, 72), Size = new Vector2(20, 9), Rotation = -38, Colour = new Color4(209, 253, 255, 180) },
                },
            });
            Add(burst);
        }

        [BackgroundDependencyLoader]
        private void load(TextureStore textures)
        {
            for (int i = 0; i < frames.Length; i++) frames[i] = SomsAiOceanTheme.GetTexture(textures, "Pop/" + i);
        }

        public void Advance(double progress, float opacity)
        {
            if (progress < previousProgress)
            {
                popped = false;
                visual.Show();
                burst.Hide();
            }
            previousProgress = progress;
            Alpha = opacity;
            if (!popped) return;
            int frame = (int)((Time.Current - popTime) / 30);
            if (frame >= 16) { Alpha = 0; burst.Hide(); }
            else burst.Texture = frames[frame];
        }

        private void pop()
        {
            if (popped) return;
            popped = true;
            popTime = Time.Current;
            visual.Hide();
            burst.Texture = frames[0];
            burst.Show();
        }


    }
}

internal sealed partial class SomsAiOceanHeader : Container
{
    private readonly bool compact;
    private readonly Sprite logo;
    private readonly OsuSpriteText caption;

    public SomsAiOceanHeader(bool compact, string text)
    {
        this.compact = compact;
        Name = "somsai-ocean-header";
        RelativeSizeAxes = Axes.X;
        Add(logo = new Sprite { Name = "somsai-ocean-logo", Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, FillMode = FillMode.Fit });
        Add(caption = new OsuSpriteText
        {
            Text = text, Font = OsuFont.GetFont(size: compact ? 19 : 20, weight: FontWeight.SemiBold),
            Colour = SomsAiOceanTheme.Cream, Anchor = Anchor.BottomCentre, Origin = Anchor.BottomCentre,
            Shadow = true, ShadowColour = SomsAiOceanTheme.Ink,
        });
    }

    [BackgroundDependencyLoader]
    private void load(TextureStore textures) => logo.Texture = SomsAiOceanTheme.GetTexture(textures, "somsai-logo");

    protected override void Update()
    {
        base.Update();
        float width = Math.Min(compact ? 180 : 400, DrawWidth * .66f);
        logo.Size = new Vector2(width, width * .39f);
        Height = logo.Height + (string.IsNullOrEmpty(caption.Text.ToString()) ? 0 : compact ? 28 : 32);
        caption.Scale = new Vector2(Math.Min(1, Math.Max(1, DrawWidth - 10) / Math.Max(1, caption.DrawWidth)));
    }
}

internal sealed partial class SomsAiOceanButton : RoundedButton
{
    private readonly bool primary;
    private Box? sheen;

    public SomsAiOceanButton(bool primary = false)
    {
        this.primary = primary;
        RelativeSizeAxes = Axes.X;
        Height = primary ? 50 : 44;
        BackgroundColour = primary ? SomsAiOceanTheme.Gold : new Color4(20, 133, 150, 255);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Triangles?.Hide();
        Content.CornerRadius = 10;
        Content.BorderThickness = 1.5f;
        Content.BorderColour = primary ? SomsAiOceanTheme.Cream : SomsAiOceanTheme.Aqua;
        SpriteText.Font = OsuFont.GetFont(size: primary ? 22 : 18, weight: FontWeight.Bold);
        SpriteText.Colour = primary ? SomsAiOceanTheme.Ink : SomsAiOceanTheme.Cream;
        Add(new Box
        {
            RelativeSizeAxes = Axes.Both, Depth = .5f,
            Colour = ColourInfo.GradientVertical(primary ? new Color4(255, 239, 161, 255) : new Color4(115, 234, 239, 145),
                primary ? new Color4(236, 188, 65, 255) : new Color4(4, 83, 104, 85)),
        });
        Add(sheen = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.White, Alpha = 0, Depth = -.5f });
    }

    protected override bool OnHover(HoverEvent e)
    {
        sheen?.FadeTo(.12f, 160);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        sheen?.FadeOut(240);
        base.OnHoverLost(e);
    }

    protected override void Update()
    {
        base.Update();
        // Preserve native button actions/sounds while keeping long translated captions inside the frame.
        float available = Math.Max(1, DrawWidth - 26);
        SpriteText.Scale = new Vector2(Math.Min(1, available / Math.Max(1, SpriteText.DrawWidth)));
    }
}

internal sealed partial class SomsAiShell : Container
{
    public SomsAiShell()
    {
        Size = new Vector2(27);
        for (int i = 0; i < 7; i++)
            Add(new Circle
            {
                Anchor = Anchor.BottomCentre, Origin = Anchor.BottomCentre, Y = -4,
                Size = new Vector2(8, 23), Rotation = -57 + i * 19,
                Colour = ColourInfo.GradientVertical(SomsAiOceanTheme.Cream, SomsAiOceanTheme.Gold),
                BorderThickness = .6f, BorderColour = new Color4(172, 127, 62, 255),
            });
        Add(new Circle { Anchor = Anchor.BottomCentre, Origin = Anchor.BottomCentre, Size = new Vector2(13, 6), Colour = SomsAiOceanTheme.Gold });
    }
}

internal sealed partial class SomsAiOceanConfirmDialog : ConfirmDialog
{
    public SomsAiOceanConfirmDialog(string message, Action confirm) : base(message, confirm) { }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        // Style only this dialog; native confirmation behaviour (including Escape) remains intact.
        // Content is unmasked in newer lazer versions. Decorate the actual masked frame,
        // without relying on the compile-time package's private hierarchy.
        foreach (var root in InternalChildren.OfType<Container>()) style(root);
        int index = 0;
        foreach (var button in Buttons)
            button.ButtonColour = index++ == 0 ? new Color4(165, 128, 39, 255) : new Color4(20, 133, 150, 255);
    }

    private static void style(Container container)
    {
        if (container.Masking && container.CornerRadius >= 10)
        {
            container.BorderThickness = 2;
            container.BorderColour = SomsAiOceanTheme.Cream;
        }
        if (container.Children.Any(d => d is Triangles or TrianglesV2))
        {
            foreach (var background in container.Children)
            {
                if (background is Triangles or TrianglesV2) background.Hide();
                else if (background is Box) background.Colour = SomsAiOceanTheme.Ink;
            }
        }
        foreach (var child in container.Children.OfType<Container>()) style(child);
    }
}
