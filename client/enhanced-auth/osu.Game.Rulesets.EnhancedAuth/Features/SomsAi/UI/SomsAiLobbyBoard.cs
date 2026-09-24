#nullable enable
using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>
/// Main SOMSAI lobby: match history on the left, a tactile waiting area in the
/// middle and all matchmaking decisions on the right. It collapses to a vertical
/// board for narrow windows while retaining the same controls.
/// </summary>
internal sealed partial class SomsAiLobbyBoard : Container
{
    private readonly LobbySurface history;
    private readonly LobbySurface playground;
    private readonly LobbySurface matchmaking;

    public SomsAiLobbyBoard(Drawable recentMatches, Drawable party, Drawable search, Action showRating, Action showCustoms)
    {
        Name = "somsai-lobby-board";
        RelativeSizeAxes = Axes.X;

        var leftFlow = SomsNativeMatchScreen.Flow();
        leftFlow.Add(sectionTitle("Команда", ""));
        leftFlow.Add(party);

        var tabs = new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 44,
            Children = new Drawable[]
            {
                tab("Рейтинг", showRating).With(button =>
                {
                    button.RelativeSizeAxes = Axes.X;
                    button.Width = .49f;
                }),
                tab("Кастомки", showCustoms).With(button =>
                {
                    button.RelativeSizeAxes = Axes.X;
                    button.Width = .49f;
                    button.Anchor = button.Origin = Anchor.TopRight;
                }),
            },
        };

        var rightFlow = SomsNativeMatchScreen.Flow();
        leftFlow.Add(tabs);
        rightFlow.Add(search);

        InternalChildren = new Drawable[]
        {
            history = new LobbySurface(leftFlow, true) { Name = "somsai-party" },
            playground = new LobbySurface(new Container
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    sectionTitle("Последние матчи", "20 последних сыгранных встреч"),
                    new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 55 },
                        Child = new OsuScrollContainer { RelativeSizeAxes = Axes.Both, ScrollbarVisible = false, Child = recentMatches } },
                },
            }, false) { Name = "somsai-recent-matches" },
            matchmaking = new LobbySurface(rightFlow, true) { Name = "somsai-matchmaking-actions" },
        };
    }

    private static Drawable sectionTitle(string title, string subtitle)
    {
        var flow = SomsNativeMatchScreen.Flow();
        flow.Spacing = new Vector2(0, 1);
        flow.Add(new OsuSpriteText
        {
            Text = title,
            Font = OsuFont.GetFont(size: 24, weight: FontWeight.Bold),
            Colour = SomsAiOceanTheme.Cream,
        });
        flow.Add(new OsuSpriteText
        {
            Text = subtitle,
            Font = OsuFont.GetFont(size: 14),
            Colour = SomsAiOceanTheme.Muted,
        });
        return flow;
    }

    private static SomsAiOceanButton tab(string caption, Action action) => new()
    {
        Width = 150,
        Height = 44,
        Text = caption,
        Action = action,
    };

    protected override void Update()
    {
        base.Update();
        const float gap = 14;

        if (DrawWidth >= 1040)
        {
            // Keep the complete board visible at the common 1280x720 game size.
            // Long history/party content scrolls inside the left panel instead.
            Height = 440;
            float side = Math.Clamp(DrawWidth * .285f, 300, 385);
            history.Position = Vector2.Zero;
            history.Size = new Vector2(side, Height);
            matchmaking.Position = new Vector2(DrawWidth - side, 0);
            matchmaking.Size = new Vector2(side, Height);
            playground.Position = new Vector2(side + gap, 0);
            playground.Size = new Vector2(Math.Max(260, DrawWidth - side * 2 - gap * 2), Height);
        }
        else
        {
            float panelHeight = Math.Clamp(DrawWidth * .72f, 440, 570);
            float playHeight = Math.Clamp(DrawWidth * .55f, 360, 480);
            history.Position = Vector2.Zero;
            history.Size = new Vector2(DrawWidth, panelHeight);
            playground.Position = new Vector2(0, panelHeight + gap);
            playground.Size = new Vector2(DrawWidth, playHeight);
            matchmaking.Position = new Vector2(0, panelHeight + playHeight + gap * 2);
            matchmaking.Size = new Vector2(DrawWidth, panelHeight);
            Height = panelHeight * 2 + playHeight + gap * 2;
        }
    }

    private sealed partial class LobbySurface : Container
    {
        public LobbySurface(Drawable content, bool scroll)
        {
            Masking = true;
            CornerRadius = 18;
            BorderThickness = 2;
            BorderColour = new Color4(187, 244, 238, 205);
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientVertical(new Color4(3, 59, 75, 246), new Color4(2, 31, 48, 250)),
                },
                new Circle
                {
                    Anchor = Anchor.BottomRight,
                    Origin = Anchor.Centre,
                    Size = new Vector2(230),
                    Colour = new Color4(92, 227, 219, 10),
                    BorderColour = new Color4(125, 246, 234, 30),
                    BorderThickness = 2,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding(18),
                    Child = scroll
                        ? new OsuScrollContainer { RelativeSizeAxes = Axes.Both, ScrollbarVisible = false, Child = content }
                        : content,
                },
            };
        }
    }
}

internal sealed partial class SomsAiRecentMatchRow : osu.Game.Graphics.Containers.OsuClickableContainer
{
    public SomsAiRecentMatchRow(SomsAiRecentMatch match, Action open)
    {
        Name = "somsai-recent-match";
        Action = open;
        RelativeSizeAxes = Axes.X;
        Height = 72;
        Masking = true;
        CornerRadius = 10;

        int localTeam = match.LocalTeamId ?? 0;
        int own = match.Wins.ElementAtOrDefault(localTeam);
        int other = match.Wins.ElementAtOrDefault(localTeam == 0 ? 1 : 0);
        Color4 resultColour = match.Outcome switch
        {
            "win" => new Color4(113, 235, 172, 255),
            "loss" => new Color4(255, 134, 132, 255),
            "cancelled" => SomsAiOceanTheme.Muted,
            _ => SomsAiOceanTheme.Gold,
        };
        string opponent = match.Opponents.Count == 0 ? "соперник" : string.Join(" + ", match.Opponents.Select(SomsAiRank.DisplayName));
        string result = match.Outcome switch
        {
            "win" => "ПОБЕДА",
            "loss" => "ПОРАЖЕНИЕ",
            "cancelled" => "ОТМЕНЁН",
            _ => "НИЧЬЯ",
        };
        string delta = match.Ranked ? $" · {match.RatingDelta:+0;-0;0} MMR" : " · кастом";
        string playedAt = match.EndedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "";

        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(7, 82, 98, 210) },
            new Box { RelativeSizeAxes = Axes.Y, Width = 5, Colour = resultColour },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Left = 14, Right = 145, Vertical = 8 },
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 2),
                Children = new Drawable[]
                {
                    new TruncatingSpriteText
                    {
                        RelativeSizeAxes = Axes.X,
                        Text = $"{match.Format}  ·  против {opponent}",
                        Font = OsuFont.GetFont(size: 16, weight: FontWeight.SemiBold),
                        Colour = SomsAiOceanTheme.Cream,
                    },
                    new OsuSpriteText
                    {
                        Text = $"{result}  {own}:{other}{delta}",
                        Font = OsuFont.GetFont(size: 14, weight: FontWeight.Bold),
                        Colour = resultColour,
                    },
                },
            },
            new OsuSpriteText
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Position = new Vector2(-12, 9),
                Text = playedAt,
                Font = OsuFont.GetFont(size: 12, weight: FontWeight.SemiBold),
                Colour = SomsAiOceanTheme.Muted,
            },
        };
    }
}

internal sealed partial class SomsAiInteractiveReef : Container
{
    public SomsAiInteractiveReef()
    {
        RelativeSizeAxes = Axes.Both;
        Children = new Drawable[]
        {
            new OsuSpriteText
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Y = 8,
                Text = "Пока ищется матч",
                Font = OsuFont.GetFont(size: 22, weight: FontWeight.Bold),
                Colour = SomsAiOceanTheme.Cream,
            },
            new OsuSpriteText
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Y = 38,
                Text = "нажимайте на предметы и пузыри",
                Font = OsuFont.GetFont(size: 14),
                Colour = SomsAiOceanTheme.Muted,
            },
            new InteractiveToy("interactive-jelly-lantern", "Медуза-светильник", ToyReaction.Pulse)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.Centre,
                RelativePositionAxes = Axes.Both,
                Position = new Vector2(.28f, .34f),
                Size = new Vector2(150, 205),
            },
            new InteractiveToy("interactive-shell-box", "Жемчужная шкатулка", ToyReaction.Bounce)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.Centre,
                RelativePositionAxes = Axes.Both,
                Position = new Vector2(.68f, .42f),
                Size = new Vector2(185),
            },
            new InteractiveToy("interactive-coral-game", "Коралловый автомат", ToyReaction.Shake)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.Centre,
                RelativePositionAxes = Axes.Both,
                Position = new Vector2(.49f, .75f),
                Size = new Vector2(190),
            },
        };
    }

    private enum ToyReaction
    {
        Pulse,
        Bounce,
        Shake,
    }

    private sealed partial class InteractiveToy : ClickableContainer
    {
        private readonly string textureName;
        private readonly ToyReaction reaction;
        private readonly Container particles;
        private readonly Sprite sprite;
        private readonly Circle glow;
        private int clicks;

        public InteractiveToy(string textureName, string caption, ToyReaction reaction)
        {
            this.textureName = textureName;
            this.reaction = reaction;
            Name = "somsai-interactive-toy-" + textureName;
            Action = react;
            Children = new Drawable[]
            {
                glow = new Circle
                {
                    RelativeSizeAxes = Axes.Both,
                    Scale = new Vector2(.72f),
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Colour = new Color4(103, 246, 237, 70),
                    Blending = BlendingParameters.Additive,
                    Alpha = .35f,
                },
                sprite = new Sprite
                {
                    RelativeSizeAxes = Axes.Both,
                    FillMode = FillMode.Fit,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                },
                particles = new Container { RelativeSizeAxes = Axes.Both },
                new OsuSpriteText
                {
                    Anchor = Anchor.BottomCentre,
                    Origin = Anchor.TopCentre,
                    Y = 4,
                    Text = caption,
                    Font = OsuFont.GetFont(size: 13, weight: FontWeight.SemiBold),
                    Colour = SomsAiOceanTheme.Cream,
                    Shadow = true,
                    ShadowColour = SomsAiOceanTheme.Ink,
                },
            };
        }

        [BackgroundDependencyLoader]
        private void load(TextureStore textures) => sprite.Texture = SomsAiOceanTheme.GetTexture(textures, textureName);

        protected override bool OnHover(HoverEvent e)
        {
            sprite.ScaleTo(1.05f, 180, Easing.OutQuint);
            glow.FadeTo(.75f, 180);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            sprite.ScaleTo(1, 260, Easing.OutQuint);
            glow.FadeTo(.35f, 260);
            base.OnHoverLost(e);
        }

        private void react()
        {
            clicks++;
            sprite.ClearTransforms();
            glow.ClearTransforms();
            glow.FadeTo(.95f, 70).Then().FadeTo(.35f, 420, Easing.OutQuint);

            switch (reaction)
            {
                case ToyReaction.Pulse:
                    sprite.ScaleTo(1.16f, 120, Easing.OutBack).Then().ScaleTo(1, 460, Easing.OutElastic);
                    sprite.RotateTo(clicks % 2 == 0 ? -7 : 7, 130, Easing.OutQuint).Then().RotateTo(0, 500, Easing.OutElastic);
                    break;

                case ToyReaction.Bounce:
                    sprite.MoveToY(-24, 150, Easing.OutQuint).Then().MoveToY(0, 520, Easing.OutBounce);
                    sprite.RotateTo(clicks % 2 == 0 ? -9 : 9, 160, Easing.OutQuint).Then().RotateTo(0, 420, Easing.OutElastic);
                    break;

                case ToyReaction.Shake:
                    sprite.MoveToX(-10, 55).Then().MoveToX(10, 80).Then().MoveToX(-7, 75).Then().MoveToX(0, 110, Easing.OutQuint);
                    sprite.RotateTo(-5, 55).Then().RotateTo(5, 80).Then().RotateTo(0, 120);
                    break;
            }

            for (int i = 0; i < 7; i++)
            {
                double angle = (i / 7d + clicks * .071) * Math.PI * 2;
                var pearl = new Circle
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = new Vector2(6 + i % 3 * 2),
                    Colour = i % 2 == 0 ? SomsAiOceanTheme.Aqua : SomsAiOceanTheme.Gold,
                    Blending = BlendingParameters.Additive,
                };
                particles.Add(pearl);
                pearl.MoveTo(new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * (45 + i * 5), 520, Easing.OutQuint)
                     .FadeOut(520, Easing.InQuint)
                     .Expire();
            }
        }
    }
}
