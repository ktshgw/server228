#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Beatmaps;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Skinning;
using osu.Game.Users;
using osu.Game.Users.Drawables;
using osuTK;
using osuTK.Graphics;
using Realms;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public sealed partial class SomsMarathonScreen : OsuScreen
{
    public override string Title => "Марафон";
    public override bool ShowFooter => true;
    public override bool HideOverlaysOnEnter => true;
    // Like the native playlist lounge, editing does not lease global selection.
    // Player takes its own lease; the nested native song picker needs writable mods.
    public override bool DisallowExternalBeatmapRulesetChanges => false;
    [Cached] private readonly OverlayColourProvider colours = new(OverlayColourScheme.Purple);
    [Resolved] private IAPIProvider api { get; set; } = null!;
    [Resolved] private RealmAccess realm { get; set; } = null!;
    [Resolved] private BeatmapManager beatmaps { get; set; } = null!;
    [Resolved] private BeatmapModelDownloader downloads { get; set; } = null!;
    [Resolved] private BeatmapLookupCache lookup { get; set; } = null!;
    [Resolved] private AudioManager audio { get; set; } = null!;
    [Resolved] private SkinManager skins { get; set; } = null!;
    [Resolved] private MusicController music { get; set; } = null!;
    private readonly FillFlowContainer library = flow();
    private readonly FillFlowContainer fragments = flow();
    private readonly Dictionary<SomsMarathonSegment, (MarathonListItem Card, OsuSpriteText Title, OsuSpriteText Range)> fragmentCards = new();
    private readonly OsuSpriteText fragmentHint = label("Добавьте песни из своей библиотеки карт.", 20);
    private readonly OsuSpriteText fragmentSummary = label("", 16);
    private readonly FormTextBox name = new() { Caption = "Название марафона", Current = { Value = "Songs compilation" }, LengthLimit = 100 };
    private readonly FormTextBox search = new() { Caption = "Поиск марафона", PlaceholderText = "Название или автор", Current = { Value = "" } };
    private readonly OsuSpriteText status = label("Добавьте карты. Фрагменты Kiai будут выбраны автоматически.", 16);
    private SomsMarathonDefinition definition = new();
    private readonly Dictionary<string, WorkingBeatmap> resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<SomsMarathonRequest, byte> requests = new();
    private readonly CancellationTokenSource lifetime = new();
    private ScheduledDelegate? searchPending;
    private bool busy, closed;
    private int libraryPage = 1;
    private string directory = "";
    private string draftPath => Path.Combine(directory, "draft.json");
    private WorkingBeatmap original = null!;
    private double? previewEnd;
    private bool disposed;
    private bool canEdit => definition.OwnerId == 0 && definition.Id == 0
                            || definition.OwnerId == api.LocalUser.Value.Id && definition.OwnerId > 0;

    [BackgroundDependencyLoader]
    private void load()
    {
        directory = Path.Combine(SomsClientPreferences.Instance.DirectoryPath, "soms-marathons");
        Directory.CreateDirectory(directory);
        original = Beatmap.Value;
        definition.RulesetId = Ruleset.Value.OnlineID;
        if (File.Exists(draftPath))
        {
            try
            {
                var draft = JsonConvert.DeserializeObject<SomsMarathonDefinition>(File.ReadAllText(draftPath));
                if (draft?.RulesetId == definition.RulesetId && draft.Segments.Count <= 20) definition = draft;
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        name.Current.Value = definition.Name;
        name.OnCommit += (_, _) => { if (!busy && canEdit && name.Current.Value.Trim() != definition.Name) changed(); };
        search.Current.BindValueChanged(_ =>
        {
            searchPending?.Cancel();
            searchPending = Scheduler.AddDelayed(renderSavedCards, 200);
        });
        buildLayout();
        renderFragments();
    }

    protected override void LoadComplete() { base.LoadComplete(); showLounge(); }
    private static OsuSpriteText label(string text, float size = 18) => new TruncatingSpriteText { Text = text, Font = OsuFont.GetFont(size: size), RelativeSizeAxes = Axes.X };
    private static FillFlowContainer flow() => new() { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new Vector2(0, 9) };
    private ShearedButton button(string text, Action action)
    {
        var result = new ShearedButton { Text = text, RelativeSizeAxes = Axes.X, Height = 40, Action = () => { if (!busy && !closed) action(); } };
        if (text is "Играть" or "Применить")
        {
            result.DarkerColour = new Color4(90, 140, 32, 255); result.LighterColour = new Color4(170, 235, 92, 255);
            result.TextColour = new Color4(22, 34, 15, 255);
        }
        else if (text is "Сохранить" or "Создать марафон" || text.StartsWith("+"))
        { result.DarkerColour = new Color4(81, 48, 125, 255); result.LighterColour = new Color4(121, 75, 175, 255); }
        return result;
    }
    private static Container row(params Drawable[] items)
    {
        var container = new Container { RelativeSizeAxes = Axes.X, Height = 40 };
        for (int i = 0; i < items.Length; i++)
        {
            var cell = new Container { RelativeSizeAxes = Axes.X, RelativePositionAxes = Axes.X, Width = 1f / items.Length,
                X = i / (float)items.Length, Height = 40, Padding = new MarginPadding { Right = 6 }, Child = items[i] };
            container.Add(cell);
        }
        return container;
    }

    private void changed()
    {
        if (!canEdit) return;
        definition.Id = 0;
        definition.OwnerId = api.LocalUser.Value.Id;
        definition.OwnerName = api.LocalUser.Value.Username;
        definition.Name = string.IsNullOrWhiteSpace(name.Current.Value) ? "Songs compilation" : name.Current.Value.Trim();
        File.WriteAllText(draftPath, JsonConvert.SerializeObject(definition));
    }
    private void fresh() { definition = new() { RulesetId = Ruleset.Value.OnlineID }; name.Current.Value = definition.Name; changed(); showDetail(); }
    private void run(Func<Task> action)
    {
        if (busy || closed) return;
        busy = true;
        name.ReadOnly = true;
        status.Text = "Подождите…";
        _ = execute();
        async Task execute()
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException e) { Schedule(() => { if (!closed) status.Text = e.Message; }); }
            catch (Exception e) { osu.Framework.Logging.Logger.Error(e, "Marathon action failed"); Schedule(() => { if (!closed) status.Text = e.GetBaseException().Message; }); }
            finally { Schedule(() => { busy = false; name.ReadOnly = !canEdit; }); }
        }
    }
    private async Task<JObject> request(SomsMarathonRequest request)
    {
        if (api.State.Value != APIState.Online) throw new InvalidOperationException("Войдите в SOMS, чтобы сохранить или запустить марафон.");
        var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests.TryAdd(request, 0);
        request.Success += result => completion.TrySetResult(result);
        request.Failure += error => completion.TrySetException(error);
        // Results are a child screen: the editor's scheduler is paused during gameplay.
        api.Queue(request);
        try { return await completion.Task.WaitAsync(lifetime.Token); }
        finally { requests.TryRemove(request, out _); }
    }

    private void renderFragments()
    {
        name.ReadOnly = busy || !canEdit;
        addSongsButton.Alpha = saveButton.Alpha = canEdit ? 1 : 0;
        if (fragmentSummary.Parent == null)
        {
            fragments.LayoutDuration = 500; fragments.LayoutEasing = Easing.OutQuint;
            fragments.Add(fragmentHint); fragments.Add(fragmentSummary);
        }
        foreach (var removed in fragmentCards.Keys.Except(definition.Segments).ToArray())
        {
            fragmentCards[removed].Card.Retire(); fragmentCards.Remove(removed);
        }
        for (int i = 0; i < definition.Segments.Count; i++)
        {
            var segment = definition.Segments[i];
            if (!fragmentCards.TryGetValue(segment, out var entry))
            {
                var title = label("", 18);
                var rangeLabel = label("", 14);
                var actions = new FillFlowContainer
                {
                    Alpha = canEdit ? 1 : 0,
                    Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new Vector2(4, 0),
                    Children = canEdit ? new Drawable[]
                    {
                        fragmentAction(FontAwesome.Solid.Pen, "Редактировать отрезок", () => editSegment(segment)),
                        fragmentAction(FontAwesome.Solid.ChevronUp, "Переместить выше", () => move(definition.Segments.IndexOf(segment), -1)),
                        fragmentAction(FontAwesome.Solid.ChevronDown, "Переместить ниже", () => move(definition.Segments.IndexOf(segment), 1)),
                        fragmentAction(FontAwesome.Solid.Minus, "Убрать песню", () => { definition.Segments.Remove(segment); changed(); renderFragments(); }),
                    } : Array.Empty<Drawable>(),
                };
                var captions = new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Right = canEdit ? 146 : 0 },
                    Children = new Drawable[] { title, new Container { RelativeSizeAxes = Axes.X, Y = 23, Height = 17, Child = rangeLabel } },
                };
                var card = new MarathonListItem(songCard(segment, 68, captions, actions), 68);
                fragments.Add(card);
                fragmentCards.Add(segment, entry = (card, title, rangeLabel));
            }
            entry.Title.Text = $"{i + 1}. {segment.Title}";
            entry.Range.Text = $"{timestamp(segment.StartMs)} — {timestamp(segment.EndMs)} · {(segment.EndMs - segment.StartMs) / 1000.0:0.##} с";
            fragments.SetLayoutPosition(entry.Card, i);
        }
        fragmentHint.Alpha = definition.Segments.Count == 0 ? 1 : 0;
        fragments.SetLayoutPosition(fragmentHint, -1);
        fragments.SetLayoutPosition(fragmentSummary, float.MaxValue);
        fragmentSummary.Text = $"{definition.Segments.Count}/20 песен · {duration(definition)} · без PP и MMR";
    }
    private IconButton fragmentAction(IconUsage icon, string tooltip, Action action) => new()
    {
        Icon = icon, TooltipText = tooltip,
        Action = () => { if (!busy && !closed && canEdit) action(); },
    };

    private void move(int index, int delta)
    {
        if (!canEdit) return;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= definition.Segments.Count) return;
        (definition.Segments[index], definition.Segments[target]) = (definition.Segments[target], definition.Segments[index]);
        changed(); renderFragments();
    }
    private WorkingBeatmap resolve(SomsMarathonSegment segment)
    {
        // Public compilations refer to an online difficulty, not the author's local revision.
        // Query each time so imports/updates replace any previously cached working beatmap.
        var info = findInstalled(segment);
        if (info != null) return beatmaps.GetWorkingBeatmap(info);
        if (segment.BeatmapId <= 0 && resolved.TryGetValue(segment.Checksum, out var working)) return working;
        throw new InvalidOperationException("Карта ещё не установлена: " + segment.Title + ". Нажмите «Скачать недостающие» и дождитесь импорта.");
    }
    private BeatmapInfo? findInstalled(SomsMarathonSegment segment) => segment.BeatmapId > 0
        ? beatmaps.QueryBeatmap(map => map.OnlineID == segment.BeatmapId)
        : beatmaps.QueryBeatmap(map => map.MD5Hash == segment.Checksum);
    private void preview(SomsMarathonSegment segment)
    {
        try { Beatmap.Value = resolve(segment); music.Play(); music.SeekTo(segment.StartMs); previewEnd = segment.EndMs; }
        catch (Exception e) { status.Text = e.GetBaseException().Message; }
    }
    private void downloadMissing()
    {
        run(async () =>
        {
            var missing = definition.Segments.Where(segment => segment.BeatmapSetId > 0 && findInstalled(segment) == null)
                .GroupBy(segment => segment.BeatmapSetId).Select(group => group.First()).ToArray();
            foreach (var segment in missing)
            {
                if (downloads.GetExistingDownload(new BeatmapSetInfo { OnlineID = segment.BeatmapSetId }) != null) continue;
                var map = await lookup.GetBeatmapAsync(segment.BeatmapId, lifetime.Token);
                if (map?.BeatmapSet == null) throw new InvalidOperationException("Не удалось найти карту: " + segment.Title);
                Schedule(() => { if (!closed && findInstalled(segment) == null) downloads.Download(map.BeatmapSet, true); });
            }
            Schedule(() => status.Text = missing.Length == 0 ? "Все карты установлены. Можно играть." : "Дождитесь загрузки и импорта карт. Прогресс показан на карточках и в загрузках (Ctrl+B).");
        });
    }

    private async Task save()
    {
        if (!canEdit) throw new InvalidOperationException("Редактировать марафон может только создатель.");
        if (definition.Segments.Count < 2) throw new InvalidOperationException("Добавьте хотя бы две карты.");
        if (canEdit && name.Current.Value.Trim() != definition.Name) changed();
        if (definition.Id > 0) { Schedule(() => status.Text = "Этот марафон уже сохранён."); return; }
        definition.Name = string.IsNullOrWhiteSpace(name.Current.Value) ? "Songs compilation" : name.Current.Value.Trim();
        JObject body = JObject.FromObject(definition);
        body.Remove("id"); body.Remove("owner_id"); body.Remove("owner_name");
        var response = await request(new SomsMarathonRequest(body: body));
        definition = response.ToObject<SomsMarathonDefinition>()!;
        File.WriteAllText(draftPath, JsonConvert.SerializeObject(definition));
        Schedule(() => status.Text = "Марафон сохранён. Он доступен в общем списке.");
    }
    private async Task play()
    {
        previewEnd = null;
        if (name.Current.Value.Trim() != definition.Name) changed();
        if (definition.Id == 0) await save();
        var maps = definition.Segments.Select(resolve).ToArray();
        var compilation = await Task.Run(() => SomsMarathonCompiler.Compile(definition, maps, directory,
            message => Schedule(() => { if (!closed) status.Text = message; }), lifetime.Token), lifetime.Token);
        var mods = Mods.Value.Select(mod => new APIMod(mod)).ToArray();
        var attempt = await request(new SomsMarathonRequest($"/{definition.Id}/start", new JObject { ["mods"] = JArray.FromObject(mods) }));
        string attemptId = attempt.Value<string>("attempt_id")!;
        int marathonId = definition.Id;
        Schedule(() =>
        {
            if (closed || !this.IsCurrentScreen()) return;
            var working = SomsMarathonWorkingBeatmap.Open(compilation, audio, skins);
            Ruleset.Value = working.BeatmapInfo.Ruleset;
            Beatmap.Value = working;
            this.Push(new PlayerLoader(() => new SomsMarathonPlayer(working, compilation.Songs, async result =>
            {
                var saved = await request(new SomsMarathonRequest($"/{marathonId}/scores", new JObject
                {
                    ["attempt_id"] = attemptId, ["total_score"] = result.Score,
                    ["accuracy"] = result.Accuracy, ["max_combo"] = result.Combo,
                }));
                string message = saved.Value<bool>("saved") ? "Результат сохранён в таблице марафона." : "Результат с автоматической игрой не добавляется в таблицу.";
                Schedule(() => status.Text = message);
                return message;
            })));

        });
    }
    private void loadSaved()
    {
        run(async () =>
        {
            var data = await request(new SomsMarathonRequest($"?ruleset_id={definition.RulesetId}&page={libraryPage}"));
            Schedule(() =>
            {
                if (closed) return;
                savedMarathons.Clear();
                savedMarathons.AddRange(data["items"]!.ToObject<List<SomsMarathonDefinition>>()!);
                hasMore = data.Value<bool>("has_more");
                renderSavedCards(); status.Text = "Выберите марафон или создайте свою подборку песен.";
            });
        });
    }
    private void showLeaderboard()
    {
        if (definition.Id == 0) { status.Text = "Сначала сохраните марафон."; return; }
        run(async () =>
        {
            var data = await request(new SomsMarathonRequest($"/{definition.Id}/scores"));
            Schedule(() =>
            {
                if (closed) return;
                records.Clear();
                int rank = 0;
                foreach (var item in data["items"]!)
                {
                    var user = new APIUser
                    {
                        Id = item.Value<int>("user_id"),
                        Username = item.Value<string>("username") ?? "unknown",
                        AvatarUrl = new Uri(new Uri(api.Endpoints.APIUrl), item.Value<string>("avatar_url") ?? $"/users/{item.Value<int>("user_id")}/avatar").AbsoluteUri,
                        CountryCode = Enum.TryParse<CountryCode>(item.Value<string>("country_code"), true, out var country) ? country : CountryCode.Unknown,
                    };
                    var entry = new MarathonScoreRow(++rank, user, item);
                    records.Add(entry);
                    entry.OnLoadComplete += _ => entry.FadeInFromZero(200);
                }
                if (rank == 0) records.Add(label("Пока нет результатов. Сыграйте первым!"));
                status.Text = "Песни, отрезки и рекорды выбранного марафона.";
            });
        });
    }

    public override void OnResuming(ScreenTransitionEvent e)
    {
        base.OnResuming(e); Beatmap.Value = original;
        this.FadeIn(250); this.ScaleTo(1, 250, Easing.OutSine);
        if (detailVisible && definition.Id > 0) Scheduler.AddDelayed(() => { if (!busy) showLeaderboard(); }, 100);
    }
    protected override void Update()
    {
        base.Update();
        if (range != null) range.PreviewTime = previewEnd.HasValue ? music.CurrentTrack.CurrentTime : null;
        if (previewEnd.HasValue && this.IsCurrentScreen() && music.CurrentTrack.CurrentTime >= previewEnd.Value)
        { music.Stop(); previewEnd = null; }
    }
    public override bool OnExiting(ScreenExitEvent e)
    {
        if (rangeModal != null) { closeRange(); return true; }
        if (base.OnExiting(e)) return true;
        closed = true; lifetime.Cancel();
        foreach (var request in requests.Keys) request.Cancel();
        Beatmap.Value = original;
        this.FadeOut(500, Easing.OutQuint);
        return false;
    }
    protected override void Dispose(bool isDisposing)
    {
        if (disposed) return;
        disposed = true;
        closed = true; lifetime.Cancel(); searchPending?.Cancel();
        foreach (var request in requests.Keys) request.Cancel();
        lifetime.Dispose(); base.Dispose(isDisposing);
    }

    private sealed partial class MarathonScoreRow : Container
    {
        private readonly Box hover;
        public override bool HandlePositionalInput => true;

        public MarathonScoreRow(int rank, APIUser user, JToken score)
        {
            RelativeSizeAxes = Axes.X;
            Height = 76;
            Masking = true;
            CornerRadius = 6;
            string mods = string.Join(' ', score["mods"]?.Select(mod => mod.Value<string>("acronym")) ?? Array.Empty<string?>());
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(12, 10, 18, 205) },
                hover = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = 0 },
                new OsuSpriteText
                {
                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 14,
                    Text = $"#{rank}", Font = OsuFont.Numeric.With(size: 22), Colour = new Color4(191, 164, 222, 255),
                },
                new ClickableAvatar(user)
                {
                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 65, Size = new Vector2(58),
                    Masking = true, CornerRadius = 5,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 136, Right = 245, Top = 12, Bottom = 9 },
                    Children = new Drawable[]
                    {
                        new TruncatingSpriteText { RelativeSizeAxes = Axes.X, Text = user.Username, Font = OsuFont.GetFont(size: 20, weight: FontWeight.Bold) },
                        new UpdateableFlag(user.CountryCode)
                        {
                            Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Size = new Vector2(28, 20),
                        },
                        new OsuSpriteText
                        {
                            Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, X = 39,
                            Text = $"{score.Value<double>("accuracy"):P2}  ·  {score.Value<int>("max_combo")}x{(string.IsNullOrWhiteSpace(mods) ? "" : "  ·  " + mods)}",
                            Font = OsuFont.GetFont(size: 15), Colour = new Color4(205, 199, 215, 255),
                        },
                    },
                },
                new OsuSpriteText
                {
                    Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -18,
                    Text = score.Value<long>("total_score").ToString("N0"), Font = OsuFont.Numeric.With(size: 25),
                    Colour = new Color4(144, 212, 250, 255),
                },
            };
        }

        protected override bool OnHover(HoverEvent e)
        {
            hover.FadeTo(.28f, 100);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            hover.FadeOut(180);
            base.OnHoverLost(e);
        }
    }
}
