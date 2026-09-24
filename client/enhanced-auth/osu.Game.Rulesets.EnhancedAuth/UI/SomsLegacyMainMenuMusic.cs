#nullable enable
using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>Stable's compact transport controls, all connected to the existing lazer music controller.</summary>
public sealed partial class SomsLegacyMainMenuMusic : Container
{
    private MusicController? music;
    private NowPlayingOverlay? nowPlaying;
    private IBindable<WorkingBeatmap>? beatmap;
    private readonly TruncatingSpriteText song;
    private readonly Box progress;

    public SomsLegacyMainMenuMusic()
    {
        Name = "soms-legacy-main-menu-music";
        Size = new Vector2(242, 72);
        Add(new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = 0.45f });
        Add(new OsuSpriteText
        {
            Position = new Vector2(9, 3), Text = "Сейчас играет", Font = SomsLegacyFont.Font(11), Alpha = 0.65f,
        });
        Add(song = new TruncatingSpriteText
        {
            Name = "soms-legacy-music-title", Position = new Vector2(9, 16), Width = 224,
            Font = SomsLegacyFont.Font(16), Shadow = true,
        });
        addButton(0, "previous", FontAwesome.Solid.StepBackward, () => music?.PreviousTrack());
        addButton(1, "play", FontAwesome.Solid.Play, () => music?.Play(requestedByUser: true));
        addButton(2, "pause", FontAwesome.Solid.Pause, () => music?.Stop(requestedByUser: true));
        addButton(3, "stop", FontAwesome.Solid.Stop, () =>
        {
            music?.Stop(requestedByUser: true);
            music?.SeekTo(0);
        });
        addButton(4, "next", FontAwesome.Solid.StepForward, () => music?.NextTrack());
        addButton(5, "info", FontAwesome.Solid.Info, () => nowPlaying?.ToggleVisibility());
        addButton(6, "playlist", FontAwesome.Solid.Bars, () => nowPlaying?.ToggleVisibility());

        Add(new MusicButton
        {
            Name = "soms-legacy-music-progress", Position = new Vector2(8, 62), Size = new Vector2(226, 10),
            Pressed = e =>
            {
                if (music == null || !music.TrackLoaded || e.Target == null) return;
                var progressControl = e.Target;
                float fraction = Math.Clamp(progressControl.ToLocalSpace(e.ScreenSpaceMousePosition).X / progressControl.DrawWidth, 0, 1);
                music.SeekTo(fraction * music.CurrentTrack.Length);
            },
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.X, Height = 4, Colour = Color4.White, Alpha = 0.14f },
                progress = new Box { RelativeSizeAxes = Axes.X, Height = 4, Width = 0, Colour = Color4.White, Alpha = 0.7f },
            },
        });
    }

    [BackgroundDependencyLoader(true)]
    private void load(MusicController? music, NowPlayingOverlay? nowPlaying, IBindable<WorkingBeatmap>? beatmap)
    {
        this.music = music;
        this.nowPlaying = nowPlaying;
        this.beatmap = beatmap?.GetBoundCopy();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        beatmap?.BindValueChanged(_ => updateSong(), true);
        if (beatmap == null) updateSong();
    }

    private void updateSong()
    {
        var metadata = beatmap?.Value.BeatmapInfo.Metadata;
        song.Text = metadata == null ? "—" : $"{metadata.Artist} - {metadata.Title}";
    }

    protected override void Update()
    {
        base.Update();
        progress.Width = music?.CurrentTrack.Length > 0
            ? (float)Math.Clamp(music.CurrentTrack.CurrentTime / music.CurrentTrack.Length, 0, 1) : 0;
    }

    private void addButton(int index, string name, IconUsage icon, Action action)
    {
        Add(new MusicButton
        {
            Name = "soms-legacy-music-" + name, Position = new Vector2(8 + index * 32, 34),
            Size = new Vector2(29, 27), Pressed = _ => action(),
            Child = new SpriteIcon { Anchor = Anchor.Centre, Origin = Anchor.Centre, Size = new Vector2(18), Icon = icon },
        });
    }

    protected override void Dispose(bool isDisposing)
    {
        beatmap?.UnbindAll();
        base.Dispose(isDisposing);
    }

    private sealed partial class MusicButton : Container
    {
        public Action<ClickEvent>? Pressed;
        protected override bool OnClick(ClickEvent e) { Pressed?.Invoke(e); return true; }
        protected override bool OnHover(HoverEvent e) { this.FadeColour(new Color4(247, 169, 208, 255), 100); return true; }
        protected override void OnHoverLost(HoverLostEvent e) => this.FadeColour(Color4.White, 150);
    }
}
