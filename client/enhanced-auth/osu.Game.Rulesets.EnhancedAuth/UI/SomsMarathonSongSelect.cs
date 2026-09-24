#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Graphics.Sprites;
using osu.Game.Online.Rooms;
using osu.Game.Screens.Footer;
using osu.Game.Screens.OnlinePlay;
using osu.Game.Screens.OnlinePlay.Playlists;
using osu.Game.Screens.Select;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

// Native carousel/search/collections, with selection committed to the compilation on return.
public sealed partial class SomsMarathonSongSelect : SongSelect
{
    public override string Title => "Марафон · Выбор песен";
    private readonly int mode, capacity;
    private readonly Action<IReadOnlyList<BeatmapInfo>> completed;
    private readonly List<BeatmapInfo> chosen = new();
    // An in-memory playlist drives the native tray. It is never created on the server.
    private readonly Room selection = new();
    private readonly AddToMarathonFooterButton add;
    private bool committed;

    public SomsMarathonSongSelect(int mode, int capacity, Action<IReadOnlyList<BeatmapInfo>> completed)
    {
        this.mode = mode; this.capacity = capacity; this.completed = completed;
        ShowOsuLogo = false;
        Padding = new MarginPadding { Horizontal = HORIZONTAL_OVERFLOW_PADDING };
        TopPadding = Header.HEIGHT - 10;
        add = new AddToMarathonFooterButton
        {
            Anchor = Anchor.BottomRight, Origin = Anchor.BottomRight,
            Margin = new MarginPadding { Bottom = OsuGame.SCREEN_EDGE_MARGIN, Right = OsuGame.SCREEN_EDGE_MARGIN * 2 },
            Alpha = 0, Action = OnStart,
        };
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        // Reuse the actual playlist widget: scrolling map card, item counter,
        // buffered background, delayed fade-out and all native animation timings.
        AddInternal(new PlaylistsSongSelect.PlaylistTray(selection)
        {
            Anchor = Anchor.BottomRight, Origin = Anchor.BottomRight,
            Margin = new MarginPadding { Bottom = ScreenFooterButton.HEIGHT, Right = OsuGame.SCREEN_EDGE_MARGIN },
        });
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Footer.Add(add);
        Beatmap.BindValueChanged(_ => updateAvailability(), true);
        Ruleset.BindValueChanged(_ => updateAvailability());
    }

    private void updateAvailability()
    {
        bool correctMode = Beatmap.Value.BeatmapInfo.Ruleset.OnlineID == mode && Ruleset.Value.OnlineID == mode;
        add.Enabled.Value = !committed && chosen.Count < capacity && correctMode;
        add.TooltipText = chosen.Count >= capacity ? "В марафоне может быть до 20 песен"
            : !correctMode ? "Выберите карту исходного режима марафона" : "Добавить выбранную сложность в марафон";
    }

    protected override void OnStart()
    {
        updateAvailability();
        if (!add.Enabled.Value) return;
        var beatmap = Beatmap.Value.BeatmapInfo.Clone();
        chosen.Add(beatmap);
        selection.Playlist = selection.Playlist.Append(new PlaylistItem(beatmap) { ID = chosen.Count, RulesetID = mode }).ToArray();
        updateAvailability();
    }

    public override IReadOnlyList<ScreenFooterButton> CreateFooterButtons() => base.CreateFooterButtons()
        .Where(button => button is not FooterButtonMods)
        .Append(new ScreenFooterButton { Text = "Готово", Icon = osu.Framework.Graphics.Sprites.FontAwesome.Solid.Check, Action = () => this.Exit() }).ToArray();

    public override void OnEntering(ScreenTransitionEvent e) { base.OnEntering(e); add.Appear(); }
    public override void OnSuspending(ScreenTransitionEvent e) { base.OnSuspending(e); add.Disappear(); }
    public override void OnResuming(ScreenTransitionEvent e) { base.OnResuming(e); add.Appear(); }
    public override bool OnExiting(ScreenExitEvent e)
    {
        if (base.OnExiting(e)) return true;
        add.Disappear().Expire();
        if (!committed) { committed = true; completed(chosen); }
        return false;
    }

    // Keep the native button layout, colours, hover/click feedback and Appear /
    // Disappear implementations. Only its caption is specific to marathons.
    private sealed partial class AddToMarathonFooterButton : AddToPlaylistFooterButton
    {
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Text = "Добавить в марафон";
            foreach (var caption in ButtonContent.Children.OfType<OsuSpriteText>().Where(text => text.Text.ToString() != "+"))
                caption.Text = Text;
        }
    }
}
