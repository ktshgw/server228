#nullable enable
using System;
using System.Linq;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Screens;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.Rooms;
using osu.Game.Online.Chat;
using osu.Game.Overlays.Chat;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Screens.OnlinePlay.Match.Components;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public partial class SomsAiScreen
{
    private static readonly Color4 arenaInk = new(10, 16, 27, 255);
    private static readonly Color4 arenaMuted = new(151, 170, 191, 255);
    private readonly Container arenaTeams = new() { RelativeSizeAxes = Axes.Both };
    private readonly FillFlowContainer arenaRoster = Flow();
    private readonly Room arenaChatRoom = new();
    private MatchChatDisplay arenaChat = null!;
    private ArenaWorkspace arenaWorkspace = null!;
    private ArenaActionDock arenaDock = null!;
    private ArenaButton arenaPrimary = null!;
    private OsuSpriteText arenaScore = null!;
    private OsuSpriteText arenaFormat = null!;
    private OsuSpriteText arenaStage = null!;
    private OsuSpriteText arenaClock = null!;
    private TruncatingSpriteText arenaPool = null!;
    private TruncatingSpriteText arenaHint = null!;
    private TruncatingSpriteText arenaMapName = null!;
    private TruncatingSpriteText arenaMapDifficulty = null!;
    private OsuTextFlowContainer arenaMapStats = null!;
    private Container arenaMapCover = null!;
    private string? arenaCoverKey;
    private string? inspectedSlotId;
    private string? arenaStageKey;
    private Container[] arenaPages = Array.Empty<Container>();
    private ArenaButton[] arenaTabs = Array.Empty<ArenaButton>();
    private double nextArenaClockUpdate;

    private void buildArenaLayout()
    {
        mapBoard.Spacing = new Vector2(0, 4);
        Body.Spacing = new Vector2(0, 12);
        Body.Add(matchPanel);
        Body.Add(mapBoard);
        var rosterScroll = new OsuScrollContainer { RelativeSizeAxes = Axes.Both, Child = arenaRoster };
        var historyScroll = new OsuScrollContainer { RelativeSizeAxes = Axes.Both, Child = battleFooter };
        arenaChat = new ArenaChatDisplay(arenaChatRoom) { RelativeSizeAxes = Axes.Both };
        arenaPages = new[]
        {
            new Container { RelativeSizeAxes = Axes.Both, Child = rosterScroll },
            new Container { RelativeSizeAxes = Axes.Both, Child = arenaChat, Alpha = 0 },
            new Container { RelativeSizeAxes = Axes.Both, Child = historyScroll, Alpha = 0 },
        };
        var tabRow = new Container { RelativeSizeAxes = Axes.X, Height = 30, Y = 132 };
        arenaTabs = new[] { "Игроки", "Чат", "Раунды" }.Select((caption, index) =>
        {
            var button = new ArenaButton { Text = caption, Height = 30, RelativeSizeAxes = Axes.X, Width = 1f / 3,
                RelativePositionAxes = Axes.X, X = index / 3f, Action = () => selectArenaTab(index) };
            tabRow.Add(button);
            return button;
        }).ToArray();
        var sidebar = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = arenaInk.Opacity(.94f) },
                new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding(12),
                    Children = new Drawable[]
                    {
                        arenaMapCover = new Container { RelativeSizeAxes = Axes.X, Height = 120, Masking = true, CornerRadius = 4 },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X, Height = 120, Padding = new MarginPadding(10),
                            Children = new Drawable[]
                            {
                                arenaLabel("ВЫБРАННАЯ КАРТА", 11, arenaMuted),
                                new OsuClickableContainer { RelativeSizeAxes = Axes.X, Height = 24, Y = 21,
                                    Action = () => { var slot = state.Match?.Slots.FirstOrDefault(s => s.Id == inspectedSlotId) ?? state.Match?.Slots.FirstOrDefault(s => s.Id == SelectedSlotId(state.Match)); if (slot != null) openMap(slot); },
                                    Child = arenaMapName = arenaLine("Выберите карту из пула", 17, 0) },
                                arenaMapDifficulty = arenaLine("", 12, 44),
                                arenaMapStats = new OsuTextFlowContainer(t => t.Font = OsuFont.GetFont(size: 13))
                                { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Y = 64, Colour = Color4.White },
                            },
                        },
                        tabRow,
                        new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 174 }, Children = arenaPages },
                    },
                },
            },
        };
        oceanScroll = new OsuScrollContainer { RelativeSizeAxes = Axes.Both, Child = Body };
        var categoryTabs = new FillFlowContainer
        {
            Anchor = Anchor.TopRight, Origin = Anchor.TopRight, X = -86, Y = 1,
            AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new Vector2(4, 0),
        };
        foreach (string category in new[] { "NM", "HD", "HR", "DT", "FM", "TB" })
            categoryTabs.Add(new ArenaButton { Text = category, RelativeSizeAxes = Axes.None, Width = 31, Height = 24,
                Action = () =>
                {
                    var slot = state.Match?.Slots.FirstOrDefault(s => s.Category == category);
                    if (slot != null && mapCards.TryGetValue(slot.Id, out var card))
                    {
                        arenaWorkspace.ShowPool();
                        Scheduler.Add(() => { if (Alive && card.IsAlive) oceanScroll.ScrollIntoView(card); });
                    }
                } });
        var main = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = arenaInk.Opacity(.72f) },
                new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding(12), Child = oceanScroll },
            },
        };
        arenaPrimary = new ArenaButton(true) { Text = "Ожидание", Enabled = { Value = false } };
        var leave = new ArenaButton { Text = "Покинуть матч", Action = leaveArenaMatch };
        StatusText.Font = OsuFont.GetFont(size: 13);
        StatusText.Colour = arenaMuted;
        arenaHint = arenaLine("", 18, 0);
        arenaDock = new ArenaActionDock(leave, arenaHint, StatusText, arenaPrimary);
        InternalChild = oceanContent = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                new SomsAiOceanBackdrop(() => this.IsCurrentScreen()) { Alpha = .26f },
                new Box { RelativeSizeAxes = Axes.Both, Colour = ColourInfo.GradientVertical(new Color4(9, 15, 28, 210), new Color4(8, 17, 25, 240)) },
                new Box { RelativeSizeAxes = Axes.X, Height = 2, Colour = ColourInfo.GradientHorizontal(teamColour(0), teamColour(1)) },
                new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Horizontal = 20, Top = 16, Bottom = 68 },
                    Children = new Drawable[]
                    {
                        new ArenaScoreboard(arenaTeams,
                            arenaScore = arenaLabel("0 : 0", 44, Color4.White),
                            arenaFormat = arenaLabel("SOMSAI", 12, SomsAiOceanTheme.Gold)),
                        new Container
                        {
                            RelativeSizeAxes = Axes.X, Height = 42, Y = 108,
                            Children = new Drawable[]
                            {
                                arenaStage = arenaLabel("МАППУЛ", 18, Color4.White),
                                arenaPool = arenaLine("", 12, 23),
                                arenaClock = new OsuSpriteText { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Font = OsuFont.GetFont(size: 24, weight: FontWeight.Bold), Colour = SomsAiOceanTheme.Gold },
                                categoryTabs,
                            },
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 156, Bottom = 82 },
                            Child = arenaWorkspace = new ArenaWorkspace(main, sidebar),
                        },
                        arenaDock,
                    },
                },
            },
        };
        selectArenaTab(0);
    }

    private void selectArenaTab(int index)
    {
        for (int i = 0; i < arenaPages.Length; i++)
        {
            arenaPages[i].FadeTo(i == index ? 1 : 0, 140);
            arenaTabs[i].BackgroundColour = i == index ? new Color4(41, 79, 94, 255) : new Color4(23, 34, 49, 255);
        }
    }

    private void leaveArenaMatch()
    {
        if (state.Match is not { } match) return;
        if (match.IsFinished || match.Stage == "waiting") action("leave_match");
        else dialogs.Push(new SomsAiOceanConfirmDialog("Сдаться? Вашей команде будет засчитано поражение.", () => action("leave_match")));
    }

    private void setArenaPrimary(string caption, Action? action = null)
    {
        arenaPrimary.Text = caption;
        arenaPrimary.Action = action;
        arenaPrimary.Enabled.Value = action != null && actionRequest == null && !preparingAction;
    }

    private void updateArena(SomsAiMatch match, int localId)
    {
        string key = $"{match.Stage}:{match.TurnUserId}";
        if (arenaStageKey != key)
        {
            arenaStageKey = key;
            arenaStage.FadeInFromZero(220);
            if (match.Stage is "ready" or "playing") inspectedSlotId = SelectedSlotId(match);
        }
        arenaScore.Text = $"{match.Wins.ElementAtOrDefault(0)} : {match.Wins.ElementAtOrDefault(1)}";
        arenaFormat.Text = $"SOMSAI  /  {match.Format}  /  BO{match.BestOf}";
        arenaStage.Text = stageLabel(match.Stage).ToUpperInvariant();
        arenaPool.Text = (match.Ranked ? "RANKED" : "CUSTOM") + "  /  " + (match.PoolSelected ? match.PoolName : "Выбор турнирного пула");
        arenaPool.Width = .8f;
        var player = match.Teams.SelectMany(t => t.Members).FirstOrDefault(p => p.Id == match.TurnUserId);
        bool myTurn = match.TurnUserId == localId;
        int ready = match.Teams.SelectMany(t => t.Members).Count(p => p.Ready);
        int total = match.Teams.Sum(t => t.Members.Count);
        arenaHint.Text = match.Stage switch
        {
            "banning" or "picking" => myTurn ? "Ваш ход — выберите карту" : $"Ход: {player?.OfficialUsername ?? player?.Username ?? "соперник"}",
            "ready" => $"Готовы {ready}/{total}" + (pendingReady ? " · ждём загрузку карты…" : ""),
            "playing" => "Матч идёт · удачи!",
            "ended" => "Матч завершён",
            "pool_select" => "Выбор пула · голосуют капитаны",
            _ => stageLabel(match.Stage),
        };
        arenaHint.Colour = myTurn && match.Stage is "banning" or "picking" ? SomsAiOceanTheme.Gold : Color4.White;
        var selected = match.Slots.FirstOrDefault(s => s.Id == inspectedSlotId)
            ?? match.Slots.FirstOrDefault(s => s.Id == SelectedSlotId(match));
        if (selected != null)
        {
            arenaMapName.Text = $"{(string.IsNullOrEmpty(selected.Label) ? selected.Id : selected.Label)}  /  {selected.Title}";
            arenaMapDifficulty.Text = $"{selected.Artist} · {selected.Version}";
            arenaMapStats.Text = selected.DisplayStats is { } stats
                ? $"{stats.Stars:0.00}★   {stats.Bpm:0.#} BPM   {TimeSpan.FromSeconds(stats.Length):m\\:ss}\nCS {stats.Cs:0.#}  AR {stats.Ar:0.#}  OD {stats.Od:0.#}  HP {stats.Hp:0.#}"
                : $"{selected.Stars:0.00}★ · расчёт характеристик…";
        }
        else
        {
            arenaMapName.Text = "Выберите карту из пула";
            arenaMapDifficulty.Text = "Прослушивание — кнопка ▶ на карточке";
            arenaMapStats.Text = "";
        }
        string? coverKey = selected?.BeatmapSetId.ToString();
        if (arenaCoverKey != coverKey)
        {
            arenaCoverKey = coverKey;
            arenaMapCover.Clear();
            if (selected is { BeatmapSetId: > 0 }) arenaMapCover.Add(MapCard.CreateCover(selected.BeatmapSetId));
            arenaMapCover.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = ColourInfo.GradientHorizontal(arenaInk.Opacity(.9f), arenaInk.Opacity(.55f)) });
            if (selected is { BeatmapSetId: > 0 }) arenaMapCover.Add(new SomsBeatmapDownloadProgress(selected.BeatmapSetId));
        }
        if (match.Stage is "banning" or "picking")
        {
            bool canChoose = myTurn && selected?.Status == "available";
            setArenaPrimary(canChoose ? (match.Stage == "banning" ? "Забанить " : "Пикнуть ") + selected!.Id : myTurn ? "Выберите карту" : "Ход соперника",
                canChoose ? () => action(match.Stage == "banning" ? "ban" : "pick", new Newtonsoft.Json.Linq.JObject { ["slot_id"] = selected!.Id }, match.Revision) : null);
        }
        updateArenaRoom();
    }

    private void updateArenaRoom()
    {
        if (!matchOnly || arenaChat == null) return;
        if (OwnsCurrentRoom)
        {
            arenaChatRoom.RoomID = Client.Room!.RoomID;
            if (arenaChatRoom.ChannelId != Client.Room.ChannelID) arenaChatRoom.ChannelId = Client.Room.ChannelID;
        }
    }

    protected override void Update()
    {
        base.Update();
        if (!matchOnly || arenaClock == null || Time.Current < nextArenaClockUpdate) return;
        nextArenaClockUpdate = Time.Current + 200;
        arenaClock.Text = state.Match?.Deadline is { } deadline
            ? TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds))).ToString(@"mm\:ss") : "";
        if (arenaPrimary.Action != null) arenaPrimary.Enabled.Value = actionRequest == null && !preparingAction;
    }

    private static OsuSpriteText arenaLabel(string text, float size, Color4 colour) => new()
    { Text = text, Colour = colour, Font = OsuFont.GetFont(size: size, weight: FontWeight.Bold) };

    private static TruncatingSpriteText arenaLine(string text, float size, float y) => new()
    { RelativeSizeAxes = Axes.X, Y = y, Text = text, Font = OsuFont.GetFont(size: size), Colour = Color4.White };

    private sealed partial class ArenaButton : RoundedButton
    {
        private readonly bool primary;
        public ArenaButton(bool primary = false)
        {
            this.primary = primary;
            RelativeSizeAxes = Axes.X; Height = 42;
            BackgroundColour = primary ? SomsAiOceanTheme.Gold : new Color4(26, 40, 56, 255);
        }
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Content.CornerRadius = 4;
            SpriteText.Font = OsuFont.GetFont(size: primary ? 19 : 14, weight: FontWeight.Bold);
            SpriteText.Colour = primary ? arenaInk : Color4.White;
        }
        protected override void Update()
        {
            base.Update();
            SpriteText.Scale = new Vector2(Math.Min(1, Math.Max(1, DrawWidth - (DrawWidth < 50 ? 10 : 18)) / Math.Max(1, SpriteText.DrawWidth)));
        }
    }

    private sealed partial class ArenaPanel : Container
    {
        public ArenaPanel(Drawable content)
        {
            RelativeSizeAxes = Axes.X; AutoSizeAxes = Axes.Y; Masking = true; CornerRadius = 4;
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(24, 36, 51, 255) },
                new Box { RelativeSizeAxes = Axes.X, Height = 2, Colour = SomsAiOceanTheme.Gold },
                new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding(16), Child = content },
            };
        }
    }

    // Keep native channels, drafts, links, usernames and posting; place the author above
    // the message so lazer's wide three-column chat remains readable in a room sidebar.
    private sealed partial class ArenaChatDisplay : MatchChatDisplay
    {
        public ArenaChatDisplay(Room room) : base(room) { }
        protected override ChatLine CreateMessage(Message message) => new ArenaChatLine(message);
    }

    private sealed partial class ArenaChatLine : ChatLine
    {
        private Drawable? author;
        protected override float Spacing => 3;
        protected override float UsernameWidth => 200;
        public ArenaChatLine(Message message) : base(message) { }
        protected override void LoadComplete()
        {
            base.LoadComplete();
            var grid = InternalChildren.OfType<GridContainer>().Single();
            var original = grid.Content[0];
            var timestamp = original[0]!;
            var username = author = original[1]!;
            var message = original[2]!;
            timestamp.Anchor = timestamp.Origin = Anchor.TopRight;
            username.Anchor = username.Origin = Anchor.TopLeft;
            foreach (var child in ((OsuClickableContainer)username).Children) child.Anchor = child.Origin = Anchor.TopLeft;
            var header = new GridContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
                ColumnDimensions = new[] { new Dimension(), new Dimension(GridSizeMode.Absolute, 45) },
                RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
                Content = new[] { new[] { username, timestamp } },
            };
            grid.Padding = new MarginPadding(7);
            grid.ColumnDimensions = new[] { new Dimension() };
            grid.RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize), new Dimension(GridSizeMode.AutoSize) };
            grid.Content = new[] { new Drawable[] { header }, new[] { message } };
        }
        protected override void Update()
        {
            if (author != null) author.Width = Math.Max(1, DrawWidth - 70);
            base.Update();
        }
    }

    private sealed partial class ArenaScoreboard : Container
    {
        private readonly Container teams;
        private readonly OsuSpriteText score, format;
        public ArenaScoreboard(Container teams, OsuSpriteText score, OsuSpriteText format)
        {
            this.teams = teams; this.score = score; this.format = format;
            RelativeSizeAxes = Axes.X; Height = 104;
            score.Anchor = score.Origin = Anchor.TopCentre; score.Y = 17;
            format.Anchor = format.Origin = Anchor.TopCentre; format.Y = 70;
            Children = new Drawable[] { teams, score, format };
        }
        protected override void Update()
        {
            float centre = DrawWidth < 800 ? 130 : 204;
            foreach (var team in teams.Children)
            {
                team.RelativeSizeAxes = Axes.None;
                team.Width = Math.Max(1, (DrawWidth - centre) / 2);
            }
            format.Scale = new Vector2(Math.Min(1, (centre - 12) / Math.Max(1, format.DrawWidth)));
            base.Update();
        }
    }

    private sealed partial class ArenaWorkspace : Container
    {
        private readonly Container main, sidebar;
        private readonly Container tabs;
        private bool showRoom;
        public void ShowPool() => showRoom = false;
        public ArenaWorkspace(Container main, Container sidebar)
        {
            this.main = main; this.sidebar = sidebar;
            RelativeSizeAxes = Axes.Both;
            Children = new Drawable[] { main, sidebar, tabs = new Container
            {
                RelativeSizeAxes = Axes.X, Height = 32,
                Children = new Drawable[]
                {
                    new ArenaButton { Text = "Маппул", Width = .5f, Height = 30, Action = () => showRoom = false },
                    new ArenaButton { Text = "Комната", Width = .5f, Height = 30, RelativePositionAxes = Axes.X, X = .5f, Action = () => showRoom = true },
                },
            } };
        }
        protected override void Update()
        {
            bool narrow = DrawWidth < 900;
            tabs.Alpha = narrow ? 1 : 0;
            int index = 0;
            foreach (var button in tabs.Children.OfType<ArenaButton>())
                button.BackgroundColour = (index++ == 1) == showRoom ? new Color4(41, 79, 94, 255) : new Color4(23, 34, 49, 255);
            main.Alpha = !narrow || !showRoom ? 1 : 0;
            sidebar.Alpha = !narrow || showRoom ? 1 : 0;
            float sidebarWidth = narrow ? DrawWidth : Math.Clamp(DrawWidth * .26f, 290, 360);
            main.RelativeSizeAxes = sidebar.RelativeSizeAxes = Axes.None;
            main.Size = new Vector2(narrow ? DrawWidth : DrawWidth - sidebarWidth - 14, Math.Max(1, DrawHeight - (narrow ? 38 : 0)));
            main.Y = narrow ? 38 : 0;
            sidebar.Position = new Vector2(narrow ? 0 : DrawWidth - sidebarWidth, narrow ? 38 : 0);
            sidebar.Size = new Vector2(sidebarWidth, main.Height);
            base.Update();
        }
    }

    private sealed partial class ArenaActionDock : Container
    {
        private readonly Drawable leave, title, status, action;
        public ArenaActionDock(Drawable leave, Drawable title, Drawable status, Drawable action)
        {
            this.leave = leave; this.title = title; this.status = status; this.action = action;
            RelativeSizeAxes = Axes.X; Height = 70; Anchor = Origin = Anchor.BottomLeft;
            Children = new[] { new Box { RelativeSizeAxes = Axes.Both, Colour = arenaInk }, leave, title, status, action };
        }
        protected override void Update()
        {
            bool narrow = DrawWidth < 900;
            float actionWidth = narrow ? Math.Min(240, DrawWidth * .48f) : 290;
            leave.RelativeSizeAxes = action.RelativeSizeAxes = title.RelativeSizeAxes = Axes.None;
            leave.Size = new Vector2(narrow ? 100 : 138, 44); leave.Position = new Vector2(10, 13);
            action.Size = new Vector2(actionWidth, 46); action.Position = new Vector2(DrawWidth - actionWidth - 10, 12);
            title.Position = new Vector2(leave.X + leave.Width + 16, 14);
            title.Width = Math.Max(1, action.X - title.X - 14);
            status.Position = new Vector2(title.X, 40);
            status.Scale = new Vector2(Math.Min(1, title.Width / Math.Max(1, status.DrawWidth)));
            base.Update();
        }
    }
}
