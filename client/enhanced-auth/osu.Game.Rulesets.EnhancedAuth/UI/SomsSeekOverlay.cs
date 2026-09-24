#nullable enable
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using osu.Game.Graphics;
using osu.Game.Graphics.Cursor;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.Objects;
using osu.Game.Screens.Play;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public partial class SomsSeekOverlay : CompositeDrawable, IProvideCursor
{
    private readonly Player player;
    private bool held;
    private Container cursorLayer = null!;
    private MenuCursorContainer seekCursor = null!;
    public bool ProvidingUserCursor => held;
    public CursorContainer Cursor => seekCursor;
    public bool Used { get; private set; }
    private Container timeline = null!;
    private Box progress = null!;
    private OsuSpriteText time = null!;
    private OsuSpriteText practice = null!;
    private static readonly PropertyInfo clockProperty = AccessTools.Property(typeof(Player), "GameplayClockContainer");
    private static readonly MethodInfo seek = AccessTools.Method(typeof(Player), "SetGameplayStartTime");
    private GameplayClockContainer? ClockContainer => clockProperty.GetValue(player) as GameplayClockContainer;
    public SomsSeekOverlay(Player player) { this.player = player; RelativeSizeAxes = Axes.Both; AlwaysPresent = true; }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChildren = new Drawable[]
        {
            // Native menu cursor and cursor-size setting, above the timeline and
            // pause overlay. GlobalCursorDisplay hides the gameplay cursor while
            // this provider is active and restores it on release.
            cursorLayer = new Container
            {
                Name = "soms-seek-cursor-layer", RelativeSizeAxes = Axes.Both,
                Depth = float.MinValue, Alpha = 0, AlwaysPresent = true,
                Child = seekCursor = new MenuCursorContainer { Depth = float.MinValue, HideCursorOnNonMouseInput = false },
            },
            practice = new OsuSpriteText { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Y = 15,
                Text = "Тренировка · результат не сохраняется и не отправляется", Font = OsuFont.GetFont(size: 15), Colour = Color4.Yellow, Alpha = 0 },
            timeline = new Container { Anchor = Anchor.BottomCentre, Origin = Anchor.BottomCentre, RelativeSizeAxes = Axes.X,
                Width = .85f, Height = 92, Y = -70, Masking = true, CornerRadius = 10, Alpha = 0,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(10, 10, 20, 235) },
                    new OsuSpriteText { Position = new Vector2(16, 10), Text = "Перемотка · нажмите или проведите по шкале", Font = OsuFont.GetFont(size: 17) },
                    time = new OsuSpriteText { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Position = new Vector2(-16, 10), Font = OsuFont.GetFont(size: 17) },
                    new Container { RelativeSizeAxes = Axes.X, Height = 22, Y = 47, Padding = new MarginPadding { Horizontal = 16 }, Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(255, 255, 255, 50) },
                        progress = new Box { RelativeSizeAxes = Axes.Both, Width = 0, Colour = Color4.MediumPurple },
                    } },
                } },
        };
        seekCursor.Hide();
    }

    public void SetHeld(bool value)
    {
        held = value && player.IsCurrentScreen() && player.LoadedBeatmapSuccessfully && !player.GameplayState.HasCompleted;
    }

    protected override void Update()
    {
        base.Update();
        if (!player.IsCurrentScreen() || player.GameplayState?.HasCompleted != false) held = false;
        timeline.Alpha = held ? 1 : 0;
        // MenuCursorContainer can have State=Hidden but Alpha=1 on its first
        // load (no visible->hidden transition). Gate its entire draw subtree,
        // including first load and fade-out, independently of native state.
        cursorLayer.Alpha = held ? 1 : 0;
        if (held) seekCursor.Show();
        else seekCursor.Hide();
        practice.Alpha = Used ? 1 : 0;
        if (!held || ClockContainer is not { } clock) return;
        var (start, end) = bounds();
        progress.Width = (float)Math.Clamp((clock.CurrentTime - start) / Math.Max(1, end - start), 0, 1);
        time.Text = $"{TimeSpan.FromMilliseconds(Math.Max(0, clock.CurrentTime)):m\\:ss} / {TimeSpan.FromMilliseconds(Math.Max(0, end)):m\\:ss}";
    }

    private (double Start, double End) bounds()
    {
        var objects = player.GameplayState.Beatmap.HitObjects;
        return (Math.Min(0, objects.First().StartTime - 1000), objects.Max(item => item.GetEndTime()));
    }

    protected override bool OnMouseDown(MouseDownEvent e)
    {
        if (!held) return false;
        if (e.Button == MouseButton.Left && timeline.ReceivePositionalInputAt(e.ScreenSpaceMousePosition)) scrub(e.ScreenSpaceMousePosition);
        return true;
    }
    protected override bool OnDragStart(DragStartEvent e) => held;
    protected override void OnDrag(DragEvent e)
    {
        if (held && e.Button == MouseButton.Left) scrub(e.ScreenSpaceMousePosition);
    }
    protected override bool OnClick(ClickEvent e) => held;

    public void SeekToFraction(double fraction)
    {
        if (!player.IsCurrentScreen() || !player.LoadedBeatmapSuccessfully || player.GameplayState.HasCompleted || ClockContainer is not { } clock) return;
        var (start, end) = bounds();
        Used = true;
        bool running = clock.IsRunning;
        // Match the native replay start-time jump: skip intermediate rendering and
        // return to frame-stable playback on the next update. Never submit this attempt.
        seek.Invoke(player, new object[] { Math.Clamp(start + (end - start) * Math.Clamp(fraction, 0, 1), start, end - 1) });
        player.GameplayState.HealthProcessor.Health.Value = 1;
        if (running) clock.Start();
    }

    private void scrub(Vector2 screenPosition)
    {
        var local = timeline.ToLocalSpace(screenPosition);
        SeekToFraction((local.X - 16) / Math.Max(1, timeline.DrawWidth - 32));
    }
}
