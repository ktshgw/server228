#nullable enable
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

// Reads the shared downloader used by Ctrl+B, including the import phase.
public sealed partial class SomsBeatmapDownloadProgress : Container
{
    private readonly BeatmapDownloadTracker tracker;
    private readonly Container display;
    private readonly ProgressBar progress;
    private readonly OsuSpriteText caption;

    public SomsBeatmapDownloadProgress(int setId)
    {
        tracker = new BeatmapDownloadTracker(new BeatmapSetInfo { OnlineID = setId });
        RelativeSizeAxes = Axes.X;
        Height = 22;
        Anchor = Anchor.BottomLeft;
        Origin = Anchor.BottomLeft;
        Children = new Drawable[]
        {
            tracker,
            display = new Container
            {
                RelativeSizeAxes = Axes.Both, Alpha = 0,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(0, 0, 0, 217) },
                    caption = new OsuSpriteText { X = 9, Y = 1, Font = OsuFont.GetFont(size: 12) },
                    progress = new ProgressBar(false) { Height = 4, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft,
                        FillColour = new Color4(99, 218, 243, 255), BackgroundColour = new Color4(35, 48, 60, 255) },
                },
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        tracker.Progress.BindValueChanged(value =>
        {
            progress.Current.Value = value.NewValue;
            if (tracker.State.Value == DownloadState.Downloading)
                caption.Text = $"Скачивание · {value.NewValue:P0}";
        }, true);
        tracker.State.BindValueChanged(value =>
        {
            if (value.NewValue == DownloadState.Downloading)
            {
                caption.Text = $"Скачивание · {tracker.Progress.Value:P0}";
                display.FadeIn(100);
            }
            else if (value.NewValue == DownloadState.Importing)
            {
                progress.Current.Value = 1;
                caption.Text = "Импорт карты…";
                display.FadeIn(100);
            }
            else
                display.FadeOut(150);
        }, true);
    }
}
