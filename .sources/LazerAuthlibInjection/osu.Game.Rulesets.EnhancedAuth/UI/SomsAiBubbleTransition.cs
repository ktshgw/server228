#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using osu.Game.Input.Bindings;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>A screen-independent curtain: cover the old screen, switch underneath, then reveal the loaded destination.</summary>
internal sealed partial class SomsAiBubbleTransition : Container, IKeyBindingHandler<GlobalAction>
{
    private readonly Action covered;
    private readonly Func<bool> destinationReady;
    private readonly Container[] bubbles = new Container[104];
    private readonly Vector2[] targets = new Vector2[104];
    private readonly float[] diameters = new float[104];
    private readonly List<Container> gapBubbles = new();
    private readonly List<Vector2> gapTargets = new();
    private readonly List<float> gapDiameters = new();
    private Vector2 layoutSize;
    private readonly Box water;
    private double elapsed, releaseAt = -1;
    private bool switched;
    internal const double CoverTime = 620;
    internal const double RevealTime = 780;

    public static void Enter(OsuGame game)
    {
        var previous = game.ScreenStack.CurrentScreen;
        if (previous == null) return;
        var root = SomsLegacyInterfacePatch.Member<Container>(game, "topMostOverlayContent");
        if (root == null) { previous.Push(new SomsAiScreen()); return; }
        if (root.Children.Any(d => d.Name == "somsai-bubble-transition" && d.IsAlive)) return;
        var destination = new SomsAiScreen();
        bool cancelled = false;
        root.Add(new SomsAiBubbleTransition(() =>
        {
            if (!previous.IsCurrentScreen()) { cancelled = true; return; }
            previous.Push(destination);
        }, () => cancelled || destination.IsCurrentScreen()));
    }

    internal SomsAiBubbleTransition(Action covered, Func<bool> destinationReady)
    {
        this.covered = covered; this.destinationReady = destinationReady;
        Name = "somsai-bubble-transition";
        RelativeSizeAxes = Axes.Both; Depth = -10000; Masking = true;
        Add(water = new Box
        {
            Name = "somsai-transition-water", RelativeSizeAxes = Axes.Both, Alpha = 0,
            Colour = ColourInfo.GradientVertical(new Color4(104, 233, 238, 255), new Color4(5, 103, 156, 255)),
        });
        for (int i = 0; i < bubbles.Length; i++)
        {
            bubbles[i] = createBubble("somsai-transition-bubble", -1000 - i);
            Add(bubbles[i]);
        }
    }

    private static Container createBubble(string name, float depth) => new Container
            {
                Name = name, Anchor = Anchor.TopLeft, Origin = Anchor.Centre, Depth = depth,
                Masking = true, CornerRadius = 50, Size = new Vector2(100),
                BorderThickness = 2.5f, BorderColour = new Color4(198, 255, 255, 255),
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = ColourInfo.GradientVertical(new Color4(144, 242, 238, 255), new Color4(29, 147, 199, 255)),
                    },
                    new Circle { Position = new Vector2(19, 15), Size = new Vector2(30, 12), Rotation = -38, Colour = new Color4(240, 255, 249, 235) },
                    new Circle { Position = new Vector2(16, 37), Size = new Vector2(7, 13), Rotation = 14, Colour = new Color4(236, 255, 250, 195) },
                    new Circle { Position = new Vector2(69, 76), Size = new Vector2(13, 5), Rotation = -40, Colour = new Color4(195, 208, 255, 195) },
                },
            };

    protected override void Update()
    {
        base.Update();
        if (DrawWidth <= 0 || DrawHeight <= 0) return;
        if (layoutSize != DrawSize) rebuildLayout();
        elapsed += Math.Min(Time.Elapsed, 50);
        if (!switched && elapsed >= CoverTime)
        {
            // The opaque water layer guarantees no uncovered gap when the destination is pushed.
            water.Alpha = 1;
            switched = true; covered();
        }
        if (switched && releaseAt < 0 && elapsed >= CoverTime + 140 && (destinationReady() || elapsed > 12000)) releaseAt = elapsed;
        double reveal = releaseAt < 0 ? 0 : Math.Clamp((elapsed - releaseAt) / RevealTime, 0, 1);
        water.Alpha = releaseAt < 0 ? (float)Math.Clamp((elapsed - CoverTime + 220) / 220, 0, 1) : (float)Math.Max(0, 1 - reveal * 2.6);
        for (int i = 0; i < bubbles.Length; i++)
            animate(bubbles[i], targets[i], diameters[i], i, reveal, true);
        for (int i = 0; i < gapTargets.Count; i++)
            animate(gapBubbles[i], gapTargets[i], gapDiameters[i], i + bubbles.Length, reveal, false);
        if (reveal >= 1) Expire();
    }

    private void animate(Container bubble, Vector2 target, float diameter, int index, double reveal, bool original)
    {
        double progress = Math.Clamp((elapsed - index * 37 % 160) / (CoverTime - 160), 0, 1);
        double rise = 1 - Math.Pow(1 - progress, 3);
        float startY = DrawHeight + diameter + index / 13 * 13;
        float y = startY + (target.Y - startY) * (float)rise;
        double departure = Math.Clamp(reveal * 1.4 - (index * 13 % 17) / 70f, 0, 1);
        y -= (DrawHeight + diameter * 2) * (float)(departure * departure);
        float x = target.X;
        // Preserve the original arrival sway, then keep the covered layout intact while loading.
        if (original)
            x += (float)(Math.Sin(Math.Min(elapsed, CoverTime) / 180 + index) - Math.Sin(CoverTime / 180 + index)) * diameter * .07f;
        bubble.Scale = new Vector2(diameter / 100);
        bubble.Position = new Vector2(x, y);
        bubble.Alpha = (float)Math.Min(1, progress * 5) * (float)Math.Min(1, (1 - reveal) * 5);
    }

    private void rebuildLayout()
    {
        layoutSize = DrawSize;
        float unit = Math.Max(DrawWidth / 10, DrawHeight / 6);
        for (int i = 0; i < bubbles.Length; i++)
        {
            // Original 104 circles, including their full 0.75–1.5 size variation and loose placement.
            float diameter = diameters[i] = unit * (.75f + (i * 17 % 31) / 40f);
            targets[i] = new Vector2(
                (i % 13 - .45f + (i * 19 % 29) / 29f) / 12 * DrawWidth + (float)Math.Sin(CoverTime / 180 + i) * diameter * .07f,
                (i / 13 - .45f + (i * 11 % 23) / 23f) / 7 * DrawHeight);
        }

        gapTargets.Clear(); gapDiameters.Clear();
        int columns = (int)Math.Ceiling(DrawWidth / (unit / 12));
        int rows = (int)Math.Ceiling(DrawHeight / (unit / 12));
        var probes = new Vector2[(columns + 1) * (rows + 1)];
        var clearance = new float[probes.Length];
        // Shrink coverage by half a probe cell: covering its vertices then covers the entire cell.
        float margin = new Vector2(DrawWidth / columns, DrawHeight / rows).Length / 2 + 1;
        for (int n = 0; n < probes.Length; n++)
        {
            probes[n] = new Vector2(n % (columns + 1) * DrawWidth / columns, n / (columns + 1) * DrawHeight / rows);
            clearance[n] = float.MaxValue;
            for (int i = 0; i < bubbles.Length; i++)
                clearance[n] = Math.Min(clearance[n], (probes[n] - targets[i]).Length - diameters[i] / 2 + margin);
        }
        // Add a small circle only at an uncovered location. Keep every filler behind the originals.
        for (int i = 0; i < 192; i++)
        {
            int widest = 0;
            for (int n = 1; n < probes.Length; n++)
                if (clearance[n] > clearance[widest]) widest = n;
            if (clearance[widest] <= 0) break;
            var position = probes[widest];
            float diameter = unit * (.65f + (i * 23 % 26) / 100f);
            gapTargets.Add(position); gapDiameters.Add(diameter);
            if (i >= gapBubbles.Count)
            {
                var bubble = createBubble("somsai-transition-gap-bubble", -1 - i);
                gapBubbles.Add(bubble); Add(bubble);
            }
            for (int n = 0; n < probes.Length; n++)
                clearance[n] = Math.Min(clearance[n], (probes[n] - position).Length - diameter / 2 + margin);
        }
        for (int i = gapTargets.Count; i < gapBubbles.Count; i++) gapBubbles[i].Hide();
    }

    protected override bool OnMouseDown(MouseDownEvent e) => true;
    protected override bool OnClick(ClickEvent e) => true;
    protected override bool OnScroll(ScrollEvent e) => true;
    protected override bool OnKeyDown(KeyDownEvent e) => true;
    public bool OnPressed(KeyBindingPressEvent<GlobalAction> e) => true;
    public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e) { }
}
