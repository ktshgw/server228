#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Framework.Input.Bindings;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Input.Bindings;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Graphics.Backgrounds;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Rulesets.EnhancedAuth.Beatmaps;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.Objects;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public sealed partial class SomsMarathonScreen
{
    private readonly MarathonPane lounge = new();
    private readonly MarathonPane detail = new();
    private readonly Dictionary<SomsMarathonDefinition, MarathonListItem> libraryCards = new();
    private readonly List<Drawable> libraryExtras = new();
    private readonly FillFlowContainer records = flow();
    private readonly List<SomsMarathonDefinition> savedMarathons = new();
    private RangeModal? rangeModal;
    private SomsMarathonRange? range;
    private bool detailVisible;
    private ShearedButton addSongsButton = null!, saveButton = null!;

    private void buildLayout()
    {
        Anchor = Anchor.Centre; Origin = Anchor.Centre;
        library.LayoutDuration = 500; library.LayoutEasing = Easing.OutQuint;
        lounge.Children = new Drawable[]
        {
            new Container { RelativeSizeAxes = Axes.X, Height = 66, Child = search },
            new Container
            {
                RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 80 },
                Child = new OsuScrollContainer { RelativeSizeAxes = Axes.Both, Child = library },
            },
        };
        var fragmentPanel = new Container
        {
            RelativeSizeAxes = Axes.Both, Width = .59f, Padding = new MarginPadding { Right = 16 },
            Children = new Drawable[]
            {
                new Container { RelativeSizeAxes = Axes.X, Height = 65, Child = name },
                new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 78, Bottom = 58 },
                    Child = new OsuScrollContainer { RelativeSizeAxes = Axes.Both, Child = fragments } },
                new Container { RelativeSizeAxes = Axes.X, Height = 44, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft,
                    Child = row(addSongsButton = button("+ Добавить песни", chooseSongs), button("Скачать недостающие", downloadMissing)) },
            },
        };
        var recordPanel = new Container
        {
            RelativeSizeAxes = Axes.Both, Width = .41f, Anchor = Anchor.TopRight, Origin = Anchor.TopRight,
            Children = new Drawable[]
            {
                label("Рекорды марафона", 26),
                new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 42 },
                    Child = new OsuScrollContainer { RelativeSizeAxes = Axes.Both, Child = records } },
            },
        };
        detail.Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(47, 42, 57, 255) },
            new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Horizontal = 22, Top = 20, Bottom = 82 }, Children = new Drawable[] { fragmentPanel, recordPanel } },
            new Container { RelativeSizeAxes = Axes.X, Height = 58, Anchor = Anchor.BottomCentre, Origin = Anchor.BottomCentre,
                Padding = new MarginPadding { Horizontal = 22 }, Child = row(saveButton = button("Сохранить", () => run(save)), button("Играть", () => run(play)), button("Обновить рекорды", showLeaderboard)) },
        };
        InternalChildren = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = ColourInfo.GradientVertical(new Color4(26, 21, 32, 255), new Color4(21, 43, 46, 255)) },
            new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Horizontal = 32, Top = 24, Bottom = 70 }, Children = new Drawable[]
            {
                label("Марафон · Songs compilation", 29),
                new Container { RelativeSizeAxes = Axes.X, Y = 44, Height = 30, Child = status },
                new Container { RelativeSizeAxes = Axes.X, Y = 80, Height = 44,
                    Child = row(button("Все марафоны", showLounge), button("Создать марафон", fresh), button("Мой черновик", showDraft)) },
                new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 142 }, Children = new Drawable[] { lounge, detail } },
            } },
        };
    }

    private void showLounge()
    {
        detailVisible = false; detail.Activate(false); lounge.Activate(true); libraryPage = 1; loadSaved();
    }
    private void showDetail()
    {
        detailVisible = true; lounge.Activate(false); detail.Activate(true);
        renderFragments();
        records.Clear();
        if (definition.Id > 0) showLeaderboard();
        else records.Add(label("Сохраните подборку, чтобы открыть её таблицу рекордов.", 17));
    }

    private void showDraft()
    {
        if (System.IO.File.Exists(draftPath))
        {
            try
            {
                var draft = Newtonsoft.Json.JsonConvert.DeserializeObject<SomsMarathonDefinition>(System.IO.File.ReadAllText(draftPath));
                if (draft != null && (draft.OwnerId == 0 && draft.Id == 0 || draft.OwnerId == api.LocalUser.Value.Id))
                {
                    definition = draft; name.Current.Value = draft.Name; showDetail(); return;
                }
            }
            catch (Exception e) when (e is System.IO.IOException or Newtonsoft.Json.JsonException) { }
        }
        fresh();
    }

    private void chooseSongs()
    {
        if (!canEdit) return;
        previewEnd = null;
        this.Push(new SomsMarathonSongSelect(definition.RulesetId, 20 - definition.Segments.Count, maps =>
        {
            // Return from the picker before changing the screen's UI.
            Scheduler.AddDelayed(() => addMaps(maps), 1);
        }));
    }

    private void addMaps(IReadOnlyList<BeatmapInfo> maps)
    {
        if (!canEdit || maps.Count == 0) return;
        run(async () =>
        {
            var additions = new List<(SomsMarathonSegment Segment, WorkingBeatmap Map)>();
            foreach (var info in maps.Take(20 - definition.Segments.Count))
            {
                var working = beatmaps.GetWorkingBeatmap(info);
                var segment = await Task.Run(() => SomsMarathonCompiler.Suggest(working), lifetime.Token);
                additions.Add((segment, working));
            }
            Schedule(() =>
            {
                if (closed) return;
                foreach (var item in additions) { resolved[item.Segment.Checksum] = item.Map; definition.Segments.Add(item.Segment); }
                changed(); renderFragments(); status.Text = $"Добавлено песен: {additions.Count}. Отрезки можно изменить кнопкой «Редактировать».";
            });
        });
    }

    private Drawable cover(SomsMarathonSegment? segment)
    {
        WorkingBeatmap? working = null;
        if (segment != null) { try { working = resolve(segment); } catch (InvalidOperationException) { } }
        if (working != null)
            return new DelayedLoadWrapper(() => new BeatmapBackground(working) { RelativeSizeAxes = Axes.Both }, timeBeforeLoad: 50) { RelativeSizeAxes = Axes.Both };
        // The public definition includes the set ID, so browsing does not require
        // the creator's local beatmap files. Use lazer's cached, asynchronous cover.
        if (segment?.BeatmapSetId > 0)
            return new UpdateableOnlineBeatmapSetCover(timeBeforeLoad: 50)
            {
                RelativeSizeAxes = Axes.Both,
                OnlineInfo = new APIBeatmapSet
                {
                    OnlineID = segment.BeatmapSetId,
                    Covers = new BeatmapSetOnlineCovers { Cover = $"https://assets.ppy.sh/beatmaps/{segment.BeatmapSetId}/covers/cover@2x.jpg" },
                },
            };
        return new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(81, 63, 101, 255) };
    }

    private Container songCard(SomsMarathonSegment? segment, float height, params Drawable[] content) => new()
    {
        RelativeSizeAxes = Axes.X, Height = height, Masking = true, CornerRadius = 10,
        Children = new Drawable[]
        {
            cover(segment),
            new Box { RelativeSizeAxes = Axes.Both, Colour = ColourInfo.GradientHorizontal(new Color4(24, 20, 32, 180), new Color4(24, 20, 32, 246)) },
            new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding(14), Children = content },
            new SomsBeatmapDownloadProgress(segment?.BeatmapSetId ?? 0),
        },
    };

    private void renderSavedCards()
    {
        foreach (var removed in libraryCards.Keys.Except(savedMarathons).ToArray())
        {
            libraryCards[removed].Retire(); libraryCards.Remove(removed);
        }
        foreach (var extra in libraryExtras) library.Remove(extra, true);
        libraryExtras.Clear();
        string query = search.Current.Value.Trim();
        int visible = 0;
        foreach (var item in savedMarathons)
        {
            bool matches = (item.Name + " " + item.OwnerName).Contains(query, StringComparison.OrdinalIgnoreCase);
            if (libraryCards.TryGetValue(item, out var existing))
            {
                existing.SetVisible(matches);
                if (matches) library.SetLayoutPosition(existing, visible++);
                continue;
            }
            var open = new MarathonListItem(songCard(item.Segments.FirstOrDefault(), 120,
                    label(item.Name, 25),
                    new Container { RelativeSizeAxes = Axes.X, Y = 38, Height = 26, Child = label($"{item.Segments.Count} песен · {duration(item)} · автор {item.OwnerName}", 17) },
                    new Container { RelativeSizeAxes = Axes.X, Y = 70, Height = 24, Child = label("Songs compilation   ·   отдельная таблица рекордов", 15) }), 120)
            {
                Action = () => { if (busy) return; definition = Newtonsoft.Json.JsonConvert.DeserializeObject<SomsMarathonDefinition>(Newtonsoft.Json.JsonConvert.SerializeObject(item))!; name.Current.Value = item.Name; showDetail(); },
            };
            if (item.OwnerId == api.LocalUser.Value.Id)
            {
                bool confirm = false;
                ShearedButton? remove = null;
                remove = button("Удалить", () =>
                {
                    if (!confirm) { confirm = true; remove!.Text = "Точно удалить?"; return; }
                    run(async () =>
                    {
                        await request(new SomsMarathonRequest($"/{item.Id}", method: System.Net.Http.HttpMethod.Delete));
                        Schedule(() =>
                        {
                            savedMarathons.Remove(item);
                            if (definition.Id == item.Id) changed();
                            renderSavedCards();
                            status.Text = "Марафон удалён.";
                        });
                    });
                });
                open.Add(new Container
                {
                    Anchor = Anchor.BottomRight, Origin = Anchor.BottomRight, Width = 170, Height = 40,
                    Margin = new MarginPadding { Right = 18, Bottom = 12 }, Child = remove,
                });
            }
            library.Add(open);
            libraryCards.Add(item, open); open.SetVisible(matches);
            if (matches) library.SetLayoutPosition(open, visible++);
        }
        void addExtra(Drawable extra) { library.Add(extra); libraryExtras.Add(extra); library.SetLayoutPosition(extra, float.MaxValue); }
        if (visible == 0) addExtra(label("Марафонов не найдено. Создайте свою подборку!", 22));
        if (libraryPage > 1) addExtra(button("← Предыдущие", () => { libraryPage--; loadSaved(); }));
        if (hasMore) addExtra(button("Следующие →", () => { libraryPage++; loadSaved(); }));
    }

    private bool hasMore;
    private static string duration(SomsMarathonDefinition value) => TimeSpan.FromMilliseconds(value.Segments.Sum(s => s.EndMs - s.StartMs + 2000) + 1000).ToString(@"m\:ss");
    private static string timestamp(int value) => TimeSpan.FromMilliseconds(value).ToString(@"m\:ss\.ff");

    private void editSegment(SomsMarathonSegment segment)
    {
        if (!canEdit || rangeModal != null) return;
        run(async () =>
        {
            var working = resolve(segment);
            int end = await Task.Run(() => (int)Math.Ceiling(Math.Max(working.BeatmapInfo.Length,
                working.Beatmap.HitObjects.Max(hit => hit.GetEndTime()) + 1)), lifetime.Token);
            Schedule(() =>
            {
                if (closed) return;
                var timeline = range = new SomsMarathonRange(Math.Min(7200000, end), segment.StartMs, segment.EndMs);
                var caption = label("", 21);
                void updateCaption() => caption.Text = $"{timestamp(timeline.Start)}  —  {timestamp(timeline.End)}   ·   {(timeline.End - timeline.Start) / 1000.0:0.00} с";
                timeline.Changed = updateCaption; updateCaption();
                var contents = flow();
                contents.Add(label("Выберите отрезок", 28)); contents.Add(label(segment.Title, 18));
                contents.Add(label("Перетащите границы · от 5 до 90 секунд", 16));
                contents.Add(timeline); contents.Add(caption);
                contents.Add(row(button("Прослушать", () =>
                {
                    Beatmap.Value = working; music.Play(); music.SeekTo(timeline.Start); previewEnd = timeline.End;
                }), button("Применить", () =>
                {
                    if (!canEdit) return;
                    segment.StartMs = timeline.Start; segment.EndMs = timeline.End; changed(); closeRange(); renderFragments();
                }), button("Отмена", closeRange)));
                AddInternal(rangeModal = new RangeModal(contents, closeRange));
            });
        });
    }

    private void closeRange()
    {
        if (rangeModal == null) return;
        previewEnd = null; music.Stop(); range = null;
        rangeModal.Dismiss(); rangeModal = null; Beatmap.Value = original;
    }

    private sealed partial class RangeModal : Container, IKeyBindingHandler<GlobalAction>
    {
        private readonly Container panel;
        private readonly Action close;
        private bool closing;
        public override bool PropagatePositionalInputSubTree => !closing;
        public override bool PropagateNonPositionalInputSubTree => !closing;

        public RangeModal(Drawable content, Action close)
        {
            this.close = close;
            RelativeSizeAxes = Axes.Both; Depth = -100;
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(0, 0, 0, 210) },
                panel = new Container { RelativeSizeAxes = Axes.X, Width = .8f, Height = 340, Anchor = Anchor.Centre, Origin = Anchor.Centre,
                    Masking = true, CornerRadius = 14, Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(46, 40, 57, 255) },
                        new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding(24), Child = content },
                    } },
            };
        }
        protected override void LoadComplete()
        {
            base.LoadComplete(); this.FadeInFromZero(200);
            panel.MoveToY(24).MoveToY(0, 400, Easing.OutQuint);
        }
        public void Dismiss()
        {
            if (closing) return;
            closing = true;
            this.FadeOut(200).Expire(); panel.MoveToY(-24, 200, Easing.InSine);
        }
        protected override bool OnMouseDown(MouseDownEvent e) => true;
        protected override bool OnClick(ClickEvent e) => true;
        protected override bool OnScroll(ScrollEvent e) => true;
        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            if (e.Action != GlobalAction.Back) return false;
            if (!e.Repeat && !closing) close();
            return true;
        }
        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e) { }
    }

    // Match the native online-play fade and the lounge's 500 ms OutQuint reflow.
    // Disable outgoing input immediately, before the fade has finished.
    private sealed partial class MarathonPane : Container
    {
        private bool active;
        public MarathonPane() { RelativeSizeAxes = Axes.Both; Alpha = 0; }
        public override bool PropagatePositionalInputSubTree => active;
        public override bool PropagateNonPositionalInputSubTree => active;
        public void Activate(bool value)
        {
            if (active == value) return;
            active = value;
            if (value && Alpha == 0) Y = 16;
            this.FadeTo(value ? 1 : 0, value ? 500 : 250, Easing.OutQuint);
            this.MoveToY(value ? 0 : -16, value ? 500 : 250, Easing.OutQuint);
        }
    }

    private sealed partial class MarathonListItem : ClickableContainer
    {
        private bool visible = true, retired;
        private readonly Container content;
        private readonly Box hover;
        public override bool PropagatePositionalInputSubTree => visible && !retired;
        public override bool PropagateNonPositionalInputSubTree => visible && !retired;
        public MarathonListItem(Drawable card, float height)
        {
            RelativeSizeAxes = Axes.X; Height = height;
            Add(content = new Container { RelativeSizeAxes = Axes.Both, Children = new Drawable[]
            {
                card,
                new Container { RelativeSizeAxes = Axes.Both, Masking = true, CornerRadius = 10,
                    Child = hover = new Box { RelativeSizeAxes = Axes.Both, Alpha = 0, Colour = Color4.White } },
            } });
        }
        protected override void LoadComplete()
        {
            base.LoadComplete();
            if (visible)
            {
                this.FadeInFromZero(200);
                content.MoveToY(12).MoveToY(0, 400, Easing.OutQuint);
            }
        }
        public void SetVisible(bool value)
        {
            if (visible == value || retired) return;
            visible = value;
            this.FadeTo(value ? 1 : 0, 200);
        }
        public void Retire()
        {
            retired = true; Action = null;
            this.FadeOut(200).Expire(); content.MoveToY(-12, 200, Easing.InSine);
        }
        protected override bool OnHover(HoverEvent e) { hover.FadeTo(.07f, 120); return base.OnHover(e); }
        protected override void OnHoverLost(HoverLostEvent e) { hover.FadeOut(200); base.OnHoverLost(e); }
    }

    public override void OnEntering(ScreenTransitionEvent e)
    {
        base.OnEntering(e);
        this.FadeInFromZero(800, Easing.OutQuint);
    }

    public override void OnSuspending(ScreenTransitionEvent e)
    {
        base.OnSuspending(e);
        this.ScaleTo(1.1f, 250, Easing.InSine); this.FadeOut(250);
    }
}
