#nullable enable
using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Screens.Select;
using osuTK;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public sealed partial class SomsAiBotScreen : OsuScreen
{
    public override string Title => "SOMSAI · 1v1 с ботом";
    public override bool ShowFooter => true;
    [Cached] private readonly OverlayColourProvider colours = new(190);
    private readonly FillFlowContainer body = SomsNativeMatchScreen.Flow();
    private readonly FillFlowContainer resultPanel = SomsNativeMatchScreen.Flow();
    private readonly FormDropdown<string> difficulty = new()
    {
        Caption = "Сложность соперника", Items = SomsAiBotSimulation.Labels,
        Current = { Value = SomsAiBotSimulation.Labels[1] },
    };
    private readonly TextFlowContainer mapText = text("Выберите карту и моды", 21);
    private readonly TextFlowContainer opponent = text("", 24);
    private readonly TextFlowContainer status = text("", 18);
    private bool playing;
    private int wins, losses;
    private SomsAiBotLevel level => SomsAiBotSimulation.SelectableLevels[Math.Max(0, Array.IndexOf(SomsAiBotSimulation.Labels, difficulty.Current.Value))];

    [BackgroundDependencyLoader]
    private void load()
    {
        body.Spacing = new Vector2(0, 14);
        body.Add(new SomsAiOceanHeader(true, "ТРЕНИРОВКА · 1v1 С БОТОМ"));
        body.Add(text("Дуэль без изменения MMR и без отправки результатов в рейтинги.", 20));
        body.Add(difficulty);
        body.Add(opponent);
        body.Add(text("Digit — условный уровень игры. Реальные результаты зависят от карты и модов.", 17));
        body.Add(mapText);
        body.Add(button("Выбрать карту и моды", chooseMap));
        body.Add(button("Начать дуэль 1v1", start, true));
        body.Add(status);
        body.Add(resultPanel);
        InternalChild = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                new SomsAiOceanBackdrop(() => this.IsCurrentScreen()),
                new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Horizontal = 65, Top = 20, Bottom = 85 },
                    Child = new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = new Container
                        {
                            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Masking = true, CornerRadius = 18,
                            Children = new Drawable[]
                            {
                                new Box { RelativeSizeAxes = Axes.Both, Colour = SomsAiOceanTheme.Ink, Alpha = .9f },
                                new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding(24), Child = body },
                            },
                        },
                    },
                },
            },
        };
        difficulty.Current.BindValueChanged(_ =>
        {
            wins = losses = 0;
            resultPanel.Clear();
            opponent.Text = SomsAiBotSimulation.NameFor(level) + " · BOT";
            status.Text = level == SomsAiBotLevel.Mrekk
                ? "mrekk всегда держит FC. Победитель определяется по точности."
                : "Победитель определяется по счёту. Можно доиграть даже с промахами.";
        }, true);
        renderMap();
    }

    private void chooseMap()
    {
        if (!playing) this.Push(new BotSongSelect());
    }

    private void renderMap()
    {
        var b = Beatmap.Value.BeatmapInfo;
        mapText.Text = Beatmap.Value is DummyWorkingBeatmap || b.ID == Guid.Empty ? "Выберите карту и моды" :
            $"{b.Metadata.Artist} — {b.Metadata.Title}\n[{b.DifficultyName}] · {Ruleset.Value.ShortName} · " +
            (Mods.Value.Count == 0 ? "NM" : string.Join(" ", Mods.Value.Select(m => m.Acronym)));
    }

    private void start()
    {
        if (playing || !this.IsCurrentScreen()) return;
        if (Beatmap.Value is DummyWorkingBeatmap || Beatmap.Value.BeatmapInfo.ID == Guid.Empty)
        {
            status.Text = "Сначала выберите установленную карту.";
            return;
        }
        if (Mods.Value.Any(m => m is ICreateReplayData))
        {
            status.Text = "Отключите Autoplay перед дуэлью с ботом.";
            return;
        }
        playing = true;
        resultPanel.Clear();
        int seed = Random.Shared.Next();
        SomsAiBotLevel selected = level;
        this.Push(new PlayerLoader(() => new SomsAiBotPlayer(selected, seed, result =>
        {
            if (!playing) return;
            if (result.Winner > 0) wins++;
            else if (result.Winner < 0) losses++;
            resultPanel.Add(text(result.Winner > 0 ? "Победа!" : result.Winner < 0 ? "Бот победил" : "Ничья", 30));
            resultPanel.Add(text($"Вы: {result.PlayerScore:N0} · {result.PlayerAccuracy:P2} · {result.PlayerCombo}x", 22));
            resultPanel.Add(text($"{SomsAiBotSimulation.NameFor(selected)}: {result.BotScore:N0} · {result.BotAccuracy:P2} · {result.BotCombo}x", 22));
            resultPanel.Add(text($"Счёт встреч: {wins} : {losses}", 21));
            this.MakeCurrent();
        })));
    }

    public override void OnResuming(ScreenTransitionEvent e)
    {
        base.OnResuming(e);
        playing = false;
        if (e.Last is BotSongSelect { Chosen: true } picker)
        {
            Beatmap.Value = picker.SelectedBeatmap!;
            Ruleset.Value = picker.SelectedRuleset!;
            Mods.Value = picker.SelectedMods;
        }
        renderMap();
    }

    private static TextFlowContainer text(string value, float size) => new(t =>
    {
        t.Font = OsuFont.GetFont(size: size);
        t.Colour = SomsAiOceanTheme.Cream;
    })
    {
        RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = value,
    };
    private static SomsAiOceanButton button(string label, Action action, bool primary = false) => new(primary) { Text = label, Action = action };

    private sealed partial class BotSongSelect : SoloSongSelect
    {
        public bool Chosen { get; private set; }
        public WorkingBeatmap? SelectedBeatmap { get; private set; }
        public RulesetInfo? SelectedRuleset { get; private set; }
        public Mod[] SelectedMods { get; private set; } = Array.Empty<Mod>();
        protected override void OnStart()
        {
            if (!this.IsCurrentScreen()) return;
            Chosen = true;
            SelectedBeatmap = Beatmap.Value;
            SelectedRuleset = Ruleset.Value;
            SelectedMods = Mods.Value.ToArray();
            this.Exit();
        }
    }
}
