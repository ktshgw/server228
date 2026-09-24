#nullable enable
using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input;
using osu.Framework.Input.Events;
using osu.Game.Beatmaps;
using osu.Game.Collections;
using osu.Game.Database;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public sealed partial class SomsLegacySongSelect
{
    private void showCollections(BeatmapInfo map)
    {
        if (scene == null || Skin == null) return;
        closeMenu(ref options);
        optionActions.Clear();
        options = new LegacyCollectionsMenu(Skin, map, () => closeMenu(ref options));
        scene.Add(options);
        options.FadeInFromZero(150);
    }

    /// <summary>Edits the native Realm collections. Set operations resolve all installed difficulties, regardless of the current filter.</summary>
    private sealed partial class LegacyCollectionsMenu : Surface
    {
        private readonly ISkin skin;
        private readonly Guid mapId;
        private readonly string mapHash;
        private readonly OsuTextBox nameInput;
        private readonly LegacyButton saveName, delete;
        private readonly FillFlowContainer list;
        private readonly OsuScrollContainer scroll;
        private readonly TruncatingSpriteText message;
        private Guid? selected, renaming;
        private bool deleteArmed;
        private IDisposable? subscription;
        [Resolved] private RealmAccess realm { get; set; } = null!;

        public LegacyCollectionsMenu(ISkin skin, BeatmapInfo map, Action close)
        {
            this.skin = skin; mapId = map.ID; mapHash = map.MD5Hash;
            Name = "soms-selection-collections-menu";
            RelativeSizeAxes = Axes.Both; Depth = -10;
            Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(0, 0, 0, .88f) });
            Add(Text("Коллекции", 4, 4, .98f, 24, true));
            var panel = new Container { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Y = 36, Size = new Vector2(455, 395) };
            Add(panel);
            panel.Add(Text(map.Metadata.Title + " [" + map.DifficultyName + "]", 0, 0, 455, 12));
            panel.Add(nameInput = new OsuTextBox
            {
                Name = "soms-collection-name", Y = 23, Width = 345, Height = 22, CornerRadius = 0,
                PlaceholderText = "Название новой коллекции", LengthLimit = 128,
            });
            nameInput.OnCommit += (_, _) => CommitName();
            panel.Add(saveName = button("create", "Создать", 350, 23, 105, () => CommitName(), new Color4(113, 163, 10, 255)));
            panel.Add(scroll = new OsuScrollContainer
            {
                Name = "soms-collection-scroll", Y = 53, Width = 455, Height = 263,
                ScrollbarOverlapsContent = false,
                Child = list = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical, Spacing = new Vector2(0, 3),
                },
            });
            panel.Add(message = Text("+Set / −Set: весь мапсет    + / −: выбранная сложность", 0, 323, 455, 11));
            panel.Add(delete = button("delete", "Удалить выбранную коллекцию", 0, 343, 455, deleteSelected, new Color4(165, 30, 10, 255), 22));
            panel.Add(button("close", "Отмена", 0, 380, 455, close, new Color4(95, 95, 95, 255), 24));
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            subscription = realm.RegisterForNotifications(r => r.All<BeatmapCollection>().OrderBy(c => c.Name),
                (_, _) => Scheduler.AddOnce(refresh));
            refresh();
        }

        private string[] setHashes() => realm.Realm.Find<BeatmapInfo>(mapId)?.BeatmapSet?.Beatmaps
            .Select(b => b.MD5Hash).Where(h => !string.IsNullOrEmpty(h)).Distinct().ToArray() ?? new[] { mapHash };

        private void refresh()
        {
            if (!IsLoaded) return;
            list.Clear();
            var hashes = setHashes();
            var collections = realm.Realm.All<BeatmapCollection>().OrderBy(c => c.Name).ToArray();
            if (selected.HasValue && !collections.Any(c => c.ID == selected)) selected = null;
            foreach (var collection in collections)
            {
                Guid id = collection.ID;
                bool hasMap = collection.BeatmapMD5Hashes.Contains(mapHash);
                int setCount = hashes.Count(h => collection.BeatmapMD5Hashes.Contains(h));
                var row = new Container
                {
                    Name = "soms-collection-row-" + id, RelativeSizeAxes = Axes.X, Height = 17,
                    Masking = true, BorderThickness = .5f, BorderColour = Color4.White,
                };
                row.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = selected == id ? new Color4(40, 65, 78, 235) : Color4.Black });
                row.Add(button("select-" + id, collection.Name + $" ({collection.BeatmapMD5Hashes.Count})", 2, 0, 270,
                    () => { selected = id; disarmDelete(); refresh(); }, Color4.Transparent, 12));
                row.Add(button("rename-" + id, "Переим", 274, 1, 47, () =>
                {
                    selected = renaming = id; disarmDelete();
                    nameInput.Text = collection.Name; GetContainingFocusManager().ChangeFocus(nameInput);
                    saveName.TooltipText = "Сохранить новое название";
                    saveName.SetCaption("Сохранить");
                    message.Text = "Введите новое название и нажмите Enter или кнопку справа.";
                    refresh();
                }, new Color4(184, 131, 0, 255), 10));
                row.Add(membership("add-set-", "+Set", 324, 33, true, true, setCount < hashes.Length));
                row.Add(membership("add-map-", "+", 360, 20, true, false, !hasMap));
                row.Add(membership("remove-set-", "−Set", 383, 33, false, true, setCount > 0));
                row.Add(membership("remove-map-", "−", 419, 20, false, false, hasMap));
                list.Add(row);

                LegacyButton membership(string key, string label, float x, float width, bool add, bool wholeSet, bool enabled)
                {
                    var control = button(key + id, label, x, 1, width,
                        () => { if (enabled) changeMembership(id, add, wholeSet); },
                        add ? new Color4(80, 150, 0, 255) : new Color4(171, 65, 0, 255), 11);
                    control.Alpha = enabled ? 1 : .3f;
                    control.TooltipText = (add ? "Добавить " : "Убрать ") + (wholeSet ? "весь мапсет" : "эту сложность");
                    return control;
                }
            }
            if (collections.Length == 0) list.Add(Text("Коллекций пока нет. Создайте первую выше.", 0, 10, 440, 14));
            delete.Alpha = selected.HasValue ? 1 : .4f;
        }

        private void changeMembership(Guid id, bool add, bool wholeSet)
        {
            string[] hashes = wholeSet ? setHashes() : new[] { mapHash };
            realm.Write(r =>
            {
                var collection = r.Find<BeatmapCollection>(id);
                if (collection == null) return;
                foreach (string hash in hashes.Where(h => !string.IsNullOrEmpty(h)))
                {
                    if (add) { if (!collection.BeatmapMD5Hashes.Contains(hash)) collection.BeatmapMD5Hashes.Add(hash); }
                    else while (collection.BeatmapMD5Hashes.Remove(hash)) { }
                }
                collection.LastModified = DateTimeOffset.UtcNow;
            });
            selected = id; disarmDelete();
            message.Text = (wholeSet ? "Мапсет" : "Сложность") + (add ? " добавлен в коллекцию." : " удалён из коллекции.");
            refresh();
        }

        public void CommitName()
        {
            if (!nameInput.HasFocus && string.IsNullOrWhiteSpace(nameInput.Text)) return;
            string value = nameInput.Text.Trim();
            if (value.Length == 0) { message.Text = "Введите название коллекции."; return; }
            if (realm.Realm.All<BeatmapCollection>().AsEnumerable().Any(c => c.ID != renaming && string.Equals(c.Name, value, StringComparison.OrdinalIgnoreCase)))
            { message.Text = "Коллекция с таким названием уже существует."; return; }
            bool rename = renaming.HasValue;
            realm.Write(r =>
            {
                if (renaming.HasValue)
                {
                    var collection = r.Find<BeatmapCollection>(renaming.Value);
                    if (collection != null) { collection.Name = value; collection.LastModified = DateTimeOffset.UtcNow; }
                }
                else { var collection = new BeatmapCollection(value); r.Add(collection); selected = collection.ID; }
            });
            renaming = null; nameInput.Text = ""; disarmDelete();
            saveName.TooltipText = "Создать коллекцию";
            saveName.SetCaption("Создать");
            message.Text = rename ? "Коллекция переименована." : "Коллекция создана. Добавьте сложность кнопкой + или весь мапсет кнопкой +Set.";
            refresh();
            ScheduleAfterChildren(() =>
            {
                var row = list.Children.FirstOrDefault(d => d.Name == "soms-collection-row-" + selected);
                if (row != null) scroll.ScrollIntoView(row);
            });
        }

        private void disarmDelete() { deleteArmed = false; delete.TooltipText = "Удалить выбранную коллекцию"; }

        private void deleteSelected()
        {
            if (!selected.HasValue) return;
            if (!deleteArmed)
            {
                deleteArmed = true;
                string name = realm.Realm.Find<BeatmapCollection>(selected.Value)?.Name ?? "";
                message.Text = $"Удалить «{name}»? Нажмите кнопку удаления ещё раз. Карты останутся.";
                return;
            }
            realm.Write(r => { if (r.Find<BeatmapCollection>(selected.Value) is { } collection) r.Remove(collection); });
            selected = renaming = null; nameInput.Text = ""; disarmDelete(); message.Text = "Коллекция удалена."; refresh();
        }

        private LegacyButton button(string key, string label, float x, float y, float width, Action action, Color4 colour, float fontSize = 12) =>
            new(skin, "", label, action, colour, textSize: fontSize)
            { Name = "soms-collection-" + key, X = x, Y = y, Width = width, Height = fontSize >= 22 ? 32 : key == "create" ? 22 : 15 };

        protected override bool OnScroll(ScrollEvent e) => true;
        protected override void Dispose(bool isDisposing) { subscription?.Dispose(); base.Dispose(isDisposing); }
    }
}
