#nullable enable
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Screens;
using osu.Game.Graphics;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Online;
using osu.Game.Online.Chat;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Screens;
using osu.Game.Users;
using osu.Game.Users.Drawables;
using osuTK;
using osuTK.Graphics;
using static osu.Game.Rulesets.EnhancedAuth.UI.SomsNativeMatchScreen;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>In-game SOMSAI records browser. Own ranking is outside the scrolling list.</summary>
internal sealed partial class SomsAiRecordsScreen : OsuScreen
{
    private const double transition_duration = 360;
    private const float transition_distance = 110;
    public override string Title => matchId.HasValue ? $"SOMSAI · Матч #{matchId}" : "SOMSAI · Рейтинг";
    public override bool ShowFooter => true;
    [Resolved] private IAPIProvider api { get; set; } = null!;
    [Resolved(CanBeNull = true)] private ILinkHandler? links { get; set; }
    [Cached] private readonly osu.Game.Overlays.OverlayColourProvider colours = new(osu.Game.Overlays.OverlayColourScheme.Blue);
    private readonly int rulesetId, variantId;
    private readonly int? matchId;
    private string format;
    private int page = 1;
    private readonly FillFlowContainer rows = Flow();
    private readonly Container own = new() { RelativeSizeAxes = Axes.X, Height = 72, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft };
    private readonly OsuSpriteText status = Text("Загрузка…", 18);
    private SomsAiDataRequest? request;
    private readonly OsuScrollContainer scroll;

    public SomsAiRecordsScreen(int rulesetId, int variantId, string format, int? matchId = null)
    {
        (this.rulesetId, this.variantId, this.format, this.matchId) = (rulesetId, variantId, format, matchId);
        var heading = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 10),
        };
        heading.Add(Text(Title, 30));
        if (!matchId.HasValue)
        {
            var tabs = new FillFlowContainer { RelativeSizeAxes = Axes.X, Height = 42, Direction = FillDirection.Horizontal, Spacing = new Vector2(10) };
            void rebuildTabs()
            {
                tabs.Clear();
                foreach (string choice in new[] { "1v1", "2v2" })
                    tabs.Add(new SomsAiOceanButton(choice == this.format)
                    {
                        RelativeSizeAxes = Axes.None,
                        Width = 130,
                        Height = 40,
                        Text = "Рейтинг " + choice,
                        Action = () =>
                        {
                            if (this.format == choice) return;
                            this.format = choice;
                            page = 1;
                            rebuildTabs();
                            refresh();
                        },
                    });
            }
            rebuildTabs();
            heading.Add(tabs);
        }
        heading.Add(status);
        InternalChildren = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = SomsAiOceanTheme.Ink },
            new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Horizontal = 40, Top = 25, Bottom = 80 },
                Children = new Drawable[]
                {
                    heading,
                    new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = matchId.HasValue ? 82 : 125, Bottom = matchId.HasValue ? 0 : 84 },
                        Child = scroll = new OsuScrollContainer { RelativeSizeAxes = Axes.Both, Child = rows } },
                    own,
                } },
        };
    }
    protected override void LoadComplete() { base.LoadComplete(); refresh(); }
    public override void OnEntering(ScreenTransitionEvent e)
    {
        this.FadeOut();
        this.FadeIn(transition_duration, Easing.OutQuint);
        this.MoveToX(transition_distance);
        this.MoveToX(0, transition_duration, Easing.OutQuint);
        base.OnEntering(e);
    }
    public override void OnSuspending(ScreenTransitionEvent e)
    {
        this.MoveToX(-transition_distance * .45f, transition_duration, Easing.OutQuint);
        this.FadeTo(.35f, transition_duration, Easing.OutQuint);
        base.OnSuspending(e);
    }
    public override void OnResuming(ScreenTransitionEvent e)
    {
        this.MoveToX(0, transition_duration, Easing.OutQuint);
        this.FadeIn(transition_duration, Easing.OutQuint);
        base.OnResuming(e);
    }
    public override bool OnExiting(ScreenExitEvent e)
    {
        this.MoveToX(transition_distance, transition_duration, Easing.OutQuint);
        this.FadeOut(transition_duration, Easing.OutQuint);
        return base.OnExiting(e);
    }
    private void refresh()
    {
        request?.Cancel();
        rows.Clear(); own.Clear();
        status.Text = "Загрузка…";
        var current = request = new SomsAiDataRequest(matchId.HasValue ? $"matches/{matchId}" : $"leaderboard?ruleset_id={rulesetId}&variant_id={variantId}&format={format}&page={page}");
        current.Success += data => Schedule(() =>
        {
            if (request != current) return;
            request = null;
            if (matchId.HasValue) showMatch(data.ToObject<SomsAiMatch>()!);
            else
            {
                status.Text = $"{format} · MMR · страница {page}";
                foreach (var item in data["items"] ?? new JArray()) rows.Add(rankingRow(item));
                if (data["self"] is JToken self) own.Add(rankingRow(self, true));
                if (page > 1) rows.Add(Button("← Предыдущая страница", () => { page--; refresh(); }));
                if (data.Value<bool>("has_more")) rows.Add(Button("Следующая страница →", () => { page++; refresh(); }));
            }
            scroll.ScrollToStart();
        });
        current.Failure += _ => Schedule(() =>
        { if (request == current) { request = null; status.Text = "Не удалось загрузить записи."; rows.Add(Button("Повторить", refresh)); } });
        api.Queue(current);
    }
    private Drawable rankingRow(JToken data, bool local = false)
    {
        var user = data["user"]!.ToObject<APIUser>()!;
        user.AvatarUrl = new Uri(new Uri(api.Endpoints.APIUrl), user.AvatarUrl).AbsoluteUri;
        double rating = data.Value<double>("rating");
        var rank = SomsAiRank.FromRating(rating);
        var caption = Flow(); caption.Padding = new MarginPadding { Left = 80, Top = 10, Right = 12 }; caption.Spacing = new Vector2(0, 3);
        caption.Add(new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = $"#{data.Value<int>("rank")}  {user.Username}" + (local ? " · ВЫ" : ""), Font = OsuFont.GetFont(size: 20, weight: FontWeight.SemiBold) });
        caption.Add(new OsuSpriteText { Text = $"{rating:N0} MMR · {rank.Name} · {data.Value<int>("wins")}W / {data.Value<int>("losses")}L", Font = OsuFont.GetFont(size: 16), Colour = rank.Colour });
        return new osu.Game.Graphics.Containers.OsuClickableContainer { RelativeSizeAxes = Axes.X, Height = 70, Masking = true, CornerRadius = 8,
            Action = () => links?.HandleLink(new LinkDetails(LinkAction.OpenUserProfile, user)),
            BorderThickness = local ? 2 : 0, BorderColour = rank.Colour, Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(8, 69, 85, 255) },
                new Container { Position = new Vector2(10, 9), Size = new Vector2(52), Masking = true, CornerRadius = 9,
                    Child = new DelayedLoadWrapper(new DrawableAvatar(user)) { RelativeSizeAxes = Axes.Both } }, caption,
            } };
    }
    private void showMatch(SomsAiMatch match)
    {
        string team(int? id) => id.HasValue ? string.Join(" + ", match.Teams.FirstOrDefault(t => t.Id == id)?.Members.Select(p => SomsAiRank.DisplayName(p.Username)) ?? Array.Empty<string>()) : "Тайбрейкер";
        int? localTeam = match.Teams.FirstOrDefault(t => t.Members.Any(p => p.Id == api.LocalUser.Value.OnlineID))?.Id;
        status.Text = $"{match.Format} · {match.Wins.ElementAtOrDefault(0)} : {match.Wins.ElementAtOrDefault(1)} · BO{match.BestOf}";
        if (match.Teams.Count >= 2)
            rows.Add(matchupHeader(match.Teams[0], match.Teams[1], match.Wins.ElementAtOrDefault(0), match.Wins.ElementAtOrDefault(1)));
        foreach (var ban in match.DraftHistory.Where(e => e.Value<string>("action") == "ban"))
        {
            int? bannedBy = ban.Value<int?>("team_id");
            addEvent(match, ban.Value<string>("slot_id"), $"БАН · {team(bannedBy)}", null,
                bannedBy is { } side ? teamColour(side) : SomsAiOceanTheme.Muted);
        }
        foreach (var round in match.History.OrderBy(r => r.Value<int>("round")))
        {
            int? winner = round.Value<int?>("winner_team_id");
            string result = winner.HasValue ? "Победа: " + team(winner) : "Ничья";
            Color4 resultColour = winner == null || localTeam == null ? SomsAiOceanTheme.Muted
                : winner == localTeam ? new Color4(103, 239, 164, 255) : new Color4(255, 103, 119, 255);
            var details = Flow();
            var scores = round["team_scores"]?.ToObject<long[]>() ?? Array.Empty<long>();
            long difference = localTeam == 1
                ? scores.ElementAtOrDefault(1) - scores.ElementAtOrDefault(0)
                : scores.ElementAtOrDefault(0) - scores.ElementAtOrDefault(1);
            var totals = Text($"{scores.ElementAtOrDefault(0):N0} : {scores.ElementAtOrDefault(1):N0} · Δ {difference:+#,0;-#,0;+0} · {result}", 22);
            totals.Font = OsuFont.GetFont(size: 22, weight: FontWeight.SemiBold);
            totals.Colour = resultColour;
            details.Add(totals);
            foreach (var player in round["players"] ?? new JArray())
                details.Add(playerResultRow(match, player));
            if (round.Value<bool>("forfeit")) details.Add(Text("Технический результат · " + round.Value<string>("reason"), 17));
            addEvent(match, round.Value<string>("slot_id"), $"РАУНД {round.Value<int>("round")} · ПИК: {team(round.Value<int?>("picked_by_team"))}", details, resultColour);
        }
        if (match.History.Count == 0 && match.DraftHistory.Count == 0) rows.Add(Text("В этом матче карты ещё не игрались.", 18));
    }

    private Drawable matchupHeader(SomsAiTeam red, SomsAiTeam blue, int redWins, int blueWins)
    {
        var header = new Container { RelativeSizeAxes = Axes.X, Height = 116 };
        header.Add(teamPanel(red, redWins, true));
        var right = teamPanel(blue, blueWins, false);
        right.Anchor = right.Origin = Anchor.TopRight;
        header.Add(right);
        var versus = Text("VS", 31);
        versus.Font = OsuFont.GetFont(size: 31, weight: FontWeight.Bold);
        versus.Anchor = versus.Origin = Anchor.Centre;
        header.Add(versus);
        return header;
    }

    private Drawable teamPanel(SomsAiTeam team, int wins, bool red)
    {
        Color4 colour = red ? SomsAiOceanTheme.Coral : SomsAiOceanTheme.Aqua;
        string name = string.IsNullOrWhiteSpace(team.Name) ? (red ? "Красная команда" : "Синяя команда") : team.Name;
        var players = new FillFlowContainer { RelativeSizeAxes = Axes.X, Height = 57, Direction = FillDirection.Horizontal, Spacing = new Vector2(8) };
        foreach (var player in team.Members)
            players.Add(playerChip(player, colour));
        return new Container
        {
            RelativeSizeAxes = Axes.X,
            Width = .475f,
            Height = 110,
            Masking = true,
            CornerRadius = 12,
            BorderThickness = 2,
            BorderColour = colour,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = .2f },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding(12),
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 7),
                    Children = new Drawable[]
                    {
                        new OsuSpriteText { Text = $"{name}  ·  {wins}", Font = OsuFont.GetFont(size: 22, weight: FontWeight.Bold), Colour = colour },
                        players,
                    },
                },
            },
        };
    }

    private Drawable playerChip(SomsPlayer player, Color4 colour)
    {
        string displayName = SomsAiRank.DisplayName(player.Username);
        var user = new APIUser
        {
            Id = player.Id,
            Username = displayName,
            AvatarUrl = new Uri(new Uri(api.Endpoints.APIUrl), player.AvatarUrl ?? $"/users/{player.Id}/avatar").AbsoluteUri,
        };
        Drawable[] children =
        {
            new Container
            {
                Size = new Vector2(46), Masking = true, CornerRadius = 9, BorderThickness = 2, BorderColour = colour,
                Child = new DelayedLoadWrapper(new DrawableAvatar(user)) { RelativeSizeAxes = Axes.Both },
            },
            new TruncatingSpriteText
            {
                X = 54, Y = 12, Width = 88, Text = displayName,
                Font = OsuFont.GetFont(size: 16, weight: FontWeight.SemiBold), Colour = Color4.White,
            },
        };
        if (player.IsBot)
            return new Container { Name = "somsai-player-bot", Width = 145, Height = 50, Children = children };

        return new osu.Game.Graphics.Containers.OsuClickableContainer
        {
            Name = "somsai-player-human",
            Width = 145,
            Height = 50,
            Action = () => links?.HandleLink(new LinkDetails(LinkAction.OpenUserProfile, user)),
            Children = children,
        };
    }

    private Drawable playerResultRow(SomsAiMatch match, JToken result)
    {
        int id = result.Value<int>("user_id");
        SomsPlayer? member = match.Teams.SelectMany(t => t.Members).FirstOrDefault(p => p.Id == id);
        bool isBot = result.Value<bool>("is_bot") || member?.IsBot == true;
        string name = SomsAiRank.DisplayName(member?.Username ?? $"#{id}");
        string country = normaliseCountry(member?.CountryCode);
        int teamId = match.Teams.FirstOrDefault(t => t.Members.Any(p => p.Id == id))?.Id ?? 0;
        Color4 colour = teamColour(teamId);
        long score = result.Value<long>("score");
        double? rawAccuracy = result["accuracy"]?.Value<double?>();
        double? accuracy = rawAccuracy.HasValue ? (rawAccuracy <= 1.0001 ? rawAccuracy * 100 : rawAccuracy) : null;
        int? combo = result["max_combo"]?.Value<int?>();
        JObject? statistics = result["statistics"] as JObject;
        int great = statistic(statistics, "great");
        int ok = statistic(statistics, "ok");
        int meh = statistic(statistics, "meh");
        int miss = statistic(statistics, "miss");
        int perfect = statistic(statistics, "perfect");
        int good = statistic(statistics, "good");
        string hitCounts = statistics == null || !statistics.Properties().Any()
            ? "Статистика старого матча недоступна"
            : $"GREAT {great:N0}  ·  OK {ok:N0}  ·  MEH {meh:N0}  ·  MISS {miss:N0}"
              + (perfect > 0 || good > 0 ? $"  ·  PERFECT {perfect:N0}  ·  GOOD {good:N0}" : "");
        string grade = result.Value<string>("rank") ?? (result.Value<bool?>("passed") == false ? "F" : "—");
        string displayGrade = gradeLabel(grade);
        Color4 gradeAccent = gradeColour(grade);
        string secondaryStats = $"{(combo.HasValue ? $"{combo:N0}x" : "—x")}   ·   {(accuracy.HasValue ? $"{accuracy:0.00}%" : "—%")}";

        var row = new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 68,
            Masking = true,
            CornerRadius = 7,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(2, 29, 42, 190) },
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = colour },
                new OsuSpriteText
                {
                    X = 340, Y = 39, RelativeSizeAxes = Axes.X, Width = .42f,
                    Text = hitCounts, Font = OsuFont.GetFont(size: 12, weight: FontWeight.SemiBold), Colour = SomsAiOceanTheme.Muted,
                },
                new OsuSpriteText
                {
                    Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, Position = new Vector2(-80, -9),
                    Text = $"{score:N0}", Font = OsuFont.GetFont(size: 30, weight: FontWeight.Bold), Colour = colour,
                },
                new OsuSpriteText
                {
                    Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, Position = new Vector2(-80, 18),
                    Text = secondaryStats, Font = OsuFont.GetFont(size: 14, weight: FontWeight.SemiBold), Colour = Color4.White,
                },
                new Container
                {
                    Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -14,
                    Size = new Vector2(50, 32), Masking = true, CornerRadius = 16,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = gradeAccent },
                        new OsuSpriteText
                        {
                            Anchor = Anchor.Centre, Origin = Anchor.Centre, Text = displayGrade,
                            Font = OsuFont.GetFont(size: displayGrade.Length > 1 ? 15 : 19, weight: FontWeight.Bold),
                            Colour = new Color4(24, 37, 42, 255),
                        },
                    },
                },
            },
        };

        Enum.TryParse(country, out CountryCode flagCountry);
        Drawable flag = new DrawableFlag(flagCountry)
        {
            Anchor = Anchor.Centre, Origin = Anchor.Centre,
            Size = new Vector2(32, 21),
        };
        if (isBot || country == "XX")
            row.Add(new Container { Position = new Vector2(12, 0), Size = new Vector2(38, 68), Child = flag });
        else
            row.Add(new osu.Game.Graphics.Containers.OsuClickableContainer
            {
                Position = new Vector2(12, 0), Size = new Vector2(38, 68),
                Action = () => openCountryRanking(match.RulesetId, country), Child = flag,
            });

        Drawable[] identity =
        {
            new TruncatingSpriteText
            {
                Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                RelativeSizeAxes = Axes.X, Width = .95f, Text = name,
                Font = OsuFont.GetFont(size: 22, weight: FontWeight.Bold), Colour = colour,
            },
        };
        if (isBot || member == null || member.Id <= 0)
            row.Add(new Container { Name = "somsai-player-bot", Position = new Vector2(56, 0), Width = 280, Height = 68, Children = identity });
        else
        {
            var profile = new APIUser
            {
                Id = member.Id,
                Username = name,
                AvatarUrl = new Uri(new Uri(api.Endpoints.APIUrl), member.AvatarUrl ?? $"/users/{member.Id}/avatar").AbsoluteUri,
            };
            row.Add(new osu.Game.Graphics.Containers.OsuClickableContainer
            {
                Name = "somsai-player-human",
                Position = new Vector2(56, 0), Width = 280, Height = 68,
                Action = () => links?.HandleLink(new LinkDetails(LinkAction.OpenUserProfile, profile)), Children = identity,
            });
        }
        return row;
    }

    private void openCountryRanking(int ruleset, string country)
    {
        string mode = ruleset switch { 1 => "taiko", 2 => "fruits", 3 => "mania", _ => "osu" };
        links?.HandleLink(new LinkDetails(
            LinkAction.External,
            $"https://osu.ppy.sh/rankings/{mode}/performance?country={Uri.EscapeDataString(country)}"));
    }

    private static int statistic(JObject? statistics, string name) =>
        statistics?.GetValue(name, StringComparison.OrdinalIgnoreCase)?.Value<int>() ?? 0;

    private static string normaliseCountry(string? code) =>
        code?.Length == 2 && code.All(char.IsLetter) ? code.ToUpperInvariant() : "XX";

    private static string gradeLabel(string grade) => grade.ToUpperInvariant() switch
    {
        "XH" => "SSH",
        "X" => "SS",
        _ => grade.ToUpperInvariant(),
    };

    private static Color4 gradeColour(string grade) => grade.ToUpperInvariant() switch
    {
        "XH" => new Color4(234, 34, 164, 255),
        "X" => new Color4(247, 55, 178, 255),
        "SH" => new Color4(41, 198, 218, 255),
        "S" => new Color4(34, 205, 202, 255),
        "A" => new Color4(137, 224, 36, 255),
        "B" => new Color4(238, 197, 38, 255),
        "C" => new Color4(244, 152, 65, 255),
        "D" or "F" => new Color4(227, 84, 111, 255),
        _ => SomsAiOceanTheme.Muted,
    };

    private void addEvent(SomsAiMatch match, string? slotId, string title, Drawable? details, Color4 colour)
    {
        var slot = match.Slots.FirstOrDefault(s => s.Id == slotId);
        var content = Flow(); content.Padding = new MarginPadding(14);
        content.Spacing = new Vector2(0, 4);
        content.Add(new OsuSpriteText { Text = title, Font = OsuFont.GetFont(size: 19, weight: FontWeight.SemiBold), Colour = Color4.White });
        if (slot != null) content.Add(new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = $"{slot.Label} · {slot.Artist} — {slot.Title} [{slot.Version}]", Font = OsuFont.GetFont(size: 24, weight: FontWeight.SemiBold), Colour = Color4.White });
        if (details != null) content.Add(details);
        var card = new osu.Game.Graphics.Containers.OsuClickableContainer
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Masking = true, CornerRadius = 9,
            BorderThickness = 2, BorderColour = colour,
            Action = slot == null ? null : () => links?.HandleLink(new LinkDetails(LinkAction.OpenBeatmap, slot.BeatmapId.ToString())),
        };
        card.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(10, 65, 81, 255) });
        if (slot?.BeatmapSetId > 0)
            card.Add(new DelayedLoadWrapper(new OnlineBeatmapSetCover(new APIBeatmapSet
            {
                OnlineID = slot.BeatmapSetId,
                Covers = new BeatmapSetOnlineCovers { Card = $"https://assets.ppy.sh/beatmaps/{slot.BeatmapSetId}/covers/card@2x.jpg" },
            }, BeatmapSetCoverType.Card) { RelativeSizeAxes = Axes.Both, FillMode = FillMode.Fill }) { RelativeSizeAxes = Axes.Both, Alpha = .2f });
        card.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = .13f });
        card.Add(content);
        rows.Add(card);
    }
    private static Color4 teamColour(int team) => team == 0 ? SomsAiOceanTheme.Coral : SomsAiOceanTheme.Aqua;
    protected override void Dispose(bool isDisposing) { request?.Cancel(); base.Dispose(isDisposing); }
}
