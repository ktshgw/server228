#nullable enable
using System;
using System.Globalization;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Screens;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Screens.OnlinePlay;
using osu.Game.Screens.OnlinePlay.Lounge;
using osu.Game.Screens.OnlinePlay.Lounge.Components;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>A compact classic room browser, fed by the native listing poller and its filters.</summary>
public sealed partial class SomsLegacyLounge : SomsLegacyComponent
{
    private readonly LoungeSubScreen owner;
    private RoomListing? listing;
    private BindableList<Room>? rooms;
    private Bindable<LoungeFilterCriteria?>? filters;
    private Bindable<Room?>? selected;
    private Bindable<bool>? hasResults;
    private Bindable<bool>? busy;
    private bool advanced;
    private bool passwordPrompt;
    private string error = "";
    private TruncatingSpriteText? status;
    private SomsLegacyOverlayButton? joinButton;
    private BasicTextBox? searchBox;
    private BasicScrollContainer? scroll;
    private bool restoreSearchFocus;
    private double scrollOffset;
    private bool active = true;
    private bool suspended;

    public bool UsesOwnChrome => LegacyEnabled && Alpha > 0 && !advanced && active;

    public void PauseLegacy()
    {
        // A suspended subscreen may stop updating immediately, so ancestor visuals must be restored here.
        suspended = true;
        active = false;
        RestoreNative();
        Alpha = 0;
    }

    public void ResumeLegacy()
    {
        suspended = false;
        active = true;
        RequestRefresh();
    }

    public SomsLegacyLounge(LoungeSubScreen owner)
    {
        this.owner = owner;
        Name = "soms-legacy-room-browser";
    }

    protected override void LoadComplete()
    {
        listing = SomsLegacyInterfacePatch.Member<RoomListing>(owner, "roomListing");
        if (listing != null)
        {
            rooms = listing.Rooms.GetBoundCopy();
            rooms.BindCollectionChanged((_, _) => RequestRefresh());
            filters = listing.Filter.GetBoundCopy();
            filters.BindValueChanged(_ => { scrollOffset = 0; RequestRefresh(); });
            selected = new Bindable<Room?>();
            ((IBindable<Room?>)selected).BindTo(listing.SelectedRoom);
            selected.BindValueChanged(_ => { passwordPrompt = false; error = ""; RequestRefresh(); });
        }
        hasResults = SomsLegacyInterfacePatch.Member<Bindable<bool>>(owner, "hasListingResults")?.GetBoundCopy();
        if (SomsLegacyInterfacePatch.Member<IBindable<bool>>(owner, "operationInProgress") is { } operations)
        {
            busy = new Bindable<bool>();
            ((IBindable<bool>)busy).BindTo(operations);
        }
        base.LoadComplete();
    }

    protected override void Rebuild(ISkinSource skin)
    {
        status = null;
        joinButton = null;
        if (listing == null) return;
        var online = onlineParent();
        active = !suspended && (online == null || online.CurrentSubScreen == owner);
        if (!active) return;
        Alpha = 1;

        if (advanced)
        {
            AddInternal(new SomsLegacyOverlayButton(skin, "", "← Классический список комнат", () =>
            {
                advanced = false;
                RequestRefresh();
            }, new Color4(113, 196, 236, 255))
            {
                Position = new Vector2(24, 70), Size = new Vector2(350, 42),
                Name = "soms-legacy-return-to-room-browser",
            });
            return;
        }

        // Keep the polling component alive. Room models and the native background still update normally.
        HideNative(SomsLegacyInterfacePatch.Children(owner).Where(child => child != this && child is not LoungeListingPoller), keepUpdating: true);
        if (online != null && SomsLegacyInterfacePatch.Member<CompositeDrawable>(online, "waves") is { } waves)
            HideNative(SomsLegacyInterfacePatch.Children(waves).OfType<Header>(), keepUpdating: true);
        AddInternal(new InputBlocker
        {
            RelativeSizeAxes = Axes.Both,
            Child = new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(18, 28, 42, 247) },
        });
        var scaling = new DrawSizePreservingFillContainer { TargetDrawSize = new Vector2(1024, 768) };
        var canvas = SomsLegacyOverlayButton.Canvas(this);
        scaling.Add(canvas);
        AddInternal(scaling);
        canvas.Add(new SomsLegacyOverlayButton(skin, "menu-back", "Назад", () =>
        {
            // Lounge is the root of the online subscreen stack. Native Back exits the outer screen.
            if (onlineParent() is { } parent) parent.Exit();
            else owner.Exit();
        }, new Color4(209, 105, 157, 255))
        {
            Name = "soms-legacy-room-back", Position = new Vector2(35, 22), Size = new Vector2(180, 42),
        });
        canvas.Add(label("Доступные комнаты", 35, 82, 620, 34));
        canvas.Add(label("Выберите комнату и войдите в неё. Двойное нажатие также запускает вход.", 37, 126, 950, 17));

        if (SomsLegacyInterfacePatch.Member<TextBox>(owner, "searchTextBox") is { } search)
        {
            canvas.Add(searchBox = new BasicTextBox
            {
                Name = "soms-legacy-room-search",
                Position = new Vector2(35, 168),
                Size = new Vector2(630, 42),
                PlaceholderText = "Поиск по названию комнаты…",
                Current = search.Current.GetBoundCopy(),
            });
            var freshSearch = searchBox;
            if (restoreSearchFocus && !passwordPrompt)
                ScheduleAfterChildren(() =>
                {
                    if (!SomsDrawableLifecycle.IsDisposed(freshSearch)) GetContainingFocusManager()?.ChangeFocus(freshSearch);
                });
        }
        canvas.Add(new SomsLegacyOverlayButton(skin, "", "Фильтры", () =>
        {
            advanced = true;
            RequestRefresh();
        }, new Color4(103, 163, 218, 255))
        {
            Name = "soms-legacy-room-filters", Position = new Vector2(682, 168), Size = new Vector2(145, 42),
        });
        canvas.Add(new SomsLegacyOverlayButton(skin, "", "Обновить", () => owner.RefreshRooms(), new Color4(103, 163, 218, 255))
        {
            Name = "soms-legacy-room-refresh", Position = new Vector2(842, 168), Size = new Vector2(147, 42),
        });

        canvas.Add(new Box { Position = new Vector2(35, 225), Size = new Vector2(954, 31), Colour = Color4.Black, Alpha = 0.28f });
        canvas.Add(label("Название / карта", 48, 231, 510, 16));
        canvas.Add(label("Игроки", 605, 231, 120, 16));
        canvas.Add(label("Состояние", 749, 231, 220, 16));

        var flow = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 6),
            Padding = new MarginPadding { Right = 10 },
        };
        canvas.Add(scroll = new BasicScrollContainer
        {
            Name = "soms-legacy-room-list",
            Position = new Vector2(35, 262),
            Size = new Vector2(954, 330),
            Child = flow,
        });
        scroll.ScrollTo(scrollOffset, false);

        foreach (var room in (rooms ?? listing.Rooms).Where(matches))
        {
            var captured = room;
            flow.Add(new RoomRow(skin, captured, () =>
            {
                if (selected == null) return;
                if (selected.Value == captured) requestJoin();
                else selected.Value = captured;
            }, selected?.Value == captured));
        }

        status = label("", 38, 610, 950, 19);
        canvas.Add(status);
        canvas.Add(new SomsLegacyOverlayButton(skin, "", "Создать комнату", () => owner.Open(), new Color4(230, 165, 86, 255))
        {
            Name = "soms-legacy-room-create", Position = new Vector2(35, 654), Size = new Vector2(355, 58),
        });
        canvas.Add(joinButton = new SomsLegacyOverlayButton(skin, "", "Войти в комнату", requestJoin, new Color4(133, 217, 123, 255))
        {
            Name = "soms-legacy-room-join", Position = new Vector2(410, 654), Size = new Vector2(579, 58),
        });

        if (passwordPrompt && selected?.Value is { } lockedRoom)
        {
            var dialog = new Container
            {
                Name = "soms-legacy-room-password",
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(600, 175),
                Children = new Drawable[] { new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(26, 36, 56, 255) } },
            };
            dialog.Add(label("Пароль комнаты: " + lockedRoom.Name, 20, 18, 560, 22));
            var password = new OsuPasswordTextBox
            {
                Position = new Vector2(20, 62), Size = new Vector2(560, 40), PlaceholderText = "Пароль",
            };
            dialog.Add(password);
            dialog.Add(new SomsLegacyOverlayButton(skin, "", "Отмена", () => { passwordPrompt = false; RequestRefresh(); }, new Color4(167, 158, 180, 255))
            {
                Position = new Vector2(20, 117), Size = new Vector2(180, 42),
            });
            dialog.Add(new SomsLegacyOverlayButton(skin, "", "Войти", () => join(lockedRoom, password.Current.Value), new Color4(133, 217, 123, 255))
            {
                Position = new Vector2(220, 117), Size = new Vector2(360, 42),
            });
            canvas.Add(new InputBlocker { RelativeSizeAxes = Axes.Both, Child = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = 0.65f } });
            canvas.Add(dialog);
        }
    }

    private void requestJoin()
    {
        if (busy?.Value == true || selected?.Value is not { } room) return;
        if (room.HasPassword) { passwordPrompt = true; RequestRefresh(); }
        else join(room, null);
    }

    private void join(Room room, string? password)
    {
        if (busy?.Value == true) return;
        error = "";
        owner.Join(room, password, onFailure: (message, _) =>
        {
            if (SomsDrawableLifecycle.IsDisposed(this)) return;
            Schedule(() =>
            {
                if (SomsDrawableLifecycle.IsDisposed(this)) return;
                passwordPrompt = false;
                error = "Не удалось войти: " + message;
                RequestRefresh();
            });
        });
    }

    protected override void Update()
    {
        base.Update();
        var online = onlineParent();
        bool current = !suspended && (online == null || online.CurrentSubScreen == owner);
        if (current != active)
        {
            active = current;
            // Restore the parent header as soon as a room replaces the lounge, and rebuild on return.
            RequestRefresh();
        }
        if (status == null) return;
        bool joining = busy?.Value == true;
        if (joinButton != null) joinButton.Enabled.Value = selected?.Value != null && !joining;
        int count = rooms?.Count(matches) ?? 0;
        status.Text = error.Length > 0 ? error : joining ? "Подключаемся к комнате…" : hasResults?.Value == false ? "Обновляем список комнат…" : count == 0 ? "Комнат по текущим фильтрам нет. Создайте свою или измените фильтры." : "Комнат: " + count;
    }

    private bool matches(Room room)
    {
        var criteria = listing?.Filter.Value;
        if (criteria == null) return true;
        if (criteria.Ruleset != null && room.PlaylistItemStats?.RulesetIDs.Any(id => id == criteria.Ruleset.OnlineID) == false) return false;
        if (criteria.Permissions == RoomPermissionsFilter.Public && room.HasPassword) return false;
        if (criteria.Permissions == RoomPermissionsFilter.Private && !room.HasPassword) return false;
        // Match the native room list's subsequence search, including case and invariant culture.
        foreach (string term in criteria.SearchString.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int index = 0;
            foreach (char character in term)
            {
                int found = CultureInfo.InvariantCulture.CompareInfo.IndexOf(room.Name, character, index, CompareOptions.OrdinalIgnoreCase);
                if (found < 0) return false;
                index = found + 1;
            }
        }
        return true;
    }

    private OnlinePlayScreen? onlineParent()
    {
        for (Drawable? parent = owner.Parent; parent != null; parent = parent.Parent)
            if (parent is OnlinePlayScreen online) return online;
        return null;
    }

    protected override void RestoreLayout()
    {
        status = null;
        joinButton = null;
        if (searchBox != null) restoreSearchFocus = searchBox.HasFocus;
        if (scroll != null) scrollOffset = scroll.Current;
        searchBox = null;
        scroll = null;
    }

    protected override void Dispose(bool isDisposing)
    {
        rooms?.UnbindAll();
        filters?.UnbindAll();
        selected?.UnbindAll();
        hasResults?.UnbindAll();
        busy?.UnbindAll();
        base.Dispose(isDisposing);
    }

    private static TruncatingSpriteText label(string text, float x, float y, float width, float size) => new()
    {
        Position = new Vector2(x, y), Width = width, Text = text,
        Font = SomsLegacyFont.Font(size), Shadow = true,
    };

    private sealed partial class InputBlocker : Container
    {
        protected override bool OnClick(osu.Framework.Input.Events.ClickEvent e) => true;
        protected override bool OnMouseDown(osu.Framework.Input.Events.MouseDownEvent e) => true;
        protected override bool OnScroll(osu.Framework.Input.Events.ScrollEvent e) => true;
    }

    private sealed partial class RoomRow : OsuClickableContainer
    {
        private readonly Room room;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText subtitle;
        private readonly TruncatingSpriteText players;
        private readonly TruncatingSpriteText state;

        public RoomRow(ISkin skin, Room room, Action action, bool selected)
        {
            this.room = room;
            Name = "soms-legacy-room-" + room.RoomID;
            RelativeSizeAxes = Axes.X;
            Height = 68;
            Action = action;
            TooltipText = room.Name;
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = selected ? new Color4(100, 74, 140, 255) : new Color4(45, 57, 76, 255) },
            };
            var art = SomsLegacyOverlayButton.Art(skin, "menu-button-background", FillMode.Stretch);
            if (art != null) { art.Alpha = 0.25f; Add(art); }
            Add(title = label("", 12, 8, 540, 23));
            Add(subtitle = label("", 12, 39, 540, 16));
            Add(players = label("", 570, 21, 128, 23));
            Add(state = label("", 715, 22, 220, 20));
        }

        protected override void Update()
        {
            base.Update();
            title.Text = (room.HasPassword ? "[Пароль] " : "") + room.Name;
            subtitle.Text = "Хост: " + (room.Host?.Username ?? "—") + " · " + (room.CurrentPlaylistItem?.Beatmap?.Metadata?.Title ?? "Выбор карты");
            players.Text = room.ParticipantCount + " / " + (room.MaxParticipants?.ToString() ?? "—");
            state.Text = room.Status == RoomStatus.Playing ? "Идёт игра" : "Ожидание";
        }
    }
}
