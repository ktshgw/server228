#nullable enable
using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Screens;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;
using osuTK;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public partial class SomsAiScreen
{
    private Container oceanContent = null!;
    private OsuScrollContainer oceanScroll = null!;

    protected override void BuildLayout()
    {
        if (matchOnly) { buildArenaLayout(); return; }
        Body.Spacing = new Vector2(0, 14);
        if (!matchOnly) Body.Add(new SomsAiOceanHeader(matchOnly || partyOnly,
            matchOnly ? "ТУРНИРНАЯ АРЕНА" : partyOnly ? "ВАША КОМАНДА" : ""));
        StatusText.Colour = SomsAiOceanTheme.Cream;
        StatusText.Shadow = true;
        StatusText.ShadowColour = SomsAiOceanTheme.Ink;
        Body.Add(new StatusLine(StatusText));
        InternalChild = oceanContent = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                new SomsAiOceanBackdrop(() => this.IsCurrentScreen()),
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = 12, Bottom = 72 },
                    Child = oceanScroll = new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = new OceanContentWidth(Body, matchOnly),
                    },
                },
            },
        };
        oceanContent.Colour = osuTK.Graphics.Color4.White;
    }

    // Keep theme dependencies scoped to SOMSAI. Native multiplayer and gameplay retain their colours.
    protected override osu.Framework.Allocation.IReadOnlyDependencyContainer CreateChildDependencies(osu.Framework.Allocation.IReadOnlyDependencyContainer parent)
    {
        var dependencies = new osu.Framework.Allocation.DependencyContainer(base.CreateChildDependencies(parent));
        dependencies.Cache(new OverlayColourProvider(190));
        return dependencies;
    }

    private void showOcean()
    {
        oceanContent?.FadeInFromZero(250, Easing.OutQuint);
    }

    private void revealForm(FillFlowContainer form)
    {
        form.FadeInFromZero(200, Easing.OutQuint);
        Scheduler.AddDelayed(() => { if (Alive && form.IsPresent) oceanScroll.ScrollIntoView(form); }, 250);
    }

    private new static SomsAiOceanButton Button(string text, Action action) => new() { Text = text, Action = action };
    private static SomsAiOceanButton PrimaryButton(string text, Action action) => new(true) { Text = text, Action = action };
    private new static OsuSpriteText Text(string text, float size = 20) => new()
    {
        Text = text, Font = OsuFont.GetFont(size: size), Colour = SomsAiOceanTheme.Cream,
    };

    private static Drawable Heading(string text) => new Container
    {
        RelativeSizeAxes = Axes.X, Height = 32,
        Children = new Drawable[]
        {
            new SomsAiShell { Y = 2 },
            new Container
            {
                RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 40 },
                Child = new TruncatingSpriteText
                {
                    RelativeSizeAxes = Axes.X, Text = text,
                    Font = OsuFont.GetFont(size: 26, weight: FontWeight.SemiBold), Colour = SomsAiOceanTheme.Cream,
                },
            },
        },
    };

    private sealed partial class StatusLine : Container
    {
        private readonly OsuSpriteText text;
        public StatusLine(OsuSpriteText text)
        {
            this.text = text;
            RelativeSizeAxes = Axes.X; Height = 23;
            text.Anchor = text.Origin = Anchor.TopCentre;
            Child = text;
        }
        protected override void Update()
        {
            base.Update();
            text.Scale = new Vector2(Math.Min(1, Math.Max(1, DrawWidth - 10) / Math.Max(1, text.DrawWidth)));
        }
    }

    private sealed partial class OceanContentWidth : Container
    {
        private readonly Drawable body;
        private readonly bool match;
        public OceanContentWidth(Drawable body, bool match)
        {
            this.body = body; this.match = match;
            RelativeSizeAxes = Axes.X; AutoSizeAxes = Axes.Y;
            body.RelativeSizeAxes &= ~Axes.X;
            body.Anchor = body.Origin = Anchor.TopCentre;
            Child = body;
        }
        protected override void Update()
        {
            body.Width = Math.Max(1, Math.Min(match ? 1460 : 1280, DrawWidth - (DrawWidth < 800 ? 32 : 100)));
            base.Update();
        }
    }
}
