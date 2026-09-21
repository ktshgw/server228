#nullable enable
using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>A rectangular stable control, with skin art where available and a usable text fallback.</summary>
public partial class SomsLegacyOverlayButton : OsuClickableContainer
{
    private readonly Box highlight;
    private readonly TruncatingSpriteText? caption;
    private readonly Drawable? normalArt;
    private readonly Drawable? hoverArt;

    public SomsLegacyOverlayButton(ISkin skin, string asset, string label, Action action, Color4 colour)
    {
        Action = action;
        TooltipText = label;
        Children = new Drawable[]
        {
            highlight = new Box { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = 0.12f },
        };
        normalArt = Art(skin, asset);
        if (normalArt != null)
        {
            highlight.Alpha = 0;
            Add(normalArt);
            hoverArt = Art(skin, asset + "-over");
            if (hoverArt != null) { hoverArt.Alpha = 0; Add(hoverArt); }
        }
        else
        {
            Add(new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = colour });
            Add(caption = new TruncatingSpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Text = label,
                Font = SomsLegacyFont.Font(24, bold: true),
                Shadow = true,
            });
        }
    }

    public static Drawable? Art(ISkin skin, string asset, FillMode fill = FillMode.Fit)
    {
        if (string.IsNullOrEmpty(asset)) return null;
        // A transparent 1x1 image is a deliberate skin override, not a missing asset.
        return SomsLegacyComponent.SpriteFor(skin, asset, fill);
    }

    protected override bool OnHover(HoverEvent e)
    {
        if (hoverArt != null) { normalArt!.FadeOut(100); hoverArt.FadeIn(100); }
        else if (normalArt == null) highlight.FadeTo(0.35f, 100);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        if (hoverArt != null) { normalArt!.FadeIn(140); hoverArt.FadeOut(140); }
        else if (normalArt == null) highlight.FadeTo(0.12f, 140);
        base.OnHoverLost(e);
    }

    protected override void Update()
    {
        base.Update();
        if (caption != null) caption.MaxWidth = Math.Max(1, DrawWidth - 20);
    }

    public static Container Canvas(CompositeDrawable parent)
    {
        return new Container
        {
            Name = "soms-legacy-overlay-canvas",
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new Vector2(1024, 768),
        };
    }
}
