#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens;
using osu.Game.Screens.OnlinePlay.Matchmaking.RankedPlay.Intro;
using osu.Game.Screens.Play;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public sealed partial class SomsReplayDuelScreen : OsuScreen
{
    public override string Title => "1 на 1 с реплеем";
    public override bool ShowFooter => true;
    public override bool HideOverlaysOnEnter => true;
    public override bool DisallowExternalBeatmapRulesetChanges => true;
    public override bool? AllowGlobalTrackControl => false;
    [Cached] private readonly OverlayColourProvider colours = new(OverlayColourScheme.Purple);
    [Resolved] private IAPIProvider api { get; set; } = null!;
    [Resolved] private ScoreManager scores { get; set; } = null!;
    [Resolved] private BeatmapManager beatmaps { get; set; } = null!;
    [Resolved] private RulesetStore rulesets { get; set; } = null!;
    [Resolved] private AudioManager audio { get; set; } = null!;
    private readonly ScoreInfo selected;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextFlowContainer status = new(t => t.Font = OsuFont.GetFont(size: 24))
    {
        RelativeSizeAxes = Axes.X, Width = .8f, AutoSizeAxes = Axes.Y, Text = "Загрузка реплея…",
    };
    private readonly Container content = new() { RelativeSizeAxes = Axes.Both };
    private Score? replay;
    private APIUser localUser = null!;
    private APIUser opponentUser = null!;
    private bool closed, playing;
    private VsSequence? intro;

    public SomsReplayDuelScreen(ScoreInfo selected) => this.selected = selected;

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChildren = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(18, 20, 32, 255) },
            content,
        };
        status.Anchor = Anchor.Centre;
        status.Origin = Anchor.Centre;
        content.Add(status);
    }

    public override void OnEntering(ScreenTransitionEvent e)
    {
        base.OnEntering(e);
        _ = prepare();
    }

    private async Task prepare()
    {
        try
        {
            var loaded = await Task.Run(() => scores.GetScore(selected)).ConfigureAwait(false);
            if (loaded?.Replay == null)
            {
                if (selected.OnlineID <= 0) throw new InvalidOperationException("У этого результата нет доступного реплея.");
                loaded = await downloadReplay().ConfigureAwait(false);
            }
            ValidateReplay(loaded, selected);
            lifetime.Token.ThrowIfCancellationRequested();
            var users = await Task.WhenAll(getUser(api.LocalUser.Value), getUser(selected.User)).ConfigureAwait(false);
            Schedule(() =>
            {
                if (closed || !this.IsCurrentScreen()) return;
                replay = loaded;
                localUser = users[0];
                opponentUser = users[1];
                replay.ScoreInfo.User = opponentUser;
                Beatmap.Value = beatmaps.GetWorkingBeatmap(replay.ScoreInfo.BeatmapInfo!);
                Ruleset.Value = replay.ScoreInfo.Ruleset;
                Mods.Value = replay.ScoreInfo.Mods.Select(m => m.DeepClone()).ToArray();
                playIntro();
            });
        }
        catch (OperationCanceledException)
        {
            Schedule(() => { if (!closed) status.Text = "Время загрузки истекло. Вернитесь к карте и попробуйте ещё раз."; });
        }
        catch (Exception exception)
        {
            Schedule(() =>
            {
                if (!closed) status.Text = exception is LegacyScoreDecoder.BeatmapNotFoundException
                    ? "Нужна та версия карты, на которой записан реплей. Обновите или импортируйте карту."
                    : "Не удалось начать дуэль: " + exception.GetBaseException().Message;
            });
        }
    }

    public static void ValidateReplay(Score score, ScoreInfo selected)
    {
        if (score.Replay?.Frames.Count is not > 0) throw new InvalidOperationException("Реплей пуст или недоступен.");
        if (!score.ScoreInfo.Passed) throw new InvalidOperationException("Для дуэли нужен реплей пройденной карты.");
        if (score.ScoreInfo.BeatmapInfo == null) throw new InvalidOperationException("Карта для реплея не установлена.");
        if (score.ScoreInfo.Ruleset.OnlineID != selected.Ruleset.OnlineID)
            throw new InvalidOperationException("Режим игры в реплее отличается от выбранного результата.");
        if (selected.BeatmapInfo?.OnlineID > 0 && selected.BeatmapInfo.OnlineID != score.ScoreInfo.BeatmapInfo.OnlineID)
            throw new InvalidOperationException("Реплей записан на другой карте.");
        if (!string.IsNullOrEmpty(selected.BeatmapInfo?.MD5Hash)
            && !string.Equals(selected.BeatmapInfo.MD5Hash, score.ScoreInfo.BeatmapInfo.MD5Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Версия карты отличается от выбранного результата.");
        if (score.ScoreInfo.Mods.Any(m => m is UnknownMod || m is ICreateReplayData))
            throw new InvalidOperationException("Реплей содержит неподдерживаемые моды или Autoplay.");
    }

    private async Task<Score> downloadReplay()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var result = new TaskCompletionSource<Score>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new DownloadReplayRequest(selected);
        request.Success += filename =>
        {
            _ = Task.Run(() =>
            {
                try
                {
                    using var file = File.OpenRead(filename);
                    result.TrySetResult(new DatabasedLegacyScoreDecoder(rulesets, beatmaps).Parse(file));
                }
                catch (Exception error) { result.TrySetException(error); }
                finally { try { File.Delete(filename); } catch (IOException) { } }
            });
        };
        request.Failure += error => result.TrySetException(error);
        using var cancellation = timeout.Token.Register(() => { result.TrySetCanceled(timeout.Token); request.Cancel(); });
        api.Queue(request);
        return await result.Task.ConfigureAwait(false);
    }

    private async Task<APIUser> getUser(APIUser fallback)
    {
        if (fallback.Id <= 0) return fallback;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var result = new TaskCompletionSource<APIUser>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new GetUserRequest(fallback.Id, selected.Ruleset);
        request.Success += user => result.TrySetResult(user);
        request.Failure += _ => result.TrySetResult(fallback);
        using var cancellation = timeout.Token.Register(() => { result.TrySetResult(fallback); request.Cancel(); });
        api.Queue(request);
        return await result.Task.ConfigureAwait(false);
    }

    private void playIntro()
    {
        if (closed || replay == null || !this.IsCurrentScreen()) return;
        content.Clear();
        intro = new VsSequence(new UserWithRating(localUser, 0), new UserWithRating(opponentUser, 0));
        intro.OnLoadComplete += _ =>
        {
            // Reuse native ranked animation, changing only its two rating labels.
            var ratingLabels = descendants(intro).OfType<OsuSpriteText>().Where(t => t.Text.ToString().StartsWith("Rating:")).ToArray();
            if (ratingLabels.Length == 2)
            {
                ratingLabels[0].Text = PerformanceLabel(localUser);
                ratingLabels[1].Text = PerformanceLabel(opponentUser);
            }
            double delay = 0;
            intro.Play(ref delay, out double impact);
            var windup = audio.Samples.Get("Multiplayer/Matchmaking/Ranked/vs-windup");
            Scheduler.AddDelayed(() => { if (!closed && this.IsCurrentScreen()) windup?.Play(); }, Math.Max(0, impact - (windup?.Length ?? 0)));
            Scheduler.AddDelayed(() => { if (!closed && this.IsCurrentScreen()) audio.Samples.Get("Multiplayer/Matchmaking/Ranked/vs-impact")?.Play(); }, impact);
            Scheduler.AddDelayed(startGameplay, delay);
        };
        content.Add(intro);
        content.Add(new OsuSpriteText
        {
            Text = "1 НА 1 С РЕПЛЕЕМ", Font = OsuFont.GetFont(size: 22, weight: FontWeight.Bold),
            Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Y = 20,
        });
    }

    public static string PerformanceLabel(APIUser user) => user.Statistics?.PP is { } pp
        ? $"{pp:N0} PP" : "— PP";

    private void startGameplay()
    {
        if (closed || playing || replay == null || !this.IsCurrentScreen()) return;
        playing = true;
        // Returning from gameplay/results goes straight back to song selection.
        ValidForResume = false;
        this.Push(new PlayerLoader(() => new SomsReplayDuelPlayer(replay, localUser)));
    }

    public override bool OnExiting(ScreenExitEvent e)
    {
        closed = true;
        lifetime.Cancel();
        return base.OnExiting(e);
    }

    protected override void Dispose(bool isDisposing)
    {
        closed = true;
        if (isDisposing) lifetime.Cancel();
        base.Dispose(isDisposing);
    }

    private static IEnumerable<Drawable> descendants(CompositeDrawable parent)
    {
        foreach (var child in SomsLegacyInterfacePatch.Children(parent))
        {
            yield return child;
            if (child is CompositeDrawable composite)
                foreach (var nested in descendants(composite)) yield return nested;
        }
    }
}
