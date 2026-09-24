#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Screens.Menu;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>A persistent stable menu scene. Native actions retain navigation, login checks and screen loading.</summary>
public sealed partial class SomsLegacyMainMenu : SomsLegacyComponent
{
    private const float canvas_width = 1366;
    private const float canvas_height = 768;
    private const float menu_x = 515;
    private readonly MainMenu owner;
    private readonly Dictionary<string, MenuPage> pages = new();
    private ButtonSystem? nativeButtons;
    private LoginOverlay? login;
    private ChatOverlay? chat;
    private DashboardOverlay? dashboard;
    private MenuButton? cookie;
    private Box? dim;
    private bool nativeMultiplayer;
    private string page = "closed";

    public SomsLegacyMainMenu(MainMenu owner)
    {
        this.owner = owner;
        Name = "soms-legacy-main-menu";
    }

    [BackgroundDependencyLoader(true)]
    private void load(LoginOverlay? login, ChatOverlay? chat, DashboardOverlay? dashboard)
    {
        this.login = login;
        this.chat = chat;
        this.dashboard = dashboard;
        nativeButtons = SomsLegacyInterfacePatch.Member<ButtonSystem>(owner, "Buttons");
    }

    protected override void Rebuild(ISkinSource skin)
    {
        if (nativeButtons == null || nativeMultiplayer) return;
        HideNative(SomsLegacyInterfacePatch.Children(owner).Where(d => d != this && d.GetType().Name != "GlobalScrollAdjustsVolume"));
        Alpha = 1;
        pages.Clear();

        var scene = new MenuScene { RelativeSizeAxes = Axes.Both, Name = "soms-legacy-main-menu-scene" };
        AddInternal(scene);
        scene.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(20, 24, 34, 255) });
        // Stable's seasonal cache contains the exact space artwork shown in the reference.
        // Custom menu-background artwork still wins over this embedded fallback.
        var background = SomsStableInterfaceResources.GetSprite(skin, "menu-background", "menu-background-seasonal");
        if (background != null)
        {
            background.RelativeSizeAxes = Axes.Both;
            background.Size = Vector2.One;
            background.FillMode = FillMode.Fill;
            background.Anchor = background.Origin = Anchor.Centre;
            scene.Add(background);
        }
        scene.Add(dim = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black });

        var canvas = new DrawSizePreservingFillContainer
        {
            // Stable preserves the 768 px vertical layout even at 4:3. The reference's wider
            // logical scene is centred inside this viewport; edge controls still anchor to it.
            RelativeSizeAxes = Axes.Both, TargetDrawSize = new Vector2(1024, canvas_height),
        };
        scene.Add(canvas);
        // Edge controls follow the viewport; cookie and menu retain stable's 768 px proportions.
        canvas.Add(new Container
        {
            RelativeSizeAxes = Axes.X, Height = 86, Depth = -5,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = 0.18f },
                new SomsLegacyUserPanel { Position = new Vector2(0, 2) },
                new SomsLegacyMainMenuMusic { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, X = -8 },
            },
        });
        canvas.Add(new Container
        {
            RelativeSizeAxes = Axes.X, Height = 86, Depth = -5, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = 0.23f },
                text("SOMS!", 12, 47, 150, 25, true),
                text("osu!lazer · Legacy interface", 13, 73, 280, 10),
                footerButton("legacy-menu-account", "Аккаунт", -304, _ => login?.ToggleVisibility()),
                footerButton("legacy-menu-users", "Игроки в сети", -210, _ => dashboard?.ToggleVisibility(), 124),
                footerButton("legacy-menu-chat", "Открыть чат  F8", -70, _ => chat?.ToggleVisibility(), 136),
            },
        });

        var centre = new Container
        {
            Anchor = Anchor.Centre, Origin = Anchor.Centre, Size = new Vector2(canvas_width, canvas_height),
        };
        canvas.Add(centre);
        var main = addPage("main");
        addButton(main, 0, "menu-play", "menu-button-play", "Играть", FontAwesome.Solid.Play, _ => setPage("play"));
        addButton(main, 1, "menu-edit", "menu-button-edit", "Редактор", FontAwesome.Solid.PencilAlt, _ => setPage("edit"));
        addButton(main, 2, "menu-options", "menu-button-options", "Настройки", FontAwesome.Solid.CheckSquare, _ => nativeButtons.OnSettings?.Invoke());
        addButton(main, 3, "menu-exit", "menu-button-exit", "Выход", FontAwesome.Solid.DoorOpen, e => nativeButtons.OnExit?.Invoke(e));

        var play = addPage("play");
        addButton(play, 0, "menu-solo", "menu-button-freeplay", "Одиночная игра", FontAwesome.Solid.User, _ => nativeButtons.OnSolo?.Invoke());
        addButton(play, 1, "menu-multi", "menu-button-multiplayer", "Мультиплеер", FontAwesome.Solid.Users, _ => openMultiplayer());
        addButton(play, 2, "menu-direct", "menu-osudirect", "Найти карты", FontAwesome.Solid.Download, _ => nativeButtons.OnBeatmapListing?.Invoke());
        addButton(play, 3, "menu-back", "menu-button-back", "Назад", FontAwesome.Solid.ArrowLeft, _ => setPage("main"));

        var edit = addPage("edit");
        addButton(edit, 0, "menu-edit-beatmap", "menu-button-edit", "Редактор карт", FontAwesome.Solid.PencilAlt, _ => nativeButtons.OnEditBeatmap?.Invoke());
        addButton(edit, 1, "menu-edit-skin", "", "Редактор скина", FontAwesome.Solid.PaintBrush, _ => nativeButtons.OnEditSkin?.Invoke());
        addButton(edit, 2, "menu-edit-back", "menu-button-back", "Назад", FontAwesome.Solid.ArrowLeft, _ => setPage("main"));

        cookie = new MenuButton
        {
            Name = "legacy-menu-logo", Origin = Anchor.Centre, Position = new Vector2(canvas_width / 2, canvas_height / 2),
            Size = new Vector2(540), CircularHitTest = true, Pressed = _ => setPage(page == "closed" ? "main" : "closed"),
        };
        centre.Add(cookie);
        var pulse = new CookiePulse { RelativeSizeAxes = Axes.Both, Anchor = Anchor.Centre, Origin = Anchor.Centre };
        cookie.Add(pulse);
        pulse.Add(new MenuLogoVisualisation
        {
            RelativeSizeAxes = Axes.Both, Size = new Vector2(0.97f), Anchor = Anchor.Centre, Origin = Anchor.Centre,
            Alpha = 0.43f, Name = "soms-legacy-cookie-spectrum",
        });
        var logoArt = SpriteFor(skin, "menu-osu") ?? SpriteFor(skin, "menu-logo");
        if (logoArt != null) pulse.Add(logoArt);
        else
        {
            pulse.Add(new Circle { RelativeSizeAxes = Axes.Both, Colour = Color4.White });
            pulse.Add(new Circle
            {
                RelativeSizeAxes = Axes.Both, Size = new Vector2(0.9f), Anchor = Anchor.Centre, Origin = Anchor.Centre,
                Colour = new Color4(238, 92, 161, 255),
            });
            pulse.Add(new OsuSpriteText
            {
                Anchor = Anchor.Centre, Origin = Anchor.Centre, Text = "osu!",
                Font = SomsLegacyFont.Font(164, bold: true), Shadow = true,
            });
        }
        // Skin / Legacy preference changes rebuild the scene; navigation animates these same drawables.
        applyPage(animate: false);
        centre.FadeInFromZero(360, Easing.OutQuint);

        MenuPage addPage(string key)
        {
            var result = new MenuPage
            {
                Name = "soms-legacy-menu-page-" + key, Position = new Vector2(menu_x, 192),
                Size = new Vector2(583, 402), Alpha = 0,
            };
            pages.Add(key, result);
            centre.Add(result);
            return result;
        }

        void addButton(MenuPage parent, int index, string name, string asset, string caption, IconUsage icon, Action<UIEvent> action)
        {
            var button = new MenuButton
            {
                Name = "legacy-" + name, Position = new Vector2(0, index * 104), Size = new Vector2(583, 89),
                Pressed = action, HoverSlide = 13,
            };
            var art = string.IsNullOrEmpty(asset) ? null : SpriteFor(skin, asset);
            if (art == null) art = SpriteFor(skin, name);
            if (art != null)
            {
                button.NormalArt = art;
                button.Add(art);
                var over = SpriteFor(skin, asset + "-over");
                if (over != null) { over.Alpha = 0; button.HoverArt = over; button.Add(over); }
            }
            else
            {
                button.Add(new Container
                {
                    RelativeSizeAxes = Axes.Both, Shear = new Vector2(-0.13f, 0), Masking = true, CornerRadius = 5,
                    Child = new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(105, 73, 175, 255) },
                });
                button.Add(text(caption, 256, 23, 248, 35));
                button.Add(new SpriteIcon { Position = new Vector2(514, 23), Size = new Vector2(42), Icon = icon });
            }
            parent.Add(button);
        }
    }

    private void setPage(string value)
    {
        if (page == value) return;
        page = value;
        applyPage(animate: true);
    }

    protected override void Update()
    {
        base.Update();
        if (nativeMultiplayer && nativeButtons != null && owner.IsCurrentScreen()
            && nativeButtons.State is not ButtonSystemState.Multi and not ButtonSystemState.EnteringMode)
        {
            nativeMultiplayer = false;
            page = "play";
            RequestRefresh();
        }
    }

    private void openMultiplayer()
    {
        if (nativeButtons == null) return;
        nativeMultiplayer = true;
        // Keep the native submenu, including SOMSAI and its login checks, entirely intact.
        // The component remains updateable so native Back can return to the legacy Play page.
        RestoreNative();
        ClearInternal();
        pages.Clear(); cookie = null; dim = null;
        Alpha = 0;
        nativeButtons.State = ButtonSystemState.Multi;
    }

    private void applyPage(bool animate)
    {
        foreach (var entry in pages)
        {
            var panel = entry.Value;
            bool shown = entry.Key == page;
            panel.Active = shown;
            if (!animate)
            {
                panel.Alpha = shown ? 1 : 0;
                panel.X = menu_x;
            }
            else if (shown)
            {
                panel.MoveToX(menu_x + 55).MoveToX(menu_x, 440, Easing.OutQuint);
                panel.FadeIn(260, Easing.OutQuint);
            }
            else
            {
                panel.FadeOut(150, Easing.OutQuint);
                panel.MoveToX(menu_x - 30, 220, Easing.OutQuint);
            }
        }
        cookie?.MoveToX(page == "closed" ? canvas_width / 2 : 480, animate ? 560 : 0, Easing.OutQuint);
        dim?.FadeTo(page == "closed" ? 0.04f : 0.24f, animate ? 360 : 0);
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (!LegacyEnabled || nativeMultiplayer || !owner.IsCurrentScreen() || e.Repeat) return base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Enter:
            case Key.P:
                if (page == "play") nativeButtons?.OnSolo?.Invoke();
                else setPage(page == "closed" ? "main" : "play");
                return true;
            case Key.M:
                openMultiplayer();
                return true;
            case Key.E:
                setPage("edit");
                return true;
            case Key.O:
                nativeButtons?.OnSettings?.Invoke();
                return true;
            case Key.Escape:
                setPage(page == "multi" ? "play" : page is "play" or "edit" ? "main" : page == "closed" ? "main" : "closed");
                return true;
        }
        return base.OnKeyDown(e);
    }

    private static MenuButton footerButton(string name, string caption, float x, Action<UIEvent> action, float width = 88) => new()
    {
        Name = name, Anchor = Anchor.BottomRight, Origin = Anchor.BottomCentre,
        Position = new Vector2(x, 0), Size = new Vector2(width, 27), Pressed = action,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(66, 80, 109, 255), Alpha = 0.86f },
            new OsuSpriteText { Anchor = Anchor.Centre, Origin = Anchor.Centre, Text = caption, Font = SomsLegacyFont.Font(14), Shadow = true },
        },
    };

    private static TruncatingSpriteText text(string value, float x, float y, float width, float size, bool bold = false) => new()
    {
        Position = new Vector2(x, y), Width = width, Text = value, Shadow = true,
        Font = SomsLegacyFont.Font(size, bold),
    };

    private sealed partial class MenuScene : Container
    {
        protected override bool OnMouseDown(MouseDownEvent e) => true;
        protected override bool OnClick(ClickEvent e) => true;
    }

    private sealed partial class MenuPage : Container
    {
        public bool Active;
        public override bool PropagatePositionalInputSubTree => Active && base.PropagatePositionalInputSubTree;
        public override bool PropagateNonPositionalInputSubTree => Active && base.PropagateNonPositionalInputSubTree;
    }

    private sealed partial class CookiePulse : BeatSyncedContainer
    {
        protected override void OnNewBeat(int beatIndex, TimingControlPoint timingPoint, EffectControlPoint effectPoint, ChannelAmplitudes amplitudes)
        {
            // Use actual track timing, speed and loudness. Paused music and silent samples do not pulse.
            if (!BeatSyncSource.Clock.IsRunning) return;
            float strength = Math.Clamp(amplitudes.Maximum, 0, 1);
            if (strength < 0.001f) return;
            double duration = Math.Clamp(timingPoint.BeatLength / Math.Max(0.1, BeatSyncSource.Clock.Rate), 100, 700);
            this.ScaleTo(1 + (effectPoint.KiaiMode ? 0.035f : 0.022f) * strength, 35, Easing.OutQuad)
                .Then().ScaleTo(1, duration - 35, Easing.OutQuad);
        }
    }

    private sealed partial class MenuButton : Container
    {
        public Action<UIEvent>? Pressed;
        public Drawable? NormalArt;
        public Drawable? HoverArt;
        public float HoverSlide;
        public bool CircularHitTest;

        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
        {
            if (!CircularHitTest) return base.ReceivePositionalInputAt(screenSpacePos);
            return (ToLocalSpace(screenSpacePos) - DrawSize / 2).LengthSquared <= DrawWidth * DrawWidth / 4;
        }

        protected override bool OnClick(ClickEvent e)
        {
            Pressed?.Invoke(e);
            return true;
        }

        protected override bool OnMouseDown(MouseDownEvent e) => true;

        protected override bool OnHover(HoverEvent e)
        {
            if (HoverArt != null) { NormalArt!.FadeOut(80); HoverArt.FadeIn(80); }
            else this.FadeColour(new Color4(255, 240, 247, 255), 80);
            if (HoverSlide > 0) this.MoveToX(HoverSlide, 160, Easing.OutQuint);
            if (CircularHitTest) this.ScaleTo(1.035f, 220, Easing.OutQuint);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            if (HoverArt != null) { NormalArt!.FadeIn(120); HoverArt.FadeOut(120); }
            else this.FadeColour(Color4.White, 120);
            if (HoverSlide > 0) this.MoveToX(0, 220, Easing.OutQuint);
            if (CircularHitTest) this.ScaleTo(1, 240, Easing.OutQuint);
        }
    }
}
