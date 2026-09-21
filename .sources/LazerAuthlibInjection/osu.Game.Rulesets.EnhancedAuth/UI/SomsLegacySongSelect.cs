#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Input.Bindings;
using osu.Game.Online.Leaderboards;
using osu.Game.Overlays.Mods;
using osu.Game.Overlays;
using osu.Game.Rulesets;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Screens.Select;
using osu.Game.Screens.Select.Filter;
using osu.Game.Skinning;
using osu.Game.Utils;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>Independent stable song browser; native song select owns filtering, selection and play.</summary>
public sealed partial class SomsLegacySongSelect : SomsLegacyComponent, IKeyBindingHandler<GlobalAction>
{
    // LegacySkin's x480 -> x768 conversion is 1.6 in the current client source.
    // Keep the inverse here because older osu.Game reference packages do not expose it.
    private const float skin_scale = 480f / 768;
    private readonly SongSelect owner;
    private IBindable<WorkingBeatmap>? beatmap;
    private Bindable<RulesetInfo>? ruleset;
    private IBindable<IReadOnlyList<Mod>>? mods;
    private ModSettingChangeTracker? modSettings;
    private ScheduledDelegate? scoreRefresh;
    private CancellationTokenSource? summaryDifficultyCancellation;
    private IBindable<StarDifficulty>? summaryDifficulty;
    private Ruleset? displayRuleset;
    private RulesetInfo? displayRulesetInfo;
    private TruncatingSpriteText? difficultyText, timingText;
    [Resolved(canBeNull: true)] private BeatmapDifficultyCache? difficultyCache { get; set; }
    private OsuConfigManager config = null!;
    private RulesetStore rulesets = null!;
    private BeatmapCarousel? carousel;
    private FilterControl? filter;
    private Container? scene, rows, summary, scores, options, modeMenu, filterMenu, modeIcon;
    private readonly Dictionary<string, LegacyMapRow> rowDrawables = new();
    private readonly List<Action?> optionActions = new();
    private OsuTextBox? search;
    private SomsLegacyUserPanel? userPanel;
    private LeaderboardManager? leaderboard;
    [Resolved(canBeNull: true)] private OsuGame? game { get; set; }
    [Resolved] private BeatmapManager beatmaps { get; set; } = null!;
    [Resolved(canBeNull: true)] private DialogOverlay? dialogs { get; set; }
    private object? lastItems;
    private Guid selectedId;
    private readonly List<GroupedBeatmap> filtered = new();
    private readonly Dictionary<string, List<GroupedBeatmap>> groupedMaps = new();
    private GroupedBeatmap[] allMaps = Array.Empty<GroupedBeatmap>();
    private readonly List<(GroupedBeatmap Beatmap, bool Set)> displayed = new();
    private int centre, scoreOffset;
    private float browseCentre, groupOffset;
    private string[] groups = Array.Empty<string>();
    private Dictionary<string, int> groupCounts = new();
    private Dictionary<Guid, int> setCounts = new();
    private string? selectedGroup;
    private double lastPoll;
    private string query = "";
    private BeatmapLeaderboardScope scope = BeatmapLeaderboardScope.Local;
    private bool exactMods;
    private bool searchBlocked;
    private TruncatingSpriteText? scopeText, status, sortText, groupText, modsText;

    public SomsLegacySongSelect(SongSelect owner) { this.owner = owner; Name = "soms-legacy-song-select"; }

    [BackgroundDependencyLoader]
    private void load(IBindable<WorkingBeatmap> workingBeatmap, Bindable<RulesetInfo> currentRuleset,
        IBindable<IReadOnlyList<Mod>> selectedMods, OsuConfigManager config, RulesetStore rulesets)
    {
        this.config = config; this.rulesets = rulesets;
        beatmap = workingBeatmap.GetBoundCopy(); ruleset = currentRuleset.GetBoundCopy(); mods = selectedMods.GetBoundCopy();
        beatmap.BindValueChanged(_ => Scheduler.AddOnce(selectedChanged));
        ruleset.BindValueChanged(_ => Scheduler.AddOnce(selectedChanged));
        mods.BindValueChanged(_ =>
        {
            modSettings?.Dispose();
            modSettings = new ModSettingChangeTracker(mods.Value) { SettingChanged = _ => Scheduler.AddOnce(modsChanged) };
            Scheduler.AddOnce(modsChanged);
        }, true);
    }

    protected override void Rebuild(ISkinSource skin)
    {
        carousel = SomsLegacyInterfacePatch.Member<BeatmapCarousel>(owner, "carousel");
        filter = SomsLegacyInterfacePatch.Member<FilterControl>(owner, "FilterControl");
        if (carousel == null || filter == null) return;
        var nativeSearch = SomsLegacyInterfacePatch.Member<object>(filter, "searchTextBox");
        if (nativeSearch != null && SomsLegacyInterfacePatch.Member<Bindable<string>>(nativeSearch, "Current") is { } currentSearch)
            query = currentSearch.Value;
        HideNative(new[] { "mainContent", "skinnableContent" }.Select(n => SomsLegacyInterfacePatch.Member<Drawable>(owner, n)).OfType<Drawable>(), keepUpdating: true);
        Alpha = 1; lastItems = null; rowDrawables.Clear();
        AddInternal(new DrawSizePreservingFillContainer
        {
            TargetDrawSize = new Vector2(640, 480),
            Child = scene = new BrowserSurface(scrollBrowser, quickBrowse, () => canBrowse)
            { RelativeSizeAxes = Axes.Both, Name = "legacy-song-browser" }
        });
        scene.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = .24f });
        var top = NaturalSpriteFor(skin, "songselect-top");
        if (top != null)
        {
            top.Scale = new Vector2(skin_scale);
            top.Name = "soms-stable-songselect-top";
            scene.Add(top);
        }
        else scene.Add(new Box { RelativeSizeAxes = Axes.X, Height = 91, Colour = Color4.Black });
        scene.Add(summary = new Container { RelativeSizeAxes = Axes.X, Height = 64, Padding = new MarginPadding { Left = 3, Right = 5 } });
        var controls = new Container { RelativePositionAxes = Axes.X, RelativeSizeAxes = Axes.X, X = .42f, Width = .575f, Y = 17, Height = 33 };
        scene.Add(controls);
        var grouping = new Container { RelativeSizeAxes = Axes.X, Width = .49f, Height = 18 };
        var sorting = new Container { RelativeSizeAxes = Axes.X, RelativePositionAxes = Axes.X, X = .51f, Width = .49f, Height = 18 };
        controls.Add(grouping); controls.Add(sorting);
        grouping.Add(Text("Группировать", 0, 0, .5f, 16, true, new Color4(151, 208, 238, 255)));
        sorting.Add(Text("Сортировать", 0, 0, .5f, 16, true, new Color4(182, 216, 143, 255)));
        var group = new LegacyButton(skin, "", "", () => toggleFilterMenu(true), Color4.Black)
        { Name = "soms-selection-group", RelativeSizeAxes = Axes.X, RelativePositionAxes = Axes.X, X = .5f, Width = .5f, Height = 16, Masking = true, CornerRadius = 3, BorderThickness = .5f, BorderColour = new Color4(151, 208, 238, 255) };
        group.Add(groupText = Text("", 3, 1, .86f, 11, true));
        group.Add(new SpriteIcon { Icon = FontAwesome.Solid.ChevronDown, Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -4, Size = new Vector2(9) });
        grouping.Add(group);
        var sort = new LegacyButton(skin, "", "", () => toggleFilterMenu(false), Color4.Black)
        { Name = "soms-selection-sort", RelativeSizeAxes = Axes.X, RelativePositionAxes = Axes.X, X = .5f, Width = .5f, Height = 16, Masking = true, CornerRadius = 3, BorderThickness = .5f, BorderColour = new Color4(182, 216, 143, 255) };
        sort.Add(sortText = Text("", 3, 1, .86f, 11, true));
        sort.Add(new SpriteIcon { Icon = FontAwesome.Solid.ChevronDown, Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -4, Size = new Vector2(9) });
        sorting.Add(sort);

        var tabs = new Container { RelativeSizeAxes = Axes.X, Y = 19, Height = 14 };
        controls.Add(tabs);
        var tabNames = new[] { "Коллекции", "По дате игры", "По артисту", "По сложности", "Всё вместе" };
        var tabGroups = new[] { "Collections", "LastPlayed", "Artist", "Difficulty", "None" };
        for (int i = 0; i < tabNames.Length; i++)
        {
            string value = tabGroups[i];
            tabs.Add(new LegacyButton(skin, "selection-tab", tabNames[i], () => setGroup(value), new Color4(220, 10, 65, 255), showCaption: true)
            { Name = "soms-selection-tab-" + value, RelativePositionAxes = Axes.X, RelativeSizeAxes = Axes.X, X = i / 5f, Width = .2f, Height = 14 });
        }
        var searchArea = new Container { RelativeSizeAxes = Axes.X, RelativePositionAxes = Axes.X, X = .55f, Width = .45f, Y = 51, Height = 23 };
        scene.Add(searchArea);
        searchArea.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(130, 35, 95, 170) });
        searchArea.Add(Text("Поиск:", 8, 3, 45, 13, colour: new Color4(180, 255, 50, 255)));
        searchArea.Add(search = new LegacySearchBox
        {
            RelativeSizeAxes = Axes.X, X = 50, Width = 1, Height = 23, Text = query,
            PlaceholderText = "введите название", CornerRadius = 0,
        });
        // Reserve the label's width without letting the textbox extend past the viewport.
        searchArea.Padding = new MarginPadding { Right = 50 };
        search.Current.BindValueChanged(e => { query = e.NewValue; filter.Search(query); });
        updateFilterLabels();
        var left = new Container { RelativeSizeAxes = Axes.X, Width = .30f, Y = 73, Height = 347 };
        scene.Add(left);
        var scopes = new LegacyButton(skin, "", "", cycleScope, Color4.Black)
        { X = 5, Width = 190, Height = 15, Masking = true, CornerRadius = 3, BorderThickness = .5f, BorderColour = new Color4(50, 200, 255, 255) };
        scopes.Add(scopeText = Text(scopeName(), 3, 1, 168, 12));
        scopes.Add(new SpriteIcon { Icon = FontAwesome.Solid.ChevronDown, Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, X = -4, Size = new Vector2(10) });
        left.Add(scopes);
        left.Add(new LegacyButton(skin, "", "М", () => { exactMods = !exactMods; fetchScores(); }, Color4.Black)
        { TooltipText = "Рекорды с выбранными модами", X = 207, Width = 16, Height = 15 });
        left.Add(scores = new ScrollSurface(delta =>
        {
            if (!canBrowse || (leaderboard?.Scores.Value?.AllScores.Count() ?? 0) <= 6) return false;
            scoreOffset = Math.Max(0, scoreOffset + delta); drawScores(); return true;
        })
        { RelativeSizeAxes = Axes.X, Y = 25, Height = 317, Masking = true });
        scene.Add(rows = new BrowserSurface(scrollBrowser, quickBrowse, () => canBrowse)
        { RelativeSizeAxes = Axes.X, RelativePositionAxes = Axes.X, X = .55f, Width = .45f, Y = 76, Height = 348, Masking = true });
        scene.Add(status = Text("Загрузка карт…", 7, 402, 235, 9));
        var footer = new Container { RelativeSizeAxes = Axes.X, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Height = 56 };
        scene.Add(footer);
        footer.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black });
        var bottom = NaturalSpriteFor(skin, "songselect-bottom");
        if (bottom != null) { bottom.Scale = new Vector2(skin_scale); bottom.Anchor = bottom.Origin = Anchor.BottomLeft; footer.Add(bottom); }
        else footer.Add(new Box { RelativeSizeAxes = Axes.X, Height = 2, Colour = new Color4(45, 90, 255, 255) });
        // Stable's v2 selection assets use a bottom-left origin, not a stretched footer.
        // The x142 mode-button position is also visible in the local stable screenshots
        // (screenshot009/010): the Fumo 1142x944 decoration starts at x319.5 at 1080p.
        // Asset pixels are x768; this browser's coordinates are x480 (see LegacySkin).
        footer.Add(new LegacyButton(skin, "menu-back", "Назад", () => owner.Exit(), naturalSize: true)
        { Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Size = new Vector2(140, 56) });
        footer.Add(new LegacyButton(skin, "selection-mode", "Режим", toggleModeMenu, naturalSize: true)
        { Name = "soms-selection-mode", Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, X = 142, Size = new Vector2(92, 90) * skin_scale });
        footer.Add(modeIcon = new Container { X = 160, Y = 10, Size = new Vector2(20) });
        footer.Add(new LegacyButton(skin, "selection-mods", "Моды · F1", toggleMods, naturalSize: true)
        { Name = "soms-selection-mods", Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, X = 199.5f, Size = new Vector2(77, 90) * skin_scale });
        footer.Add(new LegacyButton(skin, "selection-random", "Случайно · F2", () => carousel.NextRandom(), naturalSize: true)
        { Name = "soms-selection-random", Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, X = 247.625f, Size = new Vector2(77, 90) * skin_scale });
        footer.Add(new LegacyButton(skin, "selection-options", "Настройки · F3", toggleOptions, naturalSize: true)
        { Name = "soms-selection-options", Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, X = 295.75f, Size = new Vector2(77, 90) * skin_scale });
        footer.Add(userPanel = new SomsLegacyUserPanel { X = 392, Y = 6, Size = new Vector2(200, 49) });
        footer.Add(modsText = Text("", 7, -13, 240, 9));
        var play = new LegacyButton(skin, "", "", start, Color4.Transparent)
        { Name = "soms-selection-play", Anchor = Anchor.BottomRight, Origin = Anchor.BottomRight, Size = new Vector2(104, 96), TooltipText = "Играть · Enter" };
        var cookie = SpriteFor(skin, "menu-osu");
        if (cookie != null) play.Add(new Container { Anchor = Anchor.Centre, Origin = Anchor.Centre, Position = new Vector2(29, 22), Size = new Vector2(156), Child = cookie });
        else play.Add(Text("osu!", 12, 43, 86, 36));
        footer.Add(play);
        AddInternal(leaderboard = new LeaderboardManager());
        leaderboard.Scores.BindValueChanged(_ => drawScores());
        selectedChanged(); Schedule(fetchScores);
        scene.FadeInFromZero(220);
    }

    private void selectedChanged()
    {
        if (!LegacyEnabled || scene == null || beatmap == null) return;
        bool changed = selectedId != beatmap.Value.BeatmapInfo.ID;
        selectedId = beatmap.Value.BeatmapInfo.ID; scoreOffset = 0;
        drawSummary(); rebuildRows(changed); fetchScores();
        if (modsText != null) modsText.Text = (ruleset?.Value.Name ?? "osu!") + " · " +
            (mods?.Value.Count > 0 ? string.Join(" ", mods.Value.Select(m => m.Acronym)) : "Без модов");
        if (modeIcon != null && Skin != null)
        {
            modeIcon.Clear();
            if (SpriteFor(Skin, modeAsset(ruleset?.Value.OnlineID ?? 0) + "-med") is { } icon) modeIcon.Add(icon);
        }
    }

    private void modsChanged()
    {
        if (!LegacyEnabled || scene == null) return;
        updateAttributes();
        if (modsText != null) modsText.Text = (ruleset?.Value.Name ?? "osu!") + " · " +
            (mods?.Value.Count > 0 ? string.Join(" ", mods.Value.Select(m => m.Acronym)) : "Без модов");
        if (exactMods)
        {
            scoreRefresh?.Cancel();
            scoreRefresh = Scheduler.AddDelayed(fetchScores, 150);
        }
    }

    private void drawSummary()
    {
        if (summary == null || beatmap == null) return;
        summary.Clear(); var b = beatmap.Value.BeatmapInfo;
        summary.Add(Text(b.Metadata.Artist + " - " + b.Metadata.Title + " [" + b.DifficultyName + "]", 20, 0, .975f, 15, true));
        summary.Add(Text(b.Status.ToString() is "Ranked" or "Approved" ? "✓" : "?", 1, 1, 17, 22));
        var author = SomsMapperProfiles.FromMetadata(b.Metadata.Author);
        summary.Add(new OsuHoverContainer
        {
            X = 20, Y = 15, RelativeSizeAxes = Axes.X, Width = .38f, Height = 12,
            Action = () => game?.ShowUser(author),
            Child = Text("Автор: " + author.Username, 0, 0, 1, 10, true)
        });
        summary.Add(timingText = Text("", 0, 27, .41f, 10, true));
        summary.Add(Text(b.OnlineInfo is { } online ? $"Ноты: {online.CircleCount}  Слайдеры: {online.SliderCount}  Спиннеры: {online.SpinnerCount}" : "Сложность: " + b.DifficultyName, 0, 39, .41f, 9, true));
        summary.Add(difficultyText = Text("", 0, 50, .41f, 7, true));
        summaryDifficultyCancellation?.Cancel(); summaryDifficultyCancellation?.Dispose();
        summaryDifficulty?.UnbindAll();
        summaryDifficultyCancellation = new CancellationTokenSource();
        summaryDifficulty = difficultyCache?.GetBindableDifficulty(b, summaryDifficultyCancellation.Token, 150);
        summaryDifficulty?.BindValueChanged(_ => updateAttributes());
        updateAttributes();
    }

    private void updateAttributes()
    {
        if (beatmap == null || ruleset == null || difficultyText == null || timingText == null) return;
        var b = beatmap.Value.BeatmapInfo;
        if (!ReferenceEquals(displayRulesetInfo, ruleset.Value))
        {
            displayRulesetInfo = ruleset.Value;
            displayRuleset = ruleset.Value.CreateInstance();
        }
        var selected = mods?.Value.ToArray() ?? Array.Empty<Mod>();
        string attributes = string.Join(" ", displayRuleset!.GetBeatmapAttributesForDisplay(b, selected)
            .Select(a => $"{a.Acronym}:{a.AdjustedValue:0.#}"));
        difficultyText.Text = $"{attributes}  Star Rating: {summaryDifficulty?.Value.Stars ?? b.StarRating:0.##}★";
        double rate = ModUtils.CalculateRateWithMods(selected);
        if (!double.IsFinite(rate) || rate <= 0) rate = 1;
        timingText.Text = $"Длина: {TimeSpan.FromMilliseconds(Math.Max(0, b.Length) / rate):m\\:ss}  BPM: {b.BPM * rate:0.##}  Объекты: {(b.TotalObjectCount >= 0 ? b.TotalObjectCount.ToString() : "—")}";
    }

    protected override void Update()
    {
        base.Update();
        if (scene != null && userPanel != null) userPanel.Width = Math.Clamp(scene.DrawWidth - userPanel.X - 106, 1, 200);
        if (!LegacyEnabled || scene == null || carousel == null || !owner.IsCurrentScreen()) return;
        bool blockSearch = options != null || modeMenu != null || filterMenu != null ||
            SomsLegacyInterfacePatch.Member<ModSelectOverlay>(owner, "modSelectOverlay")?.State.Value == Visibility.Visible;
        if (search != null && searchBlocked != blockSearch)
        {
            if (blockSearch) SomsLegacyInputPatch.Block(search); else SomsLegacyInputPatch.Restore(search);
            searchBlocked = blockSearch;
        }
        if (Clock.CurrentTime - lastPoll < 16) return;
        lastPoll = Clock.CurrentTime;
        var items = carousel.GetCarouselItems();
        if (!ReferenceEquals(items, lastItems))
        {
            lastItems = items; filtered.Clear();
            if (items != null) filtered.AddRange(items.Select(i => i.Model).OfType<GroupedBeatmap>());
            rebuildIndex();
            rebuildRows();
        }
        if (status != null) status.Text = carousel.IsFiltering ? "Поиск…" : $"Доступно сложностей: {filtered.Count:N0}";
        if (carousel.CurrentBeatmap is { } current && current.ID != selectedId) { selectedId = current.ID; rebuildRows(); }
    }

    private void rebuildRows(bool recenter = true)
    {
        if (rows == null || Skin == null) return;
        displayed.Clear();
        if (selectedGroup != null && !groups.Contains(selectedGroup)) selectedGroup = null;
        // Folder view only needs counts. Do not enumerate every difficulty in every
        // collection again each time a folder scrolls or a mod changes.
        if (groups.Length > 0 && selectedGroup == null) { drawRows(); return; }
        IEnumerable<GroupedBeatmap> maps = selectedGroup != null ? groupedMaps[selectedGroup] : allMaps;
        Guid? setId = maps.FirstOrDefault(g => g.Beatmap.ID == selectedId)?.Beatmap.BeatmapSet?.ID;
        var seen = new HashSet<Guid>();
        foreach (var b in maps)
        {
            var id = b.Beatmap.BeatmapSet?.ID ?? b.Beatmap.ID;
            if (id == setId) displayed.Add((b, false));
            else if (seen.Add(id)) displayed.Add((b, true));
        }
        centre = Math.Max(0, displayed.FindIndex(r => r.Beatmap.Beatmap.ID == selectedId));
        browseCentre = recenter ? centre : Math.Clamp(browseCentre, 0, Math.Max(0, displayed.Count - 1));
        drawRows();
    }

    private void rebuildIndex()
    {
        groupedMaps.Clear();
        foreach (var map in filtered)
        {
            if (map.Group == null) continue;
            string group = map.Group.Title.ToString();
            if (!groupedMaps.TryGetValue(group, out var list)) groupedMaps[group] = list = new List<GroupedBeatmap>();
            list.Add(map);
        }
        groupCounts = groupedMaps.ToDictionary(g => g.Key, g => g.Value.Count);
        groups = groupCounts.Keys.ToArray();
        allMaps = filtered.DistinctBy(g => g.Beatmap.ID).ToArray();
        setCounts = allMaps.GroupBy(g => g.Beatmap.BeatmapSet?.ID ?? g.Beatmap.ID).ToDictionary(g => g.Key, g => g.Count());
    }

    // Scrolling only arranges the bounded visible row cache. It does not select a map,
    // restart preview audio, or traverse/rebuild the complete beatmap library each frame.
    private void drawRows(bool animate = true)
    {
        if (rows == null || Skin == null) return;
        var visibleKeys = new HashSet<string>();
        foreach (var old in rows.Children.Where(d => d.Name == "soms-selection-no-maps" || d.Name == "soms-selection-folder-back").ToArray()) rows.Remove(old, true);
        if (groups.Length > 0 && selectedGroup == null)
        {
            groupOffset = Math.Clamp(groupOffset, 0, Math.Max(0, groups.Length - 7));
            int first = (int)groupOffset;
            foreach (var (name, index) in groups.Skip(first).Take(8).Select((g, i) => (g, i)))
            {
                string key = "group:" + name;
                visibleKeys.Add(key);
                if (!rowDrawables.TryGetValue(key, out var folder))
                {
                    folder = new LegacyMapRow(Skin, name + $" ({groupCounts[name]:N0} карт)", () => { selectedGroup = name; rebuildRows(); });
                    rowDrawables.Add(key, folder); rows.Add(folder);
                    folder.Position = new Vector2(50, index * 49 + 20); folder.FadeInFromZero(160);
                }
                folder.SetFolderTitle(name + $" ({groupCounts[name]:N0} карт)");
                float distance = index - (groupOffset - first);
                folder.MoveTo(new Vector2(18 + Math.Abs(distance - 3) * Math.Abs(distance - 3) * 2, distance * 49), animate ? 220 : 0, Easing.OutQuint);
            }
            removeOldRows();
            return;
        }
        if (displayed.Count == 0)
        {
            removeOldRows();
            if (!rows.Children.Any(d => d.Name == "soms-selection-no-maps"))
                rows.Add(new TruncatingSpriteText { Name = "soms-selection-no-maps", Text = "Нет подходящих карт", Position = new Vector2(25, 145), Font = SomsLegacyFont.Font(16) });
            return;
        }
        // Bounded drawable count, including on large libraries.
        int before = Math.Min(3, (displayed.Count - 1) / 2);
        int after = Math.Min(4, displayed.Count - before - 1);
        for (int distance = -before; distance <= after; distance++)
        {
            int i = ((int)browseCentre + distance + displayed.Count) % displayed.Count;
            float visualDistance = distance - (browseCentre - (int)browseCentre);
            var row = displayed[i]; var b = row.Beatmap.Beatmap;
            bool selected = b.ID == selectedId;
            float inset = visualDistance * visualDistance * 3 + (row.Set ? 48 : 0);
            string key = (row.Set ? "set:" : "map:") + b.ID;
            visibleKeys.Add(key);
            if (!rowDrawables.TryGetValue(key, out var button))
            {
                button = new LegacyMapRow(Skin, b, row.Set, setCounts.GetValueOrDefault(b.BeatmapSet?.ID ?? b.ID, 1),
                    () => select(row.Beatmap, true));
                rowDrawables.Add(key, button); rows.Add(button);
                button.Position = new Vector2(inset + (animate ? 32 : 0), 116 + visualDistance * 58);
                if (animate) button.FadeInFromZero(160);
            }
            button.Action = () => select(row.Beatmap, true);
            button.ContextAction = () => { if (canBrowse) { select(row.Beatmap); showOptions(b); } };
            button.SetSelected(selected);
            button.MoveTo(new Vector2(inset, 116 + visualDistance * 58), animate ? 230 : 0, Easing.OutQuint);
        }
        removeOldRows();
        if (groups.Length > 0)
            rows.Add(new LegacyButton(Skin, "", "‹ " + selectedGroup, () => { selectedGroup = null; rebuildRows(); })
            { Name = "soms-selection-folder-back", X = 4, RelativeSizeAxes = Axes.X, Width = .9f, Height = 21, Depth = -1 });

        void removeOldRows()
        {
            foreach (var key in rowDrawables.Keys.Where(key => !visibleKeys.Contains(key)).ToArray())
            {
                rows.Remove(rowDrawables[key], true);
                rowDrawables.Remove(key);
            }
        }
    }

    private void select(GroupedBeatmap map, bool playIfSelected = false)
    {
        if (playIfSelected && map.Beatmap.ID == beatmap?.Value.BeatmapInfo.ID) { start(); return; }
        AccessTools.Method(typeof(SongSelect), "queueBeatmapSelection").Invoke(owner, new object[] { map });
        selectedId = map.Beatmap.ID; rebuildRows();
    }

    private void moveSelection(int delta)
    {
        if (selectedGroup == null && groups.Length > 0) { groupOffset += delta; drawRows(); return; }
        if (displayed.Count > 0) select(displayed[((centre + delta) % displayed.Count + displayed.Count) % displayed.Count].Beatmap);
    }

    private void moveSet(int direction)
    {
        if (selectedGroup == null && groups.Length > 0) { moveSelection(direction); return; }
        if (displayed.Count == 0) return;
        var current = displayed[centre].Beatmap.Beatmap;
        Guid currentSet = current.BeatmapSet?.ID ?? current.ID;
        for (int offset = 1; offset < displayed.Count; offset++)
        {
            int index = (centre + direction * offset + displayed.Count) % displayed.Count;
            var candidate = displayed[index].Beatmap;
            Guid targetSet = candidate.Beatmap.BeatmapSet?.ID ?? candidate.Beatmap.ID;
            if (targetSet == currentSet) continue;
            // Collapsed rows represent the first difficulty. Arrow navigation instead
            // opens the last visible difficulty in the destination set, in either direction.
            select(filtered.Last(map => (map.Beatmap.BeatmapSet?.ID ?? map.Beatmap.ID) == targetSet
                && (selectedGroup == null || map.Group?.Title.ToString() == selectedGroup)));
            return;
        }
    }

    private bool canBrowse => LegacyEnabled && scene != null && owner.IsCurrentScreen()
        && options == null && modeMenu == null && filterMenu == null
        && SomsLegacyInterfacePatch.Member<ModSelectOverlay>(owner, "modSelectOverlay")?.State.Value != Visibility.Visible;

    private void scrollBrowser(float delta, bool animate)
    {
        if (!canBrowse) return;
        if (selectedGroup == null && groups.Length > 0)
            groupOffset = Math.Clamp(groupOffset + delta, 0, Math.Max(0, groups.Length - 7));
        else browseCentre = Math.Clamp(browseCentre + delta, 0, Math.Max(0, displayed.Count - 1));
        drawRows(animate);
    }

    private void quickBrowse(float position)
    {
        if (!canBrowse) return;
        position = Math.Clamp(position, 0, 1);
        if (selectedGroup == null && groups.Length > 0) groupOffset = position * Math.Max(0, groups.Length - 7);
        else browseCentre = position * Math.Max(0, displayed.Count - 1);
        drawRows(false);
    }

    private void start()
    {
        if (carousel?.CurrentBeatmap is not { } selected) return;
        Action begin = () => AccessTools.Method(owner.GetType(), "OnStart").Invoke(owner, null);
        AccessTools.Method(typeof(SongSelect), "SelectAndRun").Invoke(owner, new object[] { selected, begin });
    }

    private void toggleMods() => SomsLegacyInterfacePatch.Member<ModSelectOverlay>(owner, "modSelectOverlay")?.ToggleVisibility();

    private void cycleMode()
    {
        if (ruleset == null || ruleset.Disabled) return;
        SomsAssistModes.Ensure();
        var available = modeChoices();
        if (available.Length > 0) SomsAssistModes.Presented.Value = available[(Array.FindIndex(available, r => r.ShortName == SomsAssistModes.Presented.Value.ShortName) + 1) % available.Length];
    }

    private RulesetInfo[] modeChoices() => rulesets.AvailableRulesets.Where(r => r.OnlineID is >= 0 and <= 3)
        .Concat(new[] { rulesets.GetRuleset("osurx"), rulesets.GetRuleset("osuap") }.OfType<RulesetInfo>()).OrderBy(r => r.OnlineID).ToArray();

    private static string modeAsset(int mode) => mode switch { 1 => "mode-taiko", 2 => "mode-fruits", 3 => "mode-mania", _ => "mode-osu" };

    private void toggleModeMenu()
    {
        if (scene == null || Skin == null || ruleset == null) return;
        if (modeMenu != null) { closeMenu(ref modeMenu); return; }
        closeMenu(ref filterMenu);
        modeMenu = new Surface
        {
            Name = "soms-selection-mode-menu", Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft,
            X = 142, Y = -56, Size = new Vector2(280, 384), Depth = -5,
        };
        modeMenu.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = .65f });
        SomsAssistModes.Ensure();
        foreach (var mode in modeChoices().OrderBy(r => r.OnlineID))
        {
            var choice = mode;
            var button = new LegacyButton(Skin, "", "", () =>
            {
                if (!SomsAssistModes.Presented.Disabled) SomsAssistModes.Presented.Value = choice;
                closeMenu(ref modeMenu);
            }, Color4.Transparent)
            {
                Name = "soms-selection-mode-" + mode.OnlineID, Y = mode.OnlineID * 64,
                RelativeSizeAxes = Axes.X, Height = 64, Alpha = ruleset.Disabled ? .4f : 1,
            };
            art(button, Skin, modeAsset(mode.OnlineID), 8, 6, 48, 50);
            button.Add(Text(mode.Name, 70, 21, 200, 24));
            modeMenu.Add(button);
        }
        scene.Add(modeMenu);
        modeMenu.FadeInFromZero(180).MoveToY(-48).MoveToY(-56, 200, Easing.OutQuint);
    }

    private void closeMenu(ref Container? menu)
    {
        if (menu == null || scene == null) return;
        var closing = menu; menu = null;
        // Remove input immediately; the fading copy must not consume the next click.
        SomsLegacyInputPatch.Block(closing);
        closing.FadeOut(100).Expire();
    }

    private void setGroup(string value)
    {
        selectedGroup = null; groupOffset = 0;
        if (status != null) status.Text = "Группировка карт…";
        config.GetBindable<GroupMode>(setting("SongSelectGroupMode")).Value = Enum.Parse<GroupMode>(value);
        updateFilterLabels(); closeMenu(ref filterMenu);
    }

    private void toggleFilterMenu(bool grouping)
    {
        if (scene == null || Skin == null) return;
        if (filterMenu != null) { closeMenu(ref filterMenu); return; }
        closeMenu(ref modeMenu);
        var values = grouping ? new[] { "None", "Collections", "Artist", "Author", "Difficulty", "RankedStatus", "LastPlayed", "RankAchieved" }
            : new[] { "Title", "Artist", "Author", "Difficulty", "DateAdded", "LastPlayed", "BPM", "Length" };
        filterMenu = new Surface
        {
            Name = "soms-selection-filter-menu", RelativePositionAxes = Axes.X, RelativeSizeAxes = Axes.X,
            X = grouping ? .56f : .855f, Width = .145f, Y = 34, Height = values.Length * 17 + 4, Depth = -6,
        };
        filterMenu.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(0, 0, 0, .95f) });
        for (int i = 0; i < values.Length; i++)
        {
            string value = values[i];
            filterMenu.Add(new LegacyButton(Skin, "", filterLabel(value), () =>
            {
                if (grouping) setGroup(value);
                else { config.GetBindable<SortMode>(setting("SongSelectSortingMode")).Value = Enum.Parse<SortMode>(value); updateFilterLabels(); closeMenu(ref filterMenu); }
            }, Color4.Transparent) { RelativeSizeAxes = Axes.X, Y = 2 + i * 17, Height = 17 });
        }
        scene.Add(filterMenu); filterMenu.FadeInFromZero(120);
    }

    private static string filterLabel(string value) => value switch
    {
        "None" => "Всё вместе", "Collections" => "Коллекции", "Artist" => "По артисту", "Author" => "По автору",
        "Title" => "По названию", "Difficulty" => "По сложности", "DateAdded" => "По дате добавления",
        "LastPlayed" => "По дате игры", "RankAchieved" => "По результатам", "RankedStatus" => "По статусу", "BPM" => "По BPM", "Length" => "По длине", _ => value,
    };

    private void cycleSort()
    {
        var modes = new[] { "Title", "Artist", "Difficulty", "DateAdded", "BPM", "Length" }.Select(Enum.Parse<SortMode>).ToArray();
        var current = config.GetBindable<SortMode>(setting("SongSelectSortingMode"));
        current.Value = modes[(Array.IndexOf(modes, current.Value) + 1) % modes.Length]; updateFilterLabels();
    }

    private void cycleGroup()
    {
        selectedGroup = null; groupOffset = 0;
        var modes = new[] { "None", "Collections", "Artist", "Difficulty", "RankedStatus" }.Select(Enum.Parse<GroupMode>).ToArray();
        var current = config.GetBindable<GroupMode>(setting("SongSelectGroupMode"));
        current.Value = modes[(Array.IndexOf(modes, current.Value) + 1) % modes.Length]; updateFilterLabels();
    }

    private void updateFilterLabels()
    {
        if (sortText != null) sortText.Text = filterLabel(config.Get<SortMode>(setting("SongSelectSortingMode")).ToString());
        if (groupText != null) groupText.Text = filterLabel(config.Get<GroupMode>(setting("SongSelectGroupMode")).ToString());
    }

    private string scopeName() => scope switch
    {
        BeatmapLeaderboardScope.Local => "Локальный топ", BeatmapLeaderboardScope.Global => "Топ мира",
        BeatmapLeaderboardScope.Country => "Топ страны", _ => "Топ друзей"
    };

    private void cycleScope()
    {
        scope = (BeatmapLeaderboardScope)(((int)scope + 1) % 4);
        if (scopeText != null) scopeText.Text = scopeName(); scoreOffset = 0; fetchScores();
    }

    private void fetchScores()
    {
        if (leaderboard?.IsLoaded != true || !LegacyEnabled || beatmap == null || ruleset == null) return;
        leaderboard.FetchWithCriteria(new LeaderboardCriteria(beatmap.Value.BeatmapInfo, ruleset.Value, scope, exactMods ? mods?.Value.ToArray() : null));
    }

    private void drawScores()
    {
        if (scores == null || Skin == null || !LegacyEnabled) return;
        scores.Clear(); var result = leaderboard?.Scores.Value;
        string? hint = result == null ? "Загрузка рекордов…" : result.FailState switch
        {
            LeaderboardFailState.NotSupporter => "Нужен supporter", LeaderboardFailState.NotLoggedIn => "Войдите в аккаунт",
            LeaderboardFailState.BeatmapUnavailable => "Нет онлайн-рейтинга", LeaderboardFailState.NetworkFailure => "Ошибка загрузки. Повторить?",
            not null => "Рейтинг недоступен", _ => result.TopScores.Count == 0 ? "Рекордов пока нет" : null
        };
        if (hint != null)
        {
            if (result?.FailState == LeaderboardFailState.NetworkFailure)
                scores.Add(new LegacyButton(Skin, "", hint, () => { if (leaderboard?.CurrentCriteria is { } c) leaderboard.FetchWithCriteria(c, true); })
                { X = 9, Y = 50, RelativeSizeAxes = Axes.X, Width = .95f, Height = 45 });
            else scores.Add(new TruncatingSpriteText { Text = hint, MaxWidth = 250, Anchor = Anchor.Centre, Origin = Anchor.Centre, Font = SomsLegacyFont.Font(13), Shadow = true });
            return;
        }
        var all = result!.AllScores.ToArray(); scoreOffset = Math.Clamp(scoreOffset, 0, Math.Max(0, all.Length - 5));
        foreach (var (score, index) in all.Skip(scoreOffset).Take(6).Select((s, i) => (s, i)))
        {
            var button = new LegacyButton(Skin, "menu-button-background", "", () => { if (((ISongSelect)owner).CanPresentScore) ((ISongSelect)owner).PresentScore(score); })
            { X = 4, Y = index * 49, RelativeSizeAxes = Axes.X, Width = .98f, Height = 46 };
            button.Add(Text("#" + (score.Position ?? (scoreOffset + index + 1)), 7, 5, 31, 11));
            art(button, Skin, "ranking-" + score.Rank + "-small", 7, 21, 30, 22);
            button.Add(Text(score.User.Username, 43, 4, 165, 13));
            button.Add(Text($"{score.TotalScore:N0}  ({score.MaxCombo}x)", 43, 20, 190, 10));
            button.Add(Text($"{score.Accuracy:P2}   {string.Join(" ", score.Mods.Select(m => m.Acronym))}", 43, 33, 190, 9));
            scores.Add(button);
        }
    }

    private void toggleOptions()
    {
        if (options != null) { closeMenu(ref options); optionActions.Clear(); return; }
        showOptions(beatmap?.Value.BeatmapInfo);
    }

    private void showOptions(BeatmapInfo? b)
    {
        if (scene == null || Skin == null) return;
        closeMenu(ref options);
        closeMenu(ref modeMenu); closeMenu(ref filterMenu);
        options = new Surface { Name = "soms-selection-options-menu", RelativeSizeAxes = Axes.Both, Depth = -10 };
        options.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(0, 0, 0, .85f) });
        string title = b == null ? "" : b.Metadata.Artist + " - " + b.Metadata.Title;
        options.Add(Text("Выбрана: " + title, 4, 4, .98f, 20, true));
        options.Add(Text("Что вы хотите сделать с этой картой?", 4, 28, .98f, 20, true));
        var panel = new Container { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Y = 106, Size = new Vector2(455, 290) };
        options.Add(panel); optionActions.Clear();
        add("Управление коллекциями", b == null ? null : () => showCollections(b), new Color4(135, 182, 20, 255));
        add("Удалить…", b?.BeatmapSet is { } set ? () => owner.Delete(set) : null, new Color4(224, 53, 0, 255));
        add(b?.LastPlayed == null ? "Убрать из несыгранных" : "Отметить как несыгранную", b == null ? null : () =>
        {
            if (b.LastPlayed == null) beatmaps.MarkPlayed(b); else beatmaps.MarkNotPlayed(b);
        }, new Color4(167, 88, 182, 255));
        add("Очистить локальный топ", b == null || dialogs == null ? null : () => dialogs.Push(new BeatmapClearScoresDialog(b, () => Schedule(fetchScores))), new Color4(167, 88, 182, 255));
        add("Редактировать", b != null && owner is SoloSongSelect solo ? () => solo.Edit(b) : null, new Color4(224, 53, 0, 255));
        add("Отмена", () => { }, new Color4(105, 105, 105, 255));
        options.Add(new LegacyButton(Skin, "", "Другие действия и фильтры…", showOtherOptions, new Color4(0, 0, 0, .3f))
        { Anchor = Anchor.BottomCentre, Origin = Anchor.BottomCentre, Y = -39, Size = new Vector2(260, 22) });
        scene.Add(options); options.FadeInFromZero(150); panel.MoveToY(116).MoveToY(106, 220, Easing.OutQuint);

        void add(string label, Action? action, Color4 colour)
        {
            int index = optionActions.Count;
            optionActions.Add(action);
            panel.Add(new LegacyButton(Skin, "", $"{index + 1}. {label}", () => activateOption(index), colour, textSize: 28)
            { Name = "soms-selection-option-" + (index + 1), Y = index * 50, RelativeSizeAxes = Axes.X, Height = 35, Alpha = action == null ? .45f : 1 });
        }
    }

    private void activateOption(int index)
    {
        if (index < 0 || index >= optionActions.Count || optionActions[index] is not { } action) return;
        closeMenu(ref options); optionActions.Clear(); action();
    }

    private void showOtherOptions()
    {
        closeMenu(ref options);
        if (scene == null || Skin == null) return;
        options = new Surface { RelativeSizeAxes = Axes.Both, Depth = -10 };
        options.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(0, 0, 0, .9f) });
        options.Add(Text("Другие действия и фильтры", 8, 14, .95f, 24, true));
        var panel = new Container { Anchor = Anchor.Centre, Origin = Anchor.Centre, Size = new Vector2(455, 340) };
        options.Add(panel); optionActions.Clear();
        add("Сбросить поиск и фильтры", () =>
        {
            filter?.Search(""); query = ""; if (search != null) search.Text = "";
            config.GetBindable<double>(setting("DisplayStarsMinimum")).SetDefault();
            config.GetBindable<double>(setting("DisplayStarsMaximum")).SetDefault();
            if (filter != null) SomsLegacyInterfacePatch.Member<Bindable<string>>(filter, "configCollectionFilter")?.SetDefault();
            setGroup("None"); owner.UnscopeBeatmapSet();
        });
        add("Конверты: " + (config.Get<bool>(setting("ShowConvertedBeatmaps")) ? "показывать" : "скрывать"), () =>
        { var value = config.GetBindable<bool>(setting("ShowConvertedBeatmaps")); value.Value = !value.Value; });
        if (beatmap?.Value.BeatmapInfo is { } b)
            foreach (var item in owner.GetForwardActions(b).Where(i => i.Action.Value != null).Take(5)) add(item.Text.Value.ToString(), item.Action.Value!);
        add("Назад", toggleOptions);
        scene.Add(options); options.FadeInFromZero(150);
        void add(string label, Action action)
        {
            int index = optionActions.Count; optionActions.Add(action);
            panel.Add(new LegacyButton(Skin, "", $"{index + 1}. {label}", () => activateOption(index), new Color4(70, 70, 90, 255), textSize: 18)
            { Y = index * 40, RelativeSizeAxes = Axes.X, Height = 34 });
        }
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (!LegacyEnabled || scene == null || !owner.IsCurrentScreen()) return false;
        if (options != null)
        {
            if (options is LegacyCollectionsMenu) return true;
            if (e.Key == Key.Escape) { closeMenu(ref options); optionActions.Clear(); return true; }
            if (!e.Repeat && e.Key is >= Key.Number1 and <= Key.Number9) { activateOption(e.Key - Key.Number1); return true; }
            return e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter or Key.F1 or Key.F2 or Key.F3;
        }
        if (modeMenu != null || filterMenu != null)
        {
            if (e.Key == Key.Escape) { closeMenu(ref modeMenu); closeMenu(ref filterMenu); return true; }
            return e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter;
        }
        if (!canBrowse || e.ControlPressed || e.AltPressed || e.SuperPressed) return false;
        switch (e.Key)
        {
            case Key.Left: moveSet(-1); return true;
            case Key.Up: moveSelection(-1); return true;
            case Key.Right: moveSet(1); return true;
            case Key.Down: moveSelection(1); return true;
            case Key.PageUp: moveSelection(-5); return true;
            case Key.PageDown: moveSelection(5); return true;
        }
        return false;
    }

    // The prioritised global binding container dispatches F1/F2/F3 before raw key events
    // (and otherwise falls through to music controls). Respect remapped bindings too.
    public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
    {
        if (!LegacyEnabled || scene == null || !owner.IsCurrentScreen()) return false;
        if (options is LegacyCollectionsMenu collections)
        {
            if (!e.Repeat)
            {
                if (e.Action is GlobalAction.Back or GlobalAction.ToggleBeatmapOptions) closeMenu(ref options);
                else if (e.Action == GlobalAction.Select) collections.CommitName();
            }
            return e.Action is GlobalAction.Back or GlobalAction.Select or GlobalAction.SelectPrevious or GlobalAction.SelectNext
                or GlobalAction.SelectNextRandom or GlobalAction.SelectPreviousRandom
                or GlobalAction.ToggleModSelection or GlobalAction.ToggleBeatmapOptions;
        }
        if (options != null || modeMenu != null || filterMenu != null)
        {
            if (e.Action == GlobalAction.Back || (options != null && e.Action == GlobalAction.ToggleBeatmapOptions))
            {
                if (!e.Repeat) { closeMenu(ref options); closeMenu(ref modeMenu); closeMenu(ref filterMenu); optionActions.Clear(); }
                return true;
            }
            return e.Action is GlobalAction.Select or GlobalAction.SelectPrevious or GlobalAction.SelectNext
                or GlobalAction.SelectNextRandom or GlobalAction.SelectPreviousRandom
                or GlobalAction.ToggleModSelection or GlobalAction.ToggleBeatmapOptions;
        }
        if (!canBrowse) return false;
        switch (e.Action)
        {
            case GlobalAction.SelectPrevious: moveSelection(-1); return true;
            case GlobalAction.SelectNext: moveSelection(1); return true;
            case GlobalAction.Select: if (!e.Repeat) start(); return true;
            case GlobalAction.ToggleModSelection: if (!e.Repeat) toggleMods(); return true;
            case GlobalAction.ToggleBeatmapOptions: if (!e.Repeat) toggleOptions(); return true;
            case GlobalAction.SelectNextRandom: if (!e.Repeat) carousel?.NextRandom(); return true;
            case GlobalAction.SelectPreviousRandom: if (!e.Repeat) carousel?.PreviousRandom(); return true;
        }
        return false;
    }

    public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e) { }

    protected override void RestoreLayout()
    {
        if (searchBlocked && search != null) SomsLegacyInputPatch.Restore(search);
        searchBlocked = false; rowDrawables.Clear(); optionActions.Clear();
        scene = rows = summary = scores = options = modeMenu = filterMenu = modeIcon = null;
        search = null; userPanel = null; leaderboard = null; lastItems = null;
        timingText = difficultyText = null;
        summaryDifficultyCancellation?.Cancel(); summaryDifficultyCancellation?.Dispose(); summaryDifficultyCancellation = null;
        summaryDifficulty?.UnbindAll(); summaryDifficulty = null;
        scoreRefresh?.Cancel();
        groupedMaps.Clear(); allMaps = Array.Empty<GroupedBeatmap>();
        groups = Array.Empty<string>(); groupCounts.Clear(); setCounts.Clear();
    }
    // The installed client's setting enum gains entries independently of the reference package.
    private static OsuSetting setting(string name) => Enum.Parse<OsuSetting>(name);
    protected override void Dispose(bool isDisposing) { modSettings?.Dispose(); beatmap?.UnbindAll(); ruleset?.UnbindAll(); mods?.UnbindAll(); base.Dispose(isDisposing); }

    private static TruncatingSpriteText Text(string value, float x, float y, float width, float size, bool relative = false, Color4? colour = null) => new()
    { Text = value, Position = new Vector2(x, y), Width = width, RelativeSizeAxes = relative ? Axes.X : Axes.None, Font = SomsLegacyFont.Font(size), Shadow = true, Colour = colour ?? Color4.White };

    private static void art(Container parent, ISkin skin, string asset, float x, float y, float width, float height, FillMode fill = FillMode.Fit)
    {
        if (SomsLegacyOverlayButton.Art(skin, asset, fill) is { } sprite)
            parent.Add(new Container { Position = new Vector2(x, y), Size = new Vector2(width, height), Child = sprite });
    }

    private partial class Surface : Container
    {
        protected override bool OnMouseDown(MouseDownEvent e) => true;
        protected override bool OnClick(ClickEvent e) => true;
    }
    private sealed partial class LegacySearchBox : FocusedTextBox
    {
        public override bool HandleLeftRightArrows => false;
        public override bool OnPressed(KeyBindingPressEvent<PlatformAction> e) =>
            e.Action is PlatformAction.SelectBackwardChar or PlatformAction.SelectForwardChar ? false : base.OnPressed(e);
        public LegacySearchBox()
        {
            HoldFocus = true;
            BackgroundUnfocused = Color4.Transparent;
            BackgroundFocused = new Color4(0, 0, 0, .25f);
        }
        protected override void LoadComplete()
        {
            base.LoadComplete();
            // OsuTextBox installs its overlay palette during load; apply the classic transparent field afterwards.
            BackgroundUnfocused = Color4.Transparent;
            BackgroundFocused = new Color4(0, 0, 0, .2f);
        }
        // Keep navigation keys available to the browser while typing a filter.
        protected override bool OnKeyDown(KeyDownEvent e) => e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Enter or Key.F1 or Key.F2 or Key.F3
            ? false : base.OnKeyDown(e);
    }
    private sealed partial class BrowserSurface : Surface
    {
        private readonly Action<float, bool> scroll;
        private readonly Action<float> quickScroll;
        private readonly Func<bool> available;
        public BrowserSurface(Action<float, bool> scroll, Action<float> quickScroll, Func<bool> available)
        { this.scroll = scroll; this.quickScroll = quickScroll; this.available = available; }
        protected override bool OnScroll(ScrollEvent e)
        {
            if (e.AltPressed || e.ControlPressed || e.SuperPressed) return false;
            if (available() && e.ScrollDelta.Y != 0) scroll(-e.ScrollDelta.Y, !e.IsPrecise);
            return true;
        }
        protected override bool OnDragStart(DragStartEvent e) => e.Button is MouseButton.Left or MouseButton.Right && available();
        protected override void OnDrag(DragEvent e)
        {
            if (!available()) return;
            if (e.Button == MouseButton.Right) quickScroll(ToLocalSpace(e.ScreenSpaceMousePosition).Y / Math.Max(1, DrawHeight));
            else scroll(-(ToLocalSpace(e.ScreenSpaceMousePosition).Y - ToLocalSpace(e.ScreenSpaceMousePosition - e.Delta).Y) / 58, false);
        }
    }
    private sealed partial class ScrollSurface : Surface
    {
        private readonly Func<int, bool> scroll;
        public ScrollSurface(Func<int, bool> scroll) => this.scroll = scroll;
        protected override bool OnScroll(ScrollEvent e)
        {
            if (e.AltPressed || e.ControlPressed || e.SuperPressed || e.ScrollDelta.Y == 0) return false;
            return scroll(e.ScrollDelta.Y > 0 ? -1 : 1);
        }
    }

    /// <summary>Only visible rows own drawables; selection moves existing rows instead of rebuilding their textures.</summary>
    private sealed partial class LegacyMapRow : OsuClickableContainer
    {
        public Action? ContextAction { get; set; }
        private Vector2? contextPress;
        private readonly Drawable background;
        private readonly Container labels;
        private readonly bool isSet, isFolder;
        private readonly TruncatingSpriteText title;
        private readonly Color4 activeText = Color4.Black;
        private readonly Color4 inactiveText = Color4.White;
        private bool? selected;
        private readonly BeatmapInfo? map;
        private readonly List<Drawable> stars = new();
        private CancellationTokenSource? difficultyCancellation;
        private IBindable<StarDifficulty>? difficulty;
        [Resolved(canBeNull: true)] private BeatmapDifficultyCache? difficultyCache { get; set; }

        public LegacyMapRow(ISkin skin, string folder, Action action)
        {
            isFolder = true; Action = action;
            Name = "soms-selection-collection-row";
            RelativeSizeAxes = Axes.X; Width = 1.1f; Height = 49;
            background = SpriteFor(skin, "menu-button-background", FillMode.Stretch) ?? new Box { RelativeSizeAxes = Axes.Both };
            background.Colour = new Color4(37, 48, 132, 255); Add(background);
            Add(labels = new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 13, Right = 10 }, Masking = true });
            labels.Add(title = Text(folder, 0, 12, 1, 20, true));
        }

        public LegacyMapRow(ISkin skin, BeatmapInfo map, bool set, int setCount, Action action)
        {
            this.map = map;
            activeText = skin.GetConfig<SkinCustomColourLookup, Color4>(new SkinCustomColourLookup("SongSelectActiveText"))?.Value ?? Color4.Black;
            inactiveText = skin.GetConfig<SkinCustomColourLookup, Color4>(new SkinCustomColourLookup("SongSelectInactiveText"))?.Value ?? Color4.White;
            isSet = set; Action = action; TooltipText = map.Metadata.Artist + " - " + map.Metadata.Title + " [" + map.DifficultyName + "]";
            Name = set ? "soms-selection-set-row" : "soms-selection-difficulty-row";
            RelativeSizeAxes = Axes.X; Width = 1.16f; Height = 55;
            background = SpriteFor(skin, "menu-button-background", FillMode.Stretch) ?? new Box { RelativeSizeAxes = Axes.Both };
            Add(background);
            var cover = new Container { Name = "soms-selection-cover", X = 3, Y = 2, Width = 69, Height = 51, Masking = true };
            cover.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(48, 41, 55, 255) });
            // Carousel snapshots deliberately omit Files. GetWorkingBeatmap re-fetches
            // those files from Realm; an empty snapshot is not a missing background.
            cover.Add(new DelayedLoadWrapper(() => new LegacyMapCover(map)
                { RelativeSizeAxes = Axes.Both, FillMode = FillMode.Fill, Anchor = Anchor.Centre, Origin = Anchor.Centre }, 80)
                { RelativeSizeAxes = Axes.Both });
            Add(cover);
            Add(labels = new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 80, Right = 4 }, Masking = true });
            labels.Add(title = Text(map.Metadata.Title, 0, 3, 1, 14, true));
            labels.Add(Text(map.Metadata.Artist + " // " + map.Metadata.Author.Username, 0, 18, 1, 10, true));
            if (set) labels.Add(Text($"{setCount} сложности", 0, 33, 1, 9, true));
            else
            {
                labels.Add(new TruncatingSpriteText
                {
                    Text = map.DifficultyName, Y = 29, RelativeSizeAxes = Axes.X,
                    Font = SomsLegacyFont.Font(10, bold: true), Shadow = true,
                });
                // Stable's ten-star strip retains fractional stars, with the exact SR in the tooltip.
                for (int i = 0; i < 10; i++)
                {
                    var star = SpriteFor(skin, "star");
                    if (star == null) star = new SpriteIcon { Icon = FontAwesome.Solid.Star, RelativeSizeAxes = Axes.Both };
                    var starContainer = new Container
                    {
                        Name = "soms-selection-star-" + i,
                        Position = new Vector2(i * 16, 42), Size = new Vector2(11),
                        Alpha = i < (int)map.StarRating ? 1 : i < map.StarRating ? Math.Clamp((float)map.StarRating - i, .2f, 1) : .15f,
                        Child = star,
                    };
                    stars.Add(starContainer);
                    labels.Add(starContainer);
                }
            }
            SetSelected(false);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            if (map == null || isSet || difficultyCache == null) return;
            difficultyCancellation = new CancellationTokenSource();
            difficulty = difficultyCache.GetBindableDifficulty(map, difficultyCancellation.Token, 150);
            difficulty.BindValueChanged(value =>
            {
                double rating = value.NewValue.Stars;
                for (int i = 0; i < stars.Count; i++)
                    stars[i].Alpha = i < (int)rating ? 1 : i < rating ? Math.Clamp((float)rating - i, .2f, 1) : .15f;
                TooltipText = $"{map.Metadata.Artist} - {map.Metadata.Title} [{map.DifficultyName}] · {rating:0.##}★";
            }, true);
        }

        protected override bool OnMouseDown(MouseDownEvent e)
        {
            if (e.Button == MouseButton.Right) { contextPress = e.ScreenSpaceMousePosition; return false; }
            return base.OnMouseDown(e);
        }
        protected override bool OnMouseMove(MouseMoveEvent e)
        {
            if (contextPress is { } press && (e.ScreenSpaceMousePosition - press).Length > 10) contextPress = null;
            return base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseUpEvent e)
        {
            // osu.Framework does not emit ClickEvent for the right mouse button.
            if (e.Button == MouseButton.Right)
            {
                bool clicked = contextPress is { } press && (e.ScreenSpaceMousePosition - press).Length <= 10
                    && Parent?.IsDragged != true && ReceivePositionalInputAt(e.ScreenSpaceMousePosition);
                contextPress = null;
                if (clicked) ContextAction?.Invoke();
            }
            base.OnMouseUp(e);
        }
        protected override bool OnClick(ClickEvent e)
        {
            return e.Button == MouseButton.Left && base.OnClick(e);
        }

        protected override void Dispose(bool isDisposing)
        {
            difficultyCancellation?.Cancel(); difficultyCancellation?.Dispose(); difficulty?.UnbindAll();
            base.Dispose(isDisposing);
        }

        public void SetSelected(bool value)
        {
            if (selected == value || isFolder) return;
            selected = value;
            var colour = value ? Color4.White : isSet ? new Color4(238, 75, 162, 255) : new Color4(0, 160, 225, 255);
            background.FadeColour(colour, 180);
            labels.FadeColour(value ? activeText : inactiveText, 180);
            title.FadeTo(value || isSet ? 1 : .45f, 180);
        }

        public void SetFolderTitle(string value) { if (isFolder) title.Text = value; }

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeTo(.86f, 100);
            return base.OnHover(e);
        }
        protected override void OnHoverLost(HoverLostEvent e) { background.FadeTo(1, 150); base.OnHoverLost(e); }
    }

    private sealed partial class LegacyMapCover : Sprite
    {
        private readonly BeatmapInfo map;
        public LegacyMapCover(BeatmapInfo map) { this.map = map; Name = "soms-selection-cover-image"; }

        [BackgroundDependencyLoader]
        private void load(BeatmapManager manager)
        {
            // GetPanelBackground crops to a very wide strip for native carousel panels.
            // These nearly square covers need the intact image from the shared cache.
            // Both the Realm lookup and texture decoding run in the delayed async load.
            Texture = manager.GetWorkingBeatmap(map).GetBackground();
        }
    }

    private sealed partial class LegacyButton : OsuClickableContainer
    {
        private readonly Drawable? hover;
        private readonly bool authoredHover;
        private readonly TruncatingSpriteText? caption;
        public void SetCaption(string text) { if (caption != null) caption.Text = text; }
        public LegacyButton(ISkin skin, string asset, string label, Action action, Color4? background = null, bool naturalSize = false, bool showCaption = false, float textSize = 11)
        {
            Action = action; TooltipText = label;
            var sprite = naturalSize ? naturalArt(skin, asset) : SomsLegacyOverlayButton.Art(skin, asset, FillMode.Stretch);
            // A transparent 1x1 asset deliberately suppresses default artwork and text.
            // Keep the button's hitbox separate from potentially screen-sized skin art.
            if (sprite == null)
                Add(new Box { RelativeSizeAxes = Axes.Both, Colour = background ?? new Color4(.18f, .17f, .23f, .94f) });
            if (sprite != null)
            {
                if (!naturalSize && background.HasValue) sprite.Colour = background.Value;
                Add(sprite);
            }
            if (naturalSize)
            {
                hover = naturalArt(skin, asset + "-over");
                authoredHover = hover != null;
            }
            if (hover == null && (!naturalSize || sprite == null))
                hover = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.White };
            if (hover != null) { hover.Alpha = 0; Add(hover); }
            if (label.Length > 0 && (sprite == null || showCaption)) Add(caption = new TruncatingSpriteText
            {
                Text = label, Anchor = Anchor.Centre, Origin = Anchor.Centre, MaxWidth = 70,
                Font = SomsLegacyFont.Font(textSize), Shadow = true,
            });
        }
        private static Drawable? naturalArt(ISkin skin, string asset)
        {
            var drawable = NaturalSpriteFor(skin, asset);
            if (drawable == null) return null;
            drawable.Name = "soms-selection-art-" + asset;
            drawable.Anchor = Anchor.BottomLeft;
            drawable.Origin = Anchor.BottomLeft;
            drawable.Scale = new Vector2(skin_scale);
            return drawable;
        }
        protected override bool OnHover(HoverEvent e) { hover?.FadeTo(authoredHover ? 1 : .15f, 80); return base.OnHover(e); }
        protected override void OnHoverLost(HoverLostEvent e) { hover?.FadeOut(100); base.OnHoverLost(e); }
        protected override void Update() { base.Update(); if (caption != null) caption.MaxWidth = Math.Max(1, DrawWidth * .94f); }
    }
}
