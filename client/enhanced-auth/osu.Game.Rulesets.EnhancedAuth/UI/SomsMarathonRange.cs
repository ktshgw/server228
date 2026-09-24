#nullable enable
using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>Two draggable boundaries on a shared song timeline, in milliseconds.</summary>
public sealed partial class SomsMarathonRange : Container
{
    public const int Minimum = 5000, Maximum = 90000;
    public int Start { get; private set; }
    public int End { get; private set; }
    public readonly int Duration;
    public Action? Changed;
    private readonly Box selection, playhead;
    private readonly Container left, right;
    private bool movingEnd;
    public double? PreviewTime;

    public SomsMarathonRange(int duration, int start, int end)
    {
        Duration = Math.Max(Minimum, duration);
        RelativeSizeAxes = Axes.X; Height = 72;
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.X, Height = 14, Y = 28, Colour = new Color4(59, 54, 69, 255) },
            selection = new Box { Height = 14, Y = 28, Colour = new Color4(86, 206, 195, 255) },
            left = handle(), right = handle(),
            playhead = new Box { Width = 2, Height = 46, Y = 12, Colour = Color4.White, Alpha = 0 },
        };
        Start = Math.Clamp(start, 0, Duration - Minimum);
        End = Math.Clamp(end, Start + Minimum, Math.Min(Duration, Start + Maximum));
    }

    private static Container handle() => new()
    {
        Size = new Vector2(18, 44), Origin = Anchor.Centre, Y = 35, Masking = true, CornerRadius = 6,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(170, 249, 106, 255) },
            new Box { Width = 2, Height = 18, Anchor = Anchor.Centre, Origin = Anchor.Centre, Colour = new Color4(40, 61, 47, 255) },
        },
    };

    public void SetBoundary(bool end, int value)
    {
        if (end) End = Math.Clamp(value, Start + Minimum, Math.Min(Duration, Start + Maximum));
        else Start = Math.Clamp(value, Math.Max(0, End - Maximum), End - Minimum);
        Changed?.Invoke();
    }

    protected override void Update()
    {
        base.Update();
        float width = Math.Max(1, DrawWidth - 20);
        left.X = 10 + width * Start / Duration; right.X = 10 + width * End / Duration;
        selection.X = left.X; selection.Width = Math.Max(0, right.X - left.X);
        playhead.Alpha = PreviewTime.HasValue ? 1 : 0;
        if (PreviewTime.HasValue) playhead.X = 10 + width * (float)Math.Clamp(PreviewTime.Value / Duration, 0, 1);
    }

    private void move(Vector2 position)
    {
        float x = ToLocalSpace(position).X;
        SetBoundary(movingEnd, (int)Math.Round(Math.Clamp((x - 10) / Math.Max(1, DrawWidth - 20), 0, 1) * Duration / 10) * 10);
    }
    protected override bool OnMouseDown(MouseDownEvent e)
    {
        if (e.Button != MouseButton.Left) return false;
        float x = ToLocalSpace(e.ScreenSpaceMousePosition).X;
        movingEnd = Math.Abs(x - right.X) < Math.Abs(x - left.X);
        move(e.ScreenSpaceMousePosition); return true;
    }
    protected override bool OnDragStart(DragStartEvent e) => e.Button == MouseButton.Left;
    protected override void OnDrag(DragEvent e) => move(e.ScreenSpaceMousePosition);
}
