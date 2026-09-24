#nullable enable
using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays;
using osu.Game.Rulesets;
using osu.Game.Users.Drawables;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>The compact stable user panel, shared by the main menu and song-select footer.</summary>
public sealed partial class SomsLegacyUserPanel : Container
{
    private readonly Container canvas;
    private readonly Container avatar;
    private readonly TruncatingSpriteText username;
    private readonly TruncatingSpriteText performance;
    private readonly TruncatingSpriteText accuracy;
    private readonly TruncatingSpriteText level;
    private readonly TruncatingSpriteText rank;
    private readonly Box progress;
    private IAPIProvider? api;
    private LocalUserStatisticsProvider? statisticsProvider;
    private IBindable<APIUser>? user;
    private IBindable<RulesetInfo>? ruleset;
    private OsuGame? game;
    private LoginOverlay? login;

    public SomsLegacyUserPanel()
    {
        Name = "soms-legacy-user-panel";
        Size = new Vector2(320, 84);
        Child = canvas = new Container
        {
            Size = new Vector2(320, 84),
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = 0.36f },
                rank = label("soms-legacy-user-rank", 156, 28, 159, 45),
                avatar = new Container { Position = new Vector2(2, 4), Size = new Vector2(74), Masking = true },
                username = label("soms-legacy-user-name", 81, 3, 231, 22),
                performance = label("soms-legacy-user-pp", 81, 28, 232, 16),
                accuracy = label("soms-legacy-user-accuracy", 81, 45, 232, 16),
                level = label("soms-legacy-user-level", 81, 63, 54, 13),
                new Container
                {
                    Position = new Vector2(124, 71), Size = new Vector2(190, 10),
                    Masking = true, CornerRadius = 5, BorderThickness = 1, BorderColour = new Color4(165, 165, 165, 255),
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(75, 75, 75, 255) },
                        progress = new Box { RelativeSizeAxes = Axes.Both, Width = 0, Colour = new Color4(244, 208, 91, 255) },
                    },
                },
            },
        };
        rank.Alpha = 0.26f;
    }

    [BackgroundDependencyLoader(true)]
    private void load(IAPIProvider? api, LocalUserStatisticsProvider? statisticsProvider, IBindable<RulesetInfo>? ruleset, OsuGame? game, LoginOverlay? login)
    {
        this.api = api;
        this.statisticsProvider = statisticsProvider;
        this.game = game;
        this.login = login;
        user = api?.LocalUser.GetBoundCopy();
        Patches.SomsAssistModes.Ensure();
        this.ruleset = Patches.SomsAssistModes.Presented.GetBoundCopy();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        user?.BindValueChanged(_ => updateUser(), true);
        this.ruleset?.BindValueChanged(_ => updateStatistics(), true);
        if (statisticsProvider != null) statisticsProvider.StatisticsUpdated += onStatisticsUpdated;
        if (user == null) updateUser();
    }

    private void updateUser()
    {
        bool loggedIn = api?.IsLoggedIn == true;
        username.Text = loggedIn ? user?.Value.Username ?? "Игрок" : "Войти в аккаунт";
        avatar.Clear();
        // Do not block a menu transition while an avatar is being fetched.
        avatar.Add(new DelayedLoadWrapper(new DrawableAvatar(loggedIn ? user?.Value : null)) { RelativeSizeAxes = Axes.Both });
        updateStatistics();
    }

    private void onStatisticsUpdated(UserStatisticsUpdate update)
    {
        if (ruleset?.Value.Equals(update.Ruleset) == true) updateStatistics();
    }

    private void updateStatistics()
    {
        // LocalUser intentionally carries no statistics. Read the same cache as lazer's account panel.
        var stats = ruleset?.Value is { } mode ? statisticsProvider?.GetStatisticsFor(mode) : null;
        performance.Text = stats?.PP is { } pp ? $"Производительность: {pp:N0}pp" : "Производительность: —";
        accuracy.Text = stats != null ? $"Точность: {stats.Accuracy:0.00}%" : "Точность: —";
        level.Text = stats != null ? $"Lv{stats.Level.Current}" : "Lv—";
        rank.Text = stats?.GlobalRank is { } position ? $"#{position:N0}" : "";
        progress.Width = Math.Clamp((stats?.Level.Progress ?? 0) / 100f, 0, 1);
    }

    protected override void Update()
    {
        base.Update();
        canvas.Scale = new Vector2(Math.Max(0.01f, Math.Min(DrawWidth / 320, DrawHeight / 84)));
    }

    protected override bool OnClick(ClickEvent e)
    {
        if (api?.IsLoggedIn == true && user?.Value is { } currentUser) game?.ShowUser(currentUser);
        else login?.ToggleVisibility();
        return true;
    }

    protected override bool OnHover(HoverEvent e)
    {
        canvas.FadeColour(new Color4(255, 238, 247, 255), 100);
        return true;
    }

    protected override void OnHoverLost(HoverLostEvent e) => canvas.FadeColour(Color4.White, 150);

    protected override void Dispose(bool isDisposing)
    {
        if (statisticsProvider != null) statisticsProvider.StatisticsUpdated -= onStatisticsUpdated;
        user?.UnbindAll();
        ruleset?.UnbindAll();
        base.Dispose(isDisposing);
    }

    private static TruncatingSpriteText label(string name, float x, float y, float width, float size) => new()
    {
        Name = name, Position = new Vector2(x, y), Width = width, Shadow = true,
        Font = SomsLegacyFont.Font(size),
    };
}
