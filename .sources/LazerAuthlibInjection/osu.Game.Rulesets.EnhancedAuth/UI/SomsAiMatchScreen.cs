#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Extensions.Color4Extensions;
using Newtonsoft.Json.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Screens;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Shapes;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Online;
using osu.Game.Online.Chat;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Screens.OnlinePlay.Multiplayer;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays.BeatmapSet.Buttons;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Users.Drawables;
using osu.Game.Screens.OnlinePlay.Matchmaking.RankedPlay.Intro;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>The match owns its screen, native room and preview audio independently of the lobby.</summary>
public sealed partial class SomsAiMatchScreen : SomsAiScreen
{
    public SomsAiMatchScreen(SomsAiState initial) : base(initial) { }
}

public partial class SomsAiScreen
{
    [Resolved(CanBeNull = true)] private ILinkHandler? mapLinks { get; set; }
    private string? roundHistoryIdentity;
    private bool matchOutcomeShown;
    protected override bool KeepMultiplayerResults => true;
    protected override MultiplayerPlayer CreateGameplay(Room room, PlaylistItem item, MultiplayerRoomUser[] users)
        => new SomsAiMultiplayerPlayer(room, item, users, state.Match!, confirmed => OnUpdateThread(() =>
        {
            state.Match = confirmed;
            showConfirmedMatchOutcome(confirmed, Api.LocalUser.Value.Id);
        }));

    private void openMap(SomsAiSlot slot) => mapLinks?.HandleLink(new LinkDetails(LinkAction.OpenBeatmap, slot.BeatmapId.ToString()));

    private void renderRoundHistory(SomsAiMatch match, int localId)
    {
        var bans = match.DraftHistory.Count > 0 ? match.DraftHistory : match.Slots.Where(s => s.Status == "banned")
            .Select(s => new JObject { ["action"] = "ban", ["slot_id"] = s.Id, ["team_id"] = s.SelectedByTeam }).ToList();
        string identity = match.Id + ":" + string.Join(";", bans.Concat(match.History).Select(r => r.ToString(Newtonsoft.Json.Formatting.None)));
        if (roundHistoryIdentity == identity) return;
        roundHistoryIdentity = identity;
        battleFooter.Clear();
        int? localTeam = match.Teams.FirstOrDefault(t => t.Members.Any(p => p.Id == localId))?.Id;
        foreach (var round in bans.Concat(match.History))
        {
            var slot = match.Slots.FirstOrDefault(s => s.Id == round.Value<string>("slot_id"));
            if (slot == null) continue;
            int? winner = round.Value<int?>("winner_team_id");
            bool ban = round.Value<string>("action") == "ban";
            int? pickedBy = ban ? round.Value<int?>("team_id") : round.Value<int?>("picked_by_team") ?? slot.SelectedByTeam;
            var picker = match.Teams.FirstOrDefault(t => t.Id == pickedBy);
            string pickerName = picker?.Name ?? "";
            var colour = winner == null || localTeam == null ? Color4.Gray : winner == localTeam ? new Color4(110, 230, 165, 255) : new Color4(255, 105, 120, 255);
            if (ban && pickedBy is { } team) colour = teamColour(team);
            var scores = round["team_scores"] as JArray;
            long a = scores?.ElementAtOrDefault(0)?.Value<long>() ?? 0, b = scores?.ElementAtOrDefault(1)?.Value<long>() ?? 0;
            long difference = localTeam == 1 ? b - a : a - b;
            string totals = ban ? "Бан" : round.Value<bool?>("forfeit") == true ? "Технический результат · " + round.Value<string>("reason")
                : $"{a:N0} : {b:N0}   ·   Δ {difference:+#,0;-#,0;0}";
            var card = new OsuClickableContainer
            {
                RelativeSizeAxes = Axes.X, Height = 88, Masking = true, CornerRadius = 5,
                BorderThickness = 2, BorderColour = colour, Action = () => openMap(slot),
                EdgeEffect = new osu.Framework.Graphics.Effects.EdgeEffectParameters
                { Type = osu.Framework.Graphics.Effects.EdgeEffectType.Glow, Colour = colour.Opacity(.35f), Radius = 6 },
            };
            if (slot.BeatmapSetId > 0) card.Add(MapCard.CreateCover(slot.BeatmapSetId));
            card.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black.Opacity(.72f) });
            card.Add(new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding(10), Children = new Drawable[]
            {
                new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = $"{(ban ? "БАН" : round["round"]?.ToString())} · {slot.Label} · {slot.Title}", Font = OsuFont.GetFont(size: 15) },
                new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Y = 25, Text = totals, Font = OsuFont.GetFont(size: 14), Colour = colour },
                new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Y = 48, Text = pickedBy.HasValue ? $"{(ban ? "Забанил" : "Пик")}: {pickerName}" : "Тайбрейкер", Font = OsuFont.GetFont(size: 13), Colour = pickedBy is { } owner ? teamColour(owner) : Color4.White },
            } });
            battleFooter.Add(card);
        }
        if (match.History.Count == 0 && bans.Count == 0) battleFooter.Add(Paragraph("Здесь появятся баны и результаты сыгранных карт.", 15));
    }

    private void showConfirmedMatchOutcome(SomsAiMatch match, int localId)
    {
        if (matchOutcomeShown || !match.IsFinished) return;
        matchOutcomeShown = true;
        var reveal = new SomsAiOutcomeAnimation(match, localId);
        if (profileGame != null) profileGame.Add(reveal);
        else AddInternal(reveal);
    }
    [Resolved(CanBeNull = true)]
    private OsuGame? profileGame { get; set; }
    private TeamHeader[] teamHeaders = Array.Empty<TeamHeader>();
    private readonly FillFlowContainer battleFooter = Flow();
    private readonly FillFlowContainer mapBoard = Flow();
    private readonly Dictionary<string, MapCard> mapCards = new();
    private string? mapIdentity;
    [Resolved] private AudioManager introAudio { get; set; } = null!;
    private Container? matchIntro;
    private bool introPlayed;

    private static SomsPlayer? captain(SomsAiTeam team) => team.Members.FirstOrDefault(p => p.Id == team.CaptainId) ?? team.Members.FirstOrDefault();
    private static string teamName(SomsAiTeam team)
    {
        var leader = captain(team);
        string name = leader?.OfficialUsername ?? leader?.Username ?? team.Name;
        return team.Members.Count > 1 ? "Team " + name : name;
    }

    private void playMatchIntro()
    {
        if (introPlayed || state.Match is not { } match || match.IsFinished || match.Teams.Count != 2 || match.Teams.Any(t => t.Members.Count == 0)) return;
        introPlayed = true;
        APIUser identity(SomsAiTeam team)
        {
            var player = captain(team)!;
            return new APIUser { Id = player.Id, Username = teamName(team), AvatarUrl = new Uri(new Uri(Api.Endpoints.APIUrl), player.AvatarUrl ?? $"/users/{player.Id}/avatar").AbsoluteUri };
        }
        // Native VS draws its first participant blue and its second red.
        var sequence = new VsSequence(new UserWithRating(identity(match.Teams[1]), 0), new UserWithRating(identity(match.Teams[0]), 0));
        matchIntro = new Container
        {
            RelativeSizeAxes = Axes.Both, Depth = -1000,
            Child = new Box { RelativeSizeAxes = Axes.Both, Colour = SomsAiOceanTheme.Ink },
        };
        oceanContent.Hide();
        sequence.OnLoadComplete += _ =>
        {
            foreach (var text in introDescendants(sequence).OfType<OsuSpriteText>().Where(t => t.Text.ToString().StartsWith("Rating:")))
                text.Text = $"{match.Format} · BO{match.BestOf}";
            double delay = 0;
            sequence.Play(ref delay, out double impact);
            var windup = introAudio.Samples.Get("Multiplayer/Matchmaking/Ranked/vs-windup");
            Scheduler.AddDelayed(() => { if (Alive && this.IsCurrentScreen()) windup?.Play(); }, Math.Max(0, impact - (windup?.Length ?? 0)));
            Scheduler.AddDelayed(() => { if (Alive && this.IsCurrentScreen()) introAudio.Samples.Get("Multiplayer/Matchmaking/Ranked/vs-impact")?.Play(); }, impact);
            Scheduler.AddDelayed(() =>
            {
                if (!Alive) return;
                matchIntro?.FadeOut(200).Expire();
                if (this.IsCurrentScreen()) showOcean();
            }, delay);
        };
        AddInternal(matchIntro);
        // Avatar/cover decoding and intro resources must not block the update thread.
        LoadComponentAsync(sequence, loaded =>
        {
            if (!Alive || !this.IsCurrentScreen()) { loaded.Dispose(); showOcean(); return; }
            matchIntro.Add(loaded);
        });
    }

    private static IEnumerable<Drawable> introDescendants(Drawable node)
    {
        yield return node;
        if (node is CompositeDrawable composite)
            foreach (var child in Patches.SomsLegacyInterfacePatch.Children(composite))
                foreach (var descendant in introDescendants(child)) yield return descendant;
    }

    private void buildMatchScreen()
    {
        // BuildLayout owns the fixed arena panes. Only their contents change on polls.
    }

    private void renderMatchHeader(SomsAiMatch match)
    {
        // Polls replace DTOs, not the drawable identities. Keep avatars loaded across
        // readiness, score and pick/ban updates.
        if (teamHeaders.Length != match.Teams.Count)
        {
            arenaTeams.Clear();
            arenaRoster.Clear();
            teamHeaders = match.Teams.Select((_, index) => new TeamHeader(index)).ToArray();
            foreach (var header in teamHeaders)
            {
                arenaTeams.Add(header);
                arenaRoster.Add(header.Roster);
            }
        }
        for (int i = 0; i < teamHeaders.Length; i++)
        {
            var team = match.Teams[i];
            var leader = captain(team);
            APIUser identity(SomsPlayer player) => new()
            {
                Id = player.Id, Username = player.OfficialUsername ?? player.Username,
                AvatarUrl = new Uri(new Uri(Api.Endpoints.APIUrl), player.AvatarUrl ?? $"/users/{player.Id}/avatar").AbsoluteUri,
            };
            var user = leader == null ? null : identity(leader);
            teamHeaders[i].SetState(team, user, match.BestOf / 2 + 1, match.Wins.ElementAtOrDefault(i), Api.LocalUser.Value.OnlineID, identity, player =>
            {
                if (profileGame == null) return;
                if (player.OfficialId is > 0)
                    SomsOfficialProfileOverlay.Open(profileGame, new APIUser { Id = player.OfficialId.Value, Username = player.OfficialUsername ?? player.Username });
                else if (!player.IsBot)
                    profileGame.ShowUser(identity(player));
            });
        }
    }

    private sealed partial class TeamHeader : Container
    {
        private readonly ClickableContainer identity;
        private readonly Container avatar;
        private readonly TruncatingSpriteText name;
        private readonly FillFlowContainer points;
        private readonly FillFlowContainer members = Flow();
        private readonly List<Box> pointFills = new();
        private readonly Dictionary<int, OsuTextFlowContainer> readyLabels = new();
        private readonly Dictionary<int, ClickableContainer> rosterRows = new();
        private readonly Color4 colour;
        private readonly OsuSpriteText readiness;
        private readonly Container nameArea;
        private readonly int side;
        public Drawable Roster => members;
        private string? avatarKey;
        private string? rosterKey;

        public TeamHeader(int index)
        {
            side = index;
            Height = 104;
            if (index == 1) Anchor = Origin = Anchor.TopRight;
            colour = teamColour(index);
            Add(new Box { RelativeSizeAxes = Axes.Both, Colour = index == 0
                ? ColourInfo.GradientHorizontal(colour.Opacity(.18f), arenaInk.Opacity(.3f))
                : ColourInfo.GradientHorizontal(arenaInk.Opacity(.3f), colour.Opacity(.18f)) });
            Add(new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = colour,
                Anchor = index == 0 ? Anchor.TopLeft : Anchor.TopRight, Origin = index == 0 ? Anchor.TopLeft : Anchor.TopRight });
            Add(identity = new ClickableContainer { RelativeSizeAxes = Axes.Both });
            identity.Add(avatar = new Container
            {
                Size = new Vector2(58), Position = new Vector2(index == 0 ? 14 : -14, 16), CornerRadius = 4, Masking = true,
                Anchor = index == 0 ? Anchor.TopLeft : Anchor.TopRight, Origin = index == 0 ? Anchor.TopLeft : Anchor.TopRight,
            });
            identity.Add(nameArea = new Container
            {
                RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = index == 0 ? 86 : 14, Right = index == 0 ? 14 : 86, Top = 17 },
                Children = new Drawable[]
                {
                    name = new TruncatingSpriteText { Colour = Color4.White, Font = OsuFont.GetFont(size: 23, weight: FontWeight.Bold) },
                    points = new FillFlowContainer { Y = 34, AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new Vector2(5, 0) },
                    readiness = arenaLabel("", 12, colour),
                },
            });
            if (index == 1)
            {
                name.Anchor = name.Origin = Anchor.TopRight;
                points.Anchor = points.Origin = Anchor.TopRight;
                readiness.Anchor = readiness.Origin = Anchor.TopRight;
            }
            readiness.Y = 62;
            members.Spacing = new Vector2(0, 6);
        }

        public void SetState(SomsAiTeam team, APIUser? user, int targetWins, int wins, int localId, Func<SomsPlayer, APIUser> identityFor, Action<SomsPlayer> openProfile)
        {
            identity.Action = () => { if (captain(team) is { } leader) openProfile(leader); };
            name.Text = teamName(team);
            string key = user == null ? "" : $"{user.Id}:{user.AvatarUrl}";
            if (avatarKey != key)
            {
                avatarKey = key;
                avatar.Clear();
                if (user != null) avatar.Add(new DelayedLoadWrapper(new DrawableAvatar(user)) { RelativeSizeAxes = Axes.Both });
            }
            if (pointFills.Count != targetWins)
            {
                points.Clear(); pointFills.Clear();
                for (int i = 0; i < targetWins; i++)
                {
                    var fill = new Box { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = .12f };
                    points.Add(new Container { Size = new Vector2(16), Masking = true, BorderThickness = 1, BorderColour = colour.Opacity(.6f), Child = fill });
                    pointFills.Add(fill);
                }
            }
            for (int i = 0; i < pointFills.Count; i++) pointFills[i].FadeTo((side == 0 ? i < wins : i >= pointFills.Count - wins) ? 1 : .12f, 200);
            string roster = string.Join('|', team.Members.Select(player => $"{player.Id}:{player.AvatarUrl}"));
            if (rosterKey != roster)
            {
                rosterKey = roster; members.Clear(); readyLabels.Clear(); rosterRows.Clear();
                members.Add(arenaLabel(teamName(team).ToUpperInvariant(), 12, colour));
                if (team.Members.Count == 0) members.Add(Paragraph("Ожидаем игроков…", 15));
                foreach (var player in team.Members)
                {
                    var label = Paragraph("", 14); readyLabels.Add(player.Id, label);
                    var row = new ClickableContainer
                    {
                        RelativeSizeAxes = Axes.X, Height = 46, Name = "somsai-team-player",
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = colour.Opacity(.06f) },
                            new Container { Position = new Vector2(7), Size = new Vector2(32), Masking = true, CornerRadius = 3,
                                Child = new DelayedLoadWrapper(new DrawableAvatar(identityFor(player))) { RelativeSizeAxes = Axes.Both } },
                            new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 48, Right = 7, Top = 6 }, Child = label },
                        },
                    };
                    rosterRows.Add(player.Id, row);
                    members.Add(row);
                }
            }
            foreach (var player in team.Members)
            {
                rosterRows[player.Id].Action = () => openProfile(player);
                readyLabels[player.Id].Text = $"{player.OfficialUsername ?? player.Username}{(player.Id == localId ? " (вы)" : "")}" + (player.IsBot ? " · бот" : "") + $"\n{(player.Ready ? "✓ Готов" : "○ Не готов")}";
                readyLabels[player.Id].Colour = player.Ready ? SomsAiOceanTheme.Aqua : SomsAiOceanTheme.Cream;
            }
            readiness.Text = $"ГОТОВЫ {team.Members.Count(p => p.Ready)}/{team.Members.Count}";
        }

        protected override void Update()
        {
            bool compact = DrawWidth < 260;
            avatar.Size = new Vector2(compact ? 36 : 58);
            nameArea.Padding = new MarginPadding { Left = side == 0 ? (compact ? 58 : 86) : 14, Right = side == 0 ? 14 : (compact ? 58 : 86), Top = 17 };
            name.Font = OsuFont.GetFont(size: compact ? 17 : 23, weight: FontWeight.Bold);
            float available = Math.Max(1, DrawWidth - nameArea.Padding.TotalHorizontal);
            name.MaxWidth = available;
            points.Scale = new Vector2(Math.Min(1, available / Math.Max(1, points.DrawWidth)));
            base.Update();
        }
    }

    private void updateMapBoard(SomsAiMatch match, int localId)
    {
        var slots = match.PoolSelected && match.Stage != "waiting" ? match.Slots : new List<SomsAiSlot>();
        string identity = string.Join('|', slots.Select(s => $"{s.Id}:{s.BeatmapId}:{s.BeatmapSetId}:{s.Checksum}"));
        if (mapIdentity != identity)
        {
            mapBoard.LayoutDuration = 450;
            mapBoard.LayoutEasing = Easing.OutQuint;
            stopPreviews();
            mapBoard.Clear();
            mapCards.Clear();
            mapIdentity = identity;
            foreach (var category in new[] { "NM", "HD", "HR", "DT", "FM", "TB" })
            {
                var group = slots.Where(s => s.Category == category && s.Status is not ("banned" or "played")).OrderBy(s => int.TryParse(s.Id.Substring(2), out int number) ? number : 0).ToArray();
                if (group.Length == 0) continue;
                var heading = arenaLabel(category + "  /  " + categoryName(category) + "   ·   " + group.Length, 12, MapCard.categoryColour(category));
                mapBoard.Add(heading);
                var grid = new MapGrid { Heading = heading };
                mapBoard.Add(grid);
                foreach (var slot in group)
                {
                    var card = new MapCard(slot, () => openMap(slot));
                    mapCards.Add(slot.Id, card);
                    grid.Add(card);
                }
            }
        }
        string? currentSlot = SelectedSlotId(match);
        foreach (var slot in slots)
        {
            if (!mapCards.TryGetValue(slot.Id, out var card)) continue;
            bool canChoose = match.TurnUserId == localId && slot.Status == "available" && match.Stage is "banning" or "picking";
            card.SetState(slot, currentSlot == slot.Id, canChoose,
                match.Stage == "banning" ? "Забанить" : "Выбрать",
                () => { inspectedSlotId = slot.Id; renderMatch(); });
            card.SetInspected(inspectedSlotId == slot.Id);
            if (slot.Status is "banned" or "played")
            {
                mapCards.Remove(slot.Id);
                card.StopPreview();
                var grid = (MapGrid)card.Parent!;
                card.FadeOut(350, Easing.OutQuint).OnComplete(_ =>
                {
                    grid.Remove(card, true);
                    if (grid.Children.Count == 0)
                    {
                        mapBoard.Remove(grid.Heading, true);
                        mapBoard.Remove(grid, true);
                    }
                    else grid.Heading.Text = slot.Category + "  /  " + categoryName(slot.Category) + "   ·   " + grid.Children.Count;
                });
            }
        }
    }

    private static string categoryName(string category) => category switch
    {
        "HD" => "HIDDEN", "HR" => "HARD ROCK", "DT" => "DOUBLE TIME", "FM" => "FREE MOD", "TB" => "TIEBREAKER", _ => "NO MOD",
    };

    private void stopPreviews()
    {
        previews?.StopAnyPlaying(this);
        foreach (var card in mapCards.Values) card.StopPreview();
    }

    private static Color4 teamColour(int team) => team == 0 ? SomsAiOceanTheme.Coral : SomsAiOceanTheme.Aqua;

    private sealed partial class MapGrid : FillFlowContainer<MapCard>
    {
        public OsuSpriteText Heading = null!;
        public MapGrid()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Full;
            Spacing = new Vector2(8);
            LayoutDuration = 450;
            LayoutEasing = Easing.OutQuint;
        }
        protected override void Update()
        {
            int columns = Math.Clamp((int)((DrawWidth + Spacing.X) / 254), 1, 3);
            float width = Math.Max(0, (DrawWidth - (columns - 1) * Spacing.X) / columns);
            foreach (var card in Children) card.Width = width;
            base.Update();
        }
    }

    private sealed partial class MapCard : OsuClickableContainer
    {
        private readonly APIBeatmapSet? beatmapSet;
        private readonly PreviewButton preview;
        private readonly Box shade;
        private readonly OsuSpriteText status;
        private readonly Box rim;
        private readonly Box banShade;
        private readonly Box flash;
        private readonly Box focus;
        private readonly OsuSpriteText statsLine;
        private readonly OsuSpriteText timingLine;
        private bool stateInitialised;
        public bool CanChoose { get; private set; }
        public string SlotStatus { get; private set; } = "available";

        public MapCard(SomsAiSlot slot, Action open)
        {
            Height = 78;
            Masking = true;
            CornerRadius = 4;
            BorderThickness = 1;
            Add(new Box { RelativeSizeAxes = Axes.Both, Colour = arenaInk });
            if (slot.BeatmapSetId > 0)
            {
                beatmapSet = new APIBeatmapSet
                {
                    OnlineID = slot.BeatmapSetId,
                    Covers = new BeatmapSetOnlineCovers { Card = $"https://assets.ppy.sh/beatmaps/{slot.BeatmapSetId}/covers/card@2x.jpg" },
                };
                Add(CreateCover(slot.BeatmapSetId));
            }
            Add(shade = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourInfo.GradientHorizontal(new Color4(9, 15, 24, 240), new Color4(9, 15, 24, 145)),
            });
            Add(rim = new Box { RelativeSizeAxes = Axes.X, Height = 2, Colour = categoryColour(slot.Category) });
            var category = Text(string.IsNullOrEmpty(slot.Label) ? slot.Id : slot.Label, 15);
            category.Colour = categoryColour(slot.Category);
            category.Position = new Vector2(10, 9);
            Add(category);
            Add(preview = new PreviewButton
            {
                Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Position = new Vector2(-5, 5), Size = new Vector2(24),
                BeatmapSet = beatmapSet!, Alpha = beatmapSet == null ? 0 : 1,
            });
            var info = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Position = new Vector2(0, 31),
                Padding = new MarginPadding { Horizontal = 10 }, Direction = FillDirection.Vertical, Spacing = new Vector2(0, 3),
            };
            Add(new Container { RelativeSizeAxes = Axes.X, Height = 21, Y = 8, Padding = new MarginPadding { Left = 52, Right = 34 },
                Child = new OsuClickableContainer { RelativeSizeAxes = Axes.Both, Action = open, Child = line(slot.Title, 16) } });
            info.Add(statsLine = line("Расчёт характеристик…", 13));
            info.Add(timingLine = line("", 12));
            Add(info);
            // Dim the entire card, including text and preview, while keeping the team
            // border and ban label legible above the overlay.
            Add(banShade = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = 0 });
            Add(new Container { RelativeSizeAxes = Axes.X, Y = 63, Height = 14, Padding = new MarginPadding { Horizontal = 10 }, Child = status = line("", 11) });
            Add(focus = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.White, Alpha = 0 });
            Add(flash = new Box { RelativeSizeAxes = Axes.Both, Alpha = 0 });
            Add(new SomsBeatmapDownloadProgress(slot.BeatmapSetId));
        }

        public void SetState(SomsAiSlot slot, bool current, bool canChoose, string chooseCaption, Action choose)
        {
            bool pickedNow = stateInitialised && SlotStatus != "picked" && slot.Status == "picked";
            SlotStatus = slot.Status;
            stateInitialised = true;
            if (slot.DisplayStats is { } stats)
            {
                statsLine.Text = $"{stats.Stars:0.00}★  ·  {stats.Bpm:0.#} BPM  ·  {TimeSpan.FromSeconds(stats.Length):m\\:ss}";
                timingLine.Text = $"CS {stats.Cs:0.#}   AR {stats.Ar:0.#}   OD {stats.Od:0.#}   HP {stats.Hp:0.#}";
            }
            CanChoose = canChoose;
            Action = choose;
            preview.BeatmapSet = beatmapSet!;
            string team = slot.SelectedByTeam is { } teamId ? $" · {(teamId == 0 ? "A" : "B")}" : "";
            status.Text = slot.Status switch
            {
                "banned" => "БАН" + team,
                "picked" => (current ? "ТЕКУЩАЯ КАРТА" : "СЫГРАНО") + team,
                "tiebreaker" => "TIEBREAKER",
                _ => canChoose ? (chooseCaption == "Забанить" ? "Выбрать для бана" : "Выбрать для пика") : "Доступна",
            };
            status.Colour = slot.SelectedByTeam is { } selectedTeam ? teamColour(selectedTeam) : current ? SomsAiOceanTheme.Gold : arenaMuted;
            BorderColour = slot.SelectedByTeam is { } id ? teamColour(id) : current ? SomsAiOceanTheme.Gold : new Color4(52, 66, 84, 255);
            BorderThickness = slot.SelectedByTeam.HasValue ? 3 : 1;
            rim.Colour = BorderColour;
            shade.Alpha = 1;
            banShade.FadeTo(slot.Status == "banned" ? .72f : 0, 200);
            if (pickedNow)
            {
                flash.Colour = BorderColour;
                flash.FadeTo(.4f, 140).Then().FadeOut(180).Then().FadeTo(.35f, 180).Then().FadeOut(220).Then().FadeTo(.3f, 180).Then().FadeOut(500);
                rim.ScaleTo(new Vector2(.05f, 1)).Then().ScaleTo(Vector2.One, 500, Easing.OutQuint);
            }
        }

        // Clearing the set also invalidates an asynchronously loading preview before it can start.
        public void StopPreview() => preview.BeatmapSet = null!;

        public void SetInspected(bool selected) => focus.FadeTo(selected ? .1f : 0, 160);

        public static Drawable CreateCover(int setId) => new DelayedLoadWrapper(new OnlineBeatmapSetCover(new APIBeatmapSet
        {
            OnlineID = setId,
            Covers = new BeatmapSetOnlineCovers { Card = $"https://assets.ppy.sh/beatmaps/{setId}/covers/card@2x.jpg" },
        }, BeatmapSetCoverType.Card) { RelativeSizeAxes = Axes.Both, FillMode = FillMode.Fill }) { RelativeSizeAxes = Axes.Both };

        private static OsuSpriteText line(string value, float size) => new TruncatingSpriteText
        {
            Text = value, Font = OsuFont.GetFont(size: size), RelativeSizeAxes = Axes.X,
        };

        public static Color4 categoryColour(string category) => category switch
        {
            "HD" => new Color4(255, 219, 122, 255),
            "HR" => new Color4(255, 144, 155, 255),
            "DT" => new Color4(192, 159, 255, 255),
            "FM" => new Color4(126, 226, 188, 255),
            "TB" => new Color4(255, 176, 113, 255),
            _ => new Color4(125, 205, 255, 255),
        };
    }
}
