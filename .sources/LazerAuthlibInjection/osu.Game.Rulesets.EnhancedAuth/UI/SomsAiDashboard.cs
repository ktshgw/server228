#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Screens;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Users.Drawables;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public partial class SomsAiScreen
{
    private static readonly Color4 accent = SomsAiOceanTheme.Aqua;
    private readonly FillFlowContainer inviteForm = Flow();
    private readonly FillFlowContainer customForm = Flow();
    private readonly FormDropdown<string> customFormat = new()
    {
        Caption = "Размер команд", Items = new[] { "1v1", "2v2", "3v3", "4v4" }, Current = { Value = "3v3" },
    };
    private static readonly string[] customRankBands = { "ARCHSOM", "DIAMOND", "PLATINUM", "GOLD", "SILVER", "BRONZE" };
    private static readonly string[] customRankDivisions = { "I", "II", "III", "IV", "V" };
    private readonly FormDropdown<string> customRankBand = new()
    {
        Caption = "Ранг пула", Items = customRankBands, Current = { Value = "GOLD" },
    };
    private readonly FormDropdown<string> customRankDivision = new()
    {
        Caption = "Ступень", Items = customRankDivisions, Current = { Value = "III" },
    };
    private readonly FormCheckBox customWithBots = new() { Caption = "С ботами", HintText = "Синяя команда будет полностью заполнена ботами. Ваши друзья могут вступить в красную команду." };
    private readonly FormDropdown<string> customBotLevel = new()
    {
        Caption = "Сложность ботов", Items = SomsAiBotSimulation.Labels, Current = { Value = SomsAiBotSimulation.Labels[1] },
    };
    private DashboardCard matchCard = null!;
    private SomsAiOceanButton? createRoomButton;
    private osu.Game.Graphics.UserInterface.LoadingSpinner? createRoomSpinner;
    private bool creatingRoom;
    private string selectedFormat = "1v1";
    private SomsAiCustomsOverlay? customsOverlay;

    private void buildDashboard()
    {
        buildRedesignedDashboard();
    }

    private void buildRedesignedDashboard()
    {
        var party = Flow();
        party.Add(partyPanel);
        party.Add(partyChat = new SomsAiPartyChat { RelativeSizeAxes = Axes.X, Height = 142 });
        inviteForm.Add(inviteTarget);
        inviteForm.Add(PrimaryButton("Отправить приглашение", invitePlayer));
        inviteForm.Add(Button("Отмена", () => inviteForm.Hide()));
        inviteForm.Hide();
        party.Add(inviteForm);

        if (partyOnly)
        {
            party.Insert(0, Heading("Ваша команда"));
            Body.Add(new DashboardCard(party));
            return;
        }

        var searchTop = Flow();
        if (Ruleset.Value.OnlineID == 3)
            searchTop.Add(variant);
        searchTop.Add(ratingPanel);
        queuePanel.Anchor = queuePanel.Origin = Anchor.BottomLeft;
        var search = new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 404,
            Children = new Drawable[] { searchTop, queuePanel },
        };

        customForm.Add(customName);
        customForm.Add(new DashboardColumns(customFormat, customRankBand));
        customForm.Add(customRankDivision);
        customRankBand.Current.BindValueChanged(_ => updateCustomRankDivision(), true);
        customForm.Add(customWithBots);
        customForm.Add(customBotLevel);
        customWithBots.Current.BindValueChanged(e => { if (e.NewValue) customBotLevel.Show(); else customBotLevel.Hide(); }, true);
        customForm.Add(Paragraph("Пул собирается автоматически по MMR SOMSAI. После заполнения слотов матч сразу переходит к банам и пикам."));
        customForm.Add(new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 50,
            Children = new Drawable[]
            {
                createRoomButton = PrimaryButton("Создать комнату", () => createCustom(customFormat.Current.Value)),
                createRoomSpinner = new osu.Game.Graphics.UserInterface.LoadingSpinner
                {
                    Size = new Vector2(26), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 18, Colour = SomsAiOceanTheme.Ink,
                },
            },
        });
        createRoomSpinner.OnLoadComplete += _ => { if (creatingRoom) createRoomSpinner.Show(); else createRoomSpinner.Hide(); };
        customForm.Add(Button("Отмена", () => customsOverlay?.HideCreate()));
        customsOverlay = new SomsAiCustomsOverlay(customPanel, customForm, () =>
        {
            setCustomRank(state.Ratings.GetValueOrDefault(selectedFormat)?.Rating ?? 1500);
            customsOverlay?.ShowCreate();
        });
        oceanContent.Add(customsOverlay);
        void showCustoms()
        {
            customsOverlay.ShowPanel();
        }

        Body.Add(new SomsAiLobbyBoard(recentPanel, party, search,
            () => this.Push(new SomsAiRecordsScreen(Ruleset.Value.OnlineID, variantId, selectedFormat)), showCustoms));
        matchCard = new DashboardCard(matchPanel) { Alpha = 0 };
        Body.Add(matchCard);
    }

    private void buildLegacyDashboard()
    {
        var party = Flow();
        party.Add(Heading("Ваша пати"));
        party.Add(Paragraph("Пригласите напарника для SOMSAI 2v2."));
        party.Add(partyPanel);
        inviteForm.Add(inviteTarget);
        inviteForm.Add(PrimaryButton("Отправить приглашение", invitePlayer));
        inviteForm.Add(Button("Отмена", () => inviteForm.Hide()));
        inviteForm.Hide();
        party.Add(inviteForm);

        if (partyOnly) Body.Add(new DashboardCard(party));
        else
        {
            var search = Flow();
            search.Add(Heading("Рейтинговый матч"));
            if (Ruleset.Value.OnlineID == 3) search.Add(variant);
            search.Add(ratingPanel);
            search.Add(queuePanel);
            Body.Add(new DashboardColumns(new DashboardCard(search), new DashboardCard(party)));
            matchCard = new DashboardCard(matchPanel) { Alpha = 0 };
            Body.Add(matchCard);

            var customs = Flow();
            var heading = Flow();
            heading.Add(Heading("Кастомные матчи"));
            heading.Add(Paragraph("1v1–4v4 · без изменения рейтинга"));
            customs.Add(new DashboardColumns(heading, Button("+  Создать кастом", () =>
            {
                if (customForm.Alpha > 0) customForm.Hide();
                else
                {
                    setCustomRank(state.Ratings.GetValueOrDefault(selectedFormat)?.Rating ?? 1500);
                    revealForm(customForm);
                }
            })));
            customForm.Add(customName);
            customForm.Add(new DashboardColumns(customFormat, customRankBand));
            customForm.Add(customRankDivision);
            customRankBand.Current.BindValueChanged(_ => updateCustomRankDivision(), true);
            customForm.Add(customWithBots);
            customForm.Add(customBotLevel);
            customWithBots.Current.BindValueChanged(e => { if (e.NewValue) customBotLevel.Show(); else customBotLevel.Hide(); }, true);
            customForm.Add(Paragraph("Пул собирается автоматически по MMR SOMSAI: каждый слот получает случайную карту из подходящих турниров. Затем — баны и пики."));
            customForm.Add(new Container
            {
                RelativeSizeAxes = Axes.X, Height = 50,
                Children = new Drawable[]
                {
                    createRoomButton = PrimaryButton("Создать комнату", () => createCustom(customFormat.Current.Value)),
                    createRoomSpinner = new osu.Game.Graphics.UserInterface.LoadingSpinner
                    { Size = new Vector2(26), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 18, Colour = SomsAiOceanTheme.Ink },
                },
            });
            createRoomSpinner.OnLoadComplete += _ => { if (creatingRoom) createRoomSpinner.Show(); };
            customForm.Add(Button("Отмена", () => customForm.Hide()));
            customForm.Hide();
            customs.Add(customForm);
            customs.Add(customPanel);
            Body.Add(new DashboardCard(customs));
        }
    }

    private void setCreatingRoom(bool value)
    {
        creatingRoom = value;
        if (createRoomButton == null) return;
        createRoomButton.Enabled.Value = !value;
        createRoomButton.Text = value ? "Создаём комнату…" : "Создать комнату";
        if (value) createRoomSpinner?.Show(); else createRoomSpinner?.Hide();
        customName.ReadOnly = value;
        customRankBand.Current.Disabled = value;
        customRankDivision.Current.Disabled = value || customRankBand.Current.Value == "ARCHSOM";
        customFormat.Current.Disabled = customWithBots.Current.Disabled = customBotLevel.Current.Disabled = value;
    }

    private void invitePlayer()
    {
        string username = inviteTarget.Current.Value.Trim();
        if (username.Length == 0) { StatusText.Text = "Введите точный ник игрока."; return; }
        action("party_invite", new JObject { ["target_username"] = username }, afterSuccess: () =>
        {
            inviteTarget.Current.Value = "";
            inviteForm.Hide();
        });
    }

    private void renderParty()
    {
        partyPanel.Clear();
        int localId = Api.LocalUser.Value.OnlineID;
        var members = state.Party?.Members.Count > 0 ? state.Party.Members : new()
        {
            new SomsPlayer { Id = localId, Username = Api.LocalUser.Value.Username, AvatarUrl = Api.LocalUser.Value.AvatarUrl },
        };
        foreach (var member in members)
        {
            var caption = Flow();
            caption.Spacing = new Vector2(0, 2);
            caption.Padding = new MarginPadding { Left = 60 };
            caption.Add(Paragraph(member.Username, 18));
            if (members.Count > 1 && member.Id == state.Party?.CaptainId) caption.Add(Paragraph("Капитан", 13));
            double mmr = member.Id == localId ? state.Ratings.GetValueOrDefault(selectedFormat)?.Rating ?? member.Rating : member.Ratings.GetValueOrDefault(selectedFormat)?.Rating ?? member.Rating;
            var rank = SomsAiRank.FromRating(mmr);
            var ratingText = Text($"{mmr:N0} MMR", 16);
            ratingText.Font = OsuFont.GetFont(size: 16, weight: FontWeight.SemiBold);
            ratingText.Colour = rank.Colour;
            caption.Add(ratingText);
            var rankText = Text(rank.Name, 14);
            rankText.Colour = rank.Colour;
            caption.Add(rankText);
            var row = new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Child = caption };
            // Avatar fetching stays off the screen's synchronous load path.
            row.Add(new Container
            {
                Size = new Vector2(48), CornerRadius = 12, Masking = true,
                Child = new DelayedLoadWrapper(new DrawableAvatar(new APIUser
                {
                    Id = member.Id, Username = member.Username,
                    AvatarUrl = new Uri(new Uri(Api.Endpoints.APIUrl), member.AvatarUrl ?? $"/users/{member.Id}/avatar").AbsoluteUri,
                })) { RelativeSizeAxes = Axes.Both },
            });
            partyPanel.Add(row);
        }
        bool captain = state.Party?.Id == null || state.Party.CaptainId == localId;
        if (members.Count < 2 && captain && state.Party?.Busy != true)
            partyPanel.Add(new Container { RelativeSizeAxes = Axes.X, Height = 52, Children = new Drawable[]
            {
                new SomsAiOceanButton { RelativeSizeAxes = Axes.None, Size = new Vector2(52), Text = "+", Action = openPartyFriends },
                new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding { Left = 64, Top = 15 }, Child = Paragraph("Пригласить друга", 16) },
            } });
        else inviteForm.Hide();
        partyChat?.SetParty(state.Party?.Id);
        if (state.Party?.Id != null)
        {
            if (state.Party.Busy) partyPanel.Add(Paragraph("Пати участвует в поиске или матче."));
            else partyPanel.Add(Button("Выйти из пати", () => action("party_leave")));
        }
        foreach (var outgoing in state.Party?.OutgoingInvites ?? Enumerable.Empty<SomsPartyInvite>())
            partyPanel.Add(Paragraph("Ожидаем ответа: " + outgoing.Target?.Username));
        foreach (var invite in (state.Party?.Invites ?? Enumerable.Empty<SomsPartyInvite>()).Concat(state.Invites).DistinctBy(i => i.Id))
        {
            partyPanel.Add(Paragraph("Приглашает " + (invite.Captain?.Username ?? "игрок")));
            partyPanel.Add(new DashboardColumns(
                Button("Принять", () => action("party_accept", new JObject { ["invitation_id"] = invite.Id })),
                Button("Отклонить", () => action("party_decline", new JObject { ["invitation_id"] = invite.Id }))));
        }
    }

    private void renderSearch()
    {
        ratingPanel.Clear();
        queuePanel.Clear();
        bool active = state.Match is { IsFinished: false };
        bool captain = state.Party?.Id == null || state.Party.CaptainId == Api.LocalUser.Value.OnlineID;
        if (state.Queue != null) selectedFormat = state.Queue.Format;
        var choices = new Drawable[2];
        int index = 0;
        foreach (string format in new[] { "1v1", "2v2" })
        {
            bool selected = selectedFormat == format;
            var rating = state.Ratings.GetValueOrDefault(format);
            var content = Flow();
            var select = new SomsAiOceanButton(selected) { Text = (selected ? "●  " : "") + format, Height = 52, Action = () =>
            {
                if (state.Queue != null || active) return;
                selectedFormat = format;
                renderSearch();
            } };
            content.Add(select);
            var value = Text(rating == null ? "— MMR" : $"{rating.Rating:N0} MMR", 30);
            value.Font = OsuFont.GetFont(size: 30, weight: FontWeight.SemiBold);
            value.Colour = selected ? SomsAiOceanTheme.Gold : SomsAiOceanTheme.Cream;
            content.Add(value);
            content.Add(Paragraph($"Rank #{rating?.Rank?.ToString() ?? "—"} · {rating?.Wins ?? 0}W / {rating?.Losses ?? 0}L", 18));
            choices[index++] = content;
        }
        ratingPanel.Add(new DashboardColumns(choices, 220));
        ratingPanel.Add(new SomsAiRankPlaque(state.Ratings.GetValueOrDefault(selectedFormat)?.Rating ?? 0)
        {
            Margin = new MarginPadding { Top = 64 },
        });
        if (state.Queue != null)
        {
            queuePanel.Add(new OceanSearchIndicator());
            if (captain) queuePanel.Add(Button("Отменить поиск", () => action("queue_leave")));
        }
        else if (active) queuePanel.Add(Paragraph("Матч найден. Перейдите в него, чтобы продолжить."));
        else if (!captain) queuePanel.Add(Paragraph("Поиск запускает капитан пати."));
        else if (selectedFormat == "1v1" && state.Party?.Members.Count > 1)
            queuePanel.Add(Paragraph("Для поиска с напарником выберите 2v2. Для 1v1 выйдите из пати."));
        else queuePanel.Add(PrimaryButton("Начать поиск " + selectedFormat, () => action("queue_join", new JObject { ["format"] = selectedFormat })));
    }

    private void renderCustoms()
    {
        customPanel.Clear();
        if (state.Customs.Count == 0) customPanel.Add(Paragraph("Открытых комнат пока нет. Создайте свою и пригласите друзей.", 16));
        foreach (var custom in state.Customs)
        {
            customPanel.Add(new CustomRoomRow(custom, team =>
                action("custom_join", new JObject { ["match_id"] = custom.Id, ["team"] = team }),
                state.Match is not { IsFinished: false } && state.Queue == null));
        }
    }

    private void renderRecentMatches()
    {
        recentPanel.Clear();
        if (state.RecentMatches.Count == 0)
        {
            recentPanel.Add(Paragraph("Здесь появятся последние сыгранные матчи.", 16));
            return;
        }

        foreach (var match in state.RecentMatches.Take(20))
            recentPanel.Add(new SomsAiRecentMatchRow(match, () => this.Push(new SomsAiRecordsScreen(Ruleset.Value.OnlineID, variantId, selectedFormat, match.Id))));
    }

    private void renderPoolVote(SomsAiMatch match, int localId)
    {
        var team = match.Teams.FirstOrDefault(t => t.CaptainId == localId);
        matchPanel.Add(Paragraph($"Пулы для {match.TargetMmr:0} MMR. У каждой команды один голос. Если выборы разные, турнир определится случайно между ними."));
        if (team == null) matchPanel.Add(Paragraph("Турнир выбирают капитаны команд."));
        else matchPanel.Add(Paragraph(match.PoolVotes.ContainsKey(team.Id) ? "Ваш голос принят. Ждём другую команду; выбор можно изменить." : "Выберите турнир для своей команды."));
        var candidates = new List<Drawable>();
        foreach (var candidate in match.PoolCandidates)
        {
            bool voted = team != null && match.PoolVotes.GetValueOrDefault(team.Id) == candidate.Id;
            var details = Flow();
            details.Add(Paragraph(candidate.Name, 20));
            details.Add(Paragraph($"{candidate.AverageStars:0.00}★ · BO{candidate.BestOf} · {candidate.MapCount} карт", 16));
            Drawable choice = team == null ? Paragraph(match.PoolVotes.Values.Contains(candidate.Id) ? "Есть голос команды" : "Ожидание выбора")
                : new ArenaButton(true) { Text = voted ? "✓  Ваш выбор" : "Выбрать турнир", Action = () => action("pool_vote", new JObject { ["pool_id"] = candidate.Id }) };
            details.Add(choice);
            candidates.Add(new ArenaPanel(details));
        }
        for (int index = 0; index < candidates.Count; index += 2)
            matchPanel.Add(new DashboardColumns(candidates.Skip(index).Take(2).ToArray(), 650));
    }

    private static OsuTextFlowContainer Paragraph(string text, float size = 17)
    {
        var flow = new OsuTextFlowContainer(t => t.Font = OsuFont.GetFont(size: size)) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Colour = SomsAiOceanTheme.Muted };
        flow.AddText(text);
        return flow;
    }

    private sealed partial class SomsAiCustomsOverlay : Container
    {
        private const double transition_duration = 280;
        private readonly osu.Game.Graphics.Containers.OsuClickableContainer dim;
        private readonly Container panel;
        private readonly Container lobbyPage;
        private readonly Container createPanel;

        public SomsAiCustomsOverlay(Drawable rooms, Drawable form, Action showCreate)
        {
            Name = "somsai-customs-overlay";
            RelativeSizeAxes = Axes.Both;
            Depth = -100;

            dim = new osu.Game.Graphics.Containers.OsuClickableContainer
            {
                RelativeSizeAxes = Axes.Both,
                Action = HidePanel,
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = .72f },
            };

            lobbyPage = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    new SomsAiOceanButton
                    {
                        Position = new Vector2(24, 62), RelativeSizeAxes = Axes.None, Size = new Vector2(190, 50),
                        Text = "+  Создать комнату", Action = showCreate,
                    },
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Left = 24, Right = 24, Top = 126, Bottom = 20 },
                        Child = new OsuScrollContainer { RelativeSizeAxes = Axes.Both, ScrollbarVisible = false, Child = rooms },
                    },
                },
            };

            createPanel = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Left = 24, Right = 24, Top = 70, Bottom = 20 },
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = 12,
                    BorderThickness = 2,
                    BorderColour = new Color4(165, 104, 188, 255),
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(58, 48, 61, 255) },
                        new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding { Horizontal = 26, Top = 20, Bottom = 18 },
                            Child = new OsuScrollContainer { RelativeSizeAxes = Axes.Both, ScrollbarVisible = false, Child = form },
                        },
                    },
                },
            };

            panel = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Masking = true,
                CornerRadius = 14,
                BorderThickness = 2,
                BorderColour = new Color4(116, 104, 133, 255),
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(29, 25, 34, 255) },
                    new OsuSpriteText
                    {
                        Position = new Vector2(24, 20), Text = "Multiplayer · SOMSAI Lounge",
                        Font = OsuFont.GetFont(size: 26, weight: FontWeight.SemiBold), Colour = Color4.White,
                    },
                    new SomsAiOceanButton
                    {
                        Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Position = new Vector2(-24, 18),
                        RelativeSizeAxes = Axes.None, Size = new Vector2(120, 44), Text = "Закрыть", Action = HidePanel,
                    },
                    lobbyPage,
                    createPanel,
                },
            };

            createPanel.Hide();
            InternalChildren = new Drawable[] { dim, panel };
            Hide();
        }

        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos) => IsPresent;

        protected override void Update()
        {
            base.Update();
            panel.Size = new Vector2(Math.Max(320, Math.Min(1180, DrawWidth - 70)), Math.Max(420, Math.Min(650, DrawHeight - 55)));
        }

        public void ShowPanel()
        {
            createPanel.Hide();
            lobbyPage.Show();
            Show();
            Alpha = 1;

            dim.ClearTransforms();
            dim.FadeInFromZero(180, Easing.OutQuint);

            panel.ClearTransforms();
            panel.X = 150;
            panel.Alpha = 0;
            panel.MoveToX(0, transition_duration, Easing.OutQuint);
            panel.FadeIn(transition_duration, Easing.OutQuint);
        }

        public void HidePanel()
        {
            createPanel.Hide();
            lobbyPage.Hide();

            dim.ClearTransforms();
            dim.FadeOut(180, Easing.InQuint);

            panel.ClearTransforms();
            panel.MoveToX(150, transition_duration, Easing.InQuint);
            panel.FadeOut(transition_duration, Easing.InQuint).OnComplete(_ =>
            {
                lobbyPage.Show();
                Hide();
            });
        }

        public void ShowCreate()
        {
            lobbyPage.Hide();
            createPanel.Show();
            createPanel.ClearTransforms();
            createPanel.X = 60;
            createPanel.Alpha = 0;
            createPanel.MoveToX(0, transition_duration, Easing.OutQuint);
            createPanel.FadeIn(transition_duration, Easing.OutQuint);
        }

        public void HideCreate()
        {
            if (!createPanel.IsPresent) return;

            createPanel.Hide();
            lobbyPage.Show();
            lobbyPage.ClearTransforms();
            lobbyPage.X = -40;
            lobbyPage.Alpha = 0;
            lobbyPage.MoveToX(0, transition_duration, Easing.OutQuint);
            lobbyPage.FadeIn(transition_duration, Easing.OutQuint);
        }
    }
    private sealed partial class CustomRoomRow : Container
    {
        public CustomRoomRow(SomsAiCustom room, Action<int> join, bool canJoin)
        {
            RelativeSizeAxes = Axes.X;
            Height = 94;
            Margin = new MarginPadding { Bottom = 8 };
            Masking = true;
            CornerRadius = 8;
            BorderThickness = 1;
            BorderColour = new Color4(101, 82, 112, 255);
            var actions = new FillFlowContainer
            {
                Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight,
                AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new Vector2(8),
                Margin = new MarginPadding { Right = 14 },
            };
            for (int team = 0; team < 2; team++)
            {
                int selectedTeam = team;
                int occupied = room.Teams.ElementAtOrDefault(team);
                if (canJoin && occupied < room.Capacity / 2)
                    actions.Add(new SomsAiOceanButton(team == 0)
                    {
                        RelativeSizeAxes = Axes.None, Size = new Vector2(155, 44),
                        Text = $"{(team == 0 ? "Красные" : "Синие")} {occupied}/{room.Capacity / 2}",
                        Action = () => join(selectedTeam),
                    });
            }
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(48, 40, 51, 255) },
                new Box { RelativeSizeAxes = Axes.Y, Width = 6, Colour = new Color4(203, 70, 158, 255) },
                new TruncatingSpriteText
                {
                    Position = new Vector2(20, 15), Width = 420, Text = room.Name,
                    Font = OsuFont.GetFont(size: 21, weight: FontWeight.SemiBold), Colour = Color4.White,
                },
                new OsuSpriteText
                {
                    Position = new Vector2(20, 51), Text = $"{room.Format}  ·  {room.Participants}/{room.Capacity} игроков  ·  {room.TargetMmr:0} MMR",
                    Font = OsuFont.GetFont(size: 16), Colour = new Color4(209, 193, 215, 255),
                },
                actions,
            };
        }
    }

    private sealed partial class DashboardCard : Container
    {
        public DashboardCard(Drawable content)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Masking = true;
            CornerRadius = 16;
            BorderThickness = 2;
            BorderColour = SomsAiOceanTheme.Cream;
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientVertical(new Color4(4, 65, 80, 253), new Color4(4, 40, 57, 253)),
                },
                new Circle
                {
                    Anchor = Anchor.BottomRight, Origin = Anchor.Centre, Size = new Vector2(180),
                    BypassAutoSizeAxes = Axes.Both,
                    Colour = new Color4(57, 183, 186, 9), BorderColour = new Color4(87, 227, 220, 28), BorderThickness = 2,
                },
                new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding(20), Child = content },
            };
        }
    }

    private sealed partial class DashboardColumns : FillFlowContainer
    {
        private readonly float breakpoint;
        public DashboardColumns(Drawable left, Drawable right) : this(new[] { left, right }, 740) { }
        public DashboardColumns(Drawable[] children, float breakpoint)
        {
            this.breakpoint = breakpoint;
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Full;
            Spacing = new Vector2(16);
            foreach (var child in children)
            {
                child.RelativeSizeAxes &= ~Axes.X;
                Add(child);
            }
        }
        protected override void Update()
        {
            float width = DrawWidth < breakpoint ? DrawWidth : (DrawWidth - Spacing.X) / 2;
            foreach (var child in Children) child.Width = Math.Max(0, width);
            base.Update();
        }
    }

    private sealed partial class OceanSearchIndicator : Container
    {
        private readonly Circle[] dots = new Circle[3];
        public OceanSearchIndicator()
        {
            Name = "somsai-search-indicator";
            RelativeSizeAxes = Axes.X; Height = 36;
            var caption = Paragraph("Ищем соперников…", 19);
            caption.Padding = new MarginPadding { Left = 65, Top = 4 };
            Add(caption);
            for (int i = 0; i < dots.Length; i++)
                Add(dots[i] = new Circle { Size = new Vector2(9), X = 8 + i * 16, Colour = SomsAiOceanTheme.Gold });
        }
        protected override void Update()
        {
            base.Update();
            for (int i = 0; i < dots.Length; i++)
                dots[i].Y = 13 + (float)Math.Sin(Time.Current / 350 - i) * 4;
        }
    }
}
