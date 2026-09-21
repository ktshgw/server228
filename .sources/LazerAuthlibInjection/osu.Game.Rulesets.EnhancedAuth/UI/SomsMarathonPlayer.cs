#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.EnhancedAuth.Beatmaps;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Screens.Ranking;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public sealed record SomsMarathonResult(long Score, double Accuracy, int Combo);

// The normal Player handles input, scoring, pauses and skins. Online score import is deliberately absent.
public sealed partial class SomsMarathonPlayer : Player
{
    [Cached(typeof(IGameplayLeaderboardProvider))]
    private readonly EmptyGameplayLeaderboardProvider leaderboard = new();
    private readonly IReadOnlyList<(double Time, string Title)> songs;
    private readonly Func<SomsMarathonResult, Task<string>> completed;
    private readonly SomsMarathonWorkingBeatmap compilation;
    private readonly TruncatingSpriteText song = new()
    {
        Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Y = 48,
        RelativeSizeAxes = Axes.X, Width = .7f,
        Font = OsuFont.GetFont(size: 18), Shadow = true,
    };
    private int currentSong = -1;

    public SomsMarathonPlayer(SomsMarathonWorkingBeatmap compilation, IReadOnlyList<(double Time, string Title)> songs, Func<SomsMarathonResult, Task<string>> completed)
        : base(new PlayerConfiguration { ShowResults = true, AllowRestart = false, ShowLeaderboard = false, ShowFailingOverlay = false })
        => (this.compilation, this.songs, this.completed) = (compilation, songs, completed);

    protected override void Dispose(bool isDisposing) { base.Dispose(isDisposing); compilation.Dispose(); }

    [BackgroundDependencyLoader]
    private void load() { if (LoadedBeatmapSuccessfully) AddInternal(song); }
    protected override bool CheckModsAllowFailure() => false;
    protected override Task ImportScore(Score score) => Task.CompletedTask;
    protected override ResultsScreen CreateResults(ScoreInfo score)
    {
        score.PP = null;
        var result = new SomsMarathonResult(score.TotalScore, score.Accuracy, score.MaxCombo);
        return new SomsMarathonResultsScreen(score, () => completed(result));
    }

    protected override void UpdateAfterChildren()
    {
        base.UpdateAfterChildren();
        if (!LoadedBeatmapSuccessfully || !this.IsCurrentScreen()) return;
        int next = 0;
        for (int i = 0; i < songs.Count; i++) if (GameplayClockContainer.CurrentTime >= songs[i].Time) next = i;
        if (next != currentSong)
        {
            currentSong = next;
            song.Text = $"{next + 1}/{songs.Count} · {songs[next].Title}";
            song.FadeIn(400).Delay(4500).FadeOut(700);
        }
    }
}

public sealed partial class SomsMarathonResultsScreen : ResultsScreen
{
    public override string Title => "Марафон · Результат";
    private readonly Func<Task<string>> submit;
    private readonly OsuSpriteText status = new()
    {
        Text = "Сохраняем результат в таблице марафона…", Font = OsuFont.GetFont(size: 18), Shadow = true,
        Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Y = 18,
    };
    private bool submitting;
    private osu.Game.Graphics.UserInterface.ShearedButton retry = null!;

    public SomsMarathonResultsScreen(ScoreInfo score, Func<Task<string>> submit) : base(score)
    {
        this.submit = submit;
        AllowRetry = false; AllowWatchingReplay = false; IsLocalPlay = true;
    }
    protected override void LoadComplete()
    {
        base.LoadComplete(); AddInternal(status);
        AddInternal(retry = new osu.Game.Graphics.UserInterface.ShearedButton
        {
            Text = "Повторить сохранение", Width = 230, Height = 36,
            Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Position = new osuTK.Vector2(-15, 48),
            Action = save, Alpha = 0,
        });
        save();
    }
    private async void save()
    {
        if (submitting) return;
        submitting = true;
        try { string text = await submit(); Schedule(() => { status.Text = text; retry.Hide(); }); }
        catch (Exception e) { Schedule(() => { status.Text = "Не удалось сохранить: " + e.GetBaseException().Message; retry.Show(); }); }
        finally { submitting = false; }
    }
}
