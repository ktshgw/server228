using System.Reflection;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Input.States;
using osu.Game.Database;
using osu.Game.Graphics.Cursor;
using osu.Game.Input.Bindings;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays.Mods;
using osu.Game.Overlays.Mods.Input;
using osu.Game.Overlays.Settings.Sections.Input;
using osu.Game.Rulesets.EnhancedAuth;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.Mods;
using osuTK;
using osuTK.Input;

internal sealed partial class IntegrationGame
{
    private SomsModHotkeysSection? modKeys;
    private float? cursorSize;
    private MenuCursorContainer.Cursor cursorProbe = null!;
    private static readonly Type hotkeys = typeof(EnhancedAuthRuleset).Assembly.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.SomsModHotkeys")!;
    private static int ModAction(string acronym) => (int)hotkeys.GetMethod("ActionId", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { acronym })!;

    private void InitPolishChecks()
    {
        // Simulate an upgrade from the previous global bindings before settings are opened.
        member<RealmAccess>(this, "realm")!.Write(r => r.Add(new RealmKeyBinding(ModAction("DT"),
            new KeyCombination(new[] { InputKey.Control, InputKey.D }), "soms-mod-hotkeys")));
        Add(new ScaleSettingsHost { Alpha = 0, AlwaysPresent = true, Child = modKeys = new SomsModHotkeysSection() });
        Add(new osu.Game.Graphics.Containers.ScalingContainer
        {
            RelativeSizeAxes = Axes.Both,
            Child = cursorProbe = new MenuCursorContainer.Cursor { Alpha = 0, AlwaysPresent = true },
        });
        CheckModDispatch();
        var profiles = typeof(EnhancedAuthRuleset).Assembly.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.SomsMapperProfiles")!;
        var author = new APIUser { Id = 42, Username = "Official mapper" };
        var local = new APIUser { Id = 42, Username = "SOMS player" };
        profiles.GetMethod("Mark", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { author });
        bool Marked(APIUser user) => (bool)profiles.GetMethod("IsOfficial", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { user })!;
        require(Marked(author) && !Marked(local) && author.AvatarUrl == "https://a.ppy.sh/42", "Mapper origin must not leak to SOMS users with the same ID");
        var unknown = new osu.Game.Models.RealmUser { Username = "Imported mapper" };
        var resolved = (APIUser)profiles.GetMethod("FromMetadata", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { unknown })!;
        require(unknown.OnlineID == 1 && resolved.Id == 0 && resolved.Username == unknown.Username && Marked(resolved), "Imported creator sentinel ID must resolve by username without mutating Realm");
        Console.WriteLine("PASS official mapper identity is scoped to its object");
    }

    private void CheckPolishSettings()
    {
        require(modKeys?.IsLoaded == true, "Native mod hotkey section must load");
        var rows = descendants(modKeys!).OfType<KeyBindingRow>().ToArray();
        int expectedRows = RulesetStore.AvailableRulesets.Where(r => r.Available && r.OnlineID is >= 0 and < 4)
            .Sum(r => r.CreateInstance().CreateAllMods().Where(mod => mod.Acronym.Length is > 0 and <= 3).DistinctBy(mod => mod.Acronym).Count());
        require(rows.Length == expectedRows, $"Expected {expectedRows} mode/mod rows, received {rows.Length}");
        var groups = descendants(modKeys!).OfType<KeyBindingsSubsection>().ToArray();
        require(groups.Length >= 20, "Rulesets must be subdivided by mod category");
        foreach (var group in groups)
        {
            var groupRows = descendants(group).OfType<KeyBindingRow>().ToArray();
            require(groupRows.Select(row => row.Action).Distinct().Count() == groupRows.Length, "No duplicate rows within a category");
        }
        var realm = member<RealmAccess>(this, "realm")!;
        const string scope = "soms-mod-hotkeys";
        var records = realm.Run(r => r.All<RealmKeyBinding>().Where(b => b.RulesetName == scope).ToArray());
        require(records.Count(record => record.Variant != null) == rows.Length, "One persisted binding per mode/mod");
        require(records.GroupBy(record => (record.Variant, record.ActionInt)).All(group => group.Count() == 1), "No duplicate persisted actions");
        int dt = ModAction("DT");
        require(records.Count(b => b.ActionInt == dt && b.KeyCombination.Keys.Contains(InputKey.Control)) == 5, "Old Ctrl+D migrated into all four modes and retained as backup");
        realm.Write(r => r.All<RealmKeyBinding>().Single(b => b.RulesetName == scope && b.ActionInt == dt && b.Variant == 1).KeyCombinationString = new KeyCombination(InputKey.None).ToString());
        require(realm.Run(r => r.All<RealmKeyBinding>().Single(b => b.RulesetName == scope && b.ActionInt == dt && b.Variant == 0).KeyCombinationString) == new KeyCombination(new[] { InputKey.Control, InputKey.D }).ToString(), "Changing taiko must preserve osu! Ctrl+D");
        Console.WriteLine($"PASS {rows.Length} real native mod binding rows, captions and persisted Ctrl+D");
        using var panel = new KeyBindingPanel();
        var addSections = typeof(SomsModHotkeySettingsPatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (int i = 0; i < 20; i++) addSections.Invoke(null, new object[] { panel });
        var sections = member<List<osu.Game.Overlays.Settings.SettingsSection>>(panel, "loadableSections")!;
        require(sections.OfType<SomsModHotkeysSection>().Count() == 4, "Repeated setup must leave exactly one section per ruleset");
        Console.WriteLine("PASS four mode sections remain unique after 20 repeated initializations");
    }

    private void CheckModDispatch()
    {
        var dispatch = typeof(SomsModKeyPressPatch).GetMethod("Handle", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Press(Key key, ModState[] mods, IModHotkeyHandler native, Dictionary<int, KeyCombination> bindings, bool ctrl = false, bool repeat = false)
        {
            var state = new InputState();
            state.Keyboard.Keys.SetPressed(key, true);
            if (ctrl) state.Keyboard.Keys.SetPressed(Key.LControl, true);
            return (bool)dispatch.Invoke(null, new object[] { new KeyDownEvent(state, key, repeat), mods, mods, native, bindings })!;
        }
        foreach (var ruleset in RulesetStore.AvailableRulesets.Where(r => r.Available && r.OnlineID is >= 0 and <= 3))
        {
            var all = ruleset.CreateInstance().CreateAllMods().Select(mod => new ModState(mod)).ToArray();
            var dt = all.First(mod => mod.Mod.Acronym == "DT");
            var nc = all.First(mod => mod.Mod.Acronym == "NC");
            var hd = all.FirstOrDefault(mod => mod.Mod.Acronym == "HD");
            var custom = new Dictionary<int, KeyCombination> { [ModAction("DT")] = new(new[] { InputKey.Control, InputKey.D }) };
            var classic = new ClassicModHotkeyHandler(false);
            require(Press(Key.D, all, classic, custom, ctrl: true) && dt.Active.Value, "Custom Ctrl+D activates DT");
            require(!Press(Key.D, all, classic, custom, ctrl: true, repeat: true) && dt.Active.Value, "Held hotkey must not toggle again");
            Press(Key.D, all, classic, custom, ctrl: true);
            require(!dt.Active.Value, "Second custom press disables DT");
            Press(Key.D, all, classic, custom);
            require(nc.Active.Value && !dt.Active.Value, "Standard D retains NC without selecting overridden DT");
            if (hd != null)
            {
                var reference = ruleset.CreateInstance().CreateAllMods().Select(mod => new ModState(mod)).ToArray();
                Press(Key.F, reference, classic, new());
                Press(Key.F, all, classic, custom);
                require(reference.Where(mod => mod.Active.Value).All(expected => all.Any(mod => mod.Mod.Acronym == expected.Mod.Acronym && mod.Active.Value)), "Unassigned F retains the native ruleset-specific cycle");
            }
            dt.Active.Value = false;
            dt.MatchingTextFilter.Value = false;
            require(!Press(Key.D, all, classic, custom, ctrl: true) && !dt.Active.Value, "Filtered-out mod cannot activate");
            dt.MatchingTextFilter.Value = true;
            custom.Clear();
            nc.Active.Value = false;
            require(Press(Key.D, all, classic, custom) && dt.Active.Value, "Clearing override restores native D cycle");
        }
        var osu = RulesetStore.GetRuleset(0)!.CreateInstance();
        var increasing = osu.CreateAllMods().Where(mod => mod.Type == ModType.DifficultyIncrease).Select(mod => new ModState(mod)).ToArray();
        var bindings = new Dictionary<int, KeyCombination> { [ModAction(increasing[0].Mod.Acronym)] = new(InputKey.F8) };
        require(Press(Key.S, increasing, SequentialModHotkeyHandler.Create(ModType.DifficultyIncrease), bindings) && increasing[1].Active.Value, "Sequential S must retain its original index after overriding the first mod");
        Console.WriteLine("PASS custom combinations, repeat/filter guards and native fallback in all four rulesets; sequential indices preserved");
    }

    private void CheckCursorCompensation()
    {
        var cursor = cursorProbe;
        require(cursor.IsLoaded, "Actual menu cursor graphics must load");
        var compensation = cursor.Children.Single(c => c.GetType().Name == "CursorVisualScale");
        var pointA = compensation.ToScreenSpace(Vector2.Zero);
        var pointB = compensation.ToScreenSpace(new Vector2(100, 0));
        float width = (pointB - pointA).Length / cursor.Scale.X;
        if (cursorSize == null) cursorSize = width;
        require(Math.Abs(width - cursorSize.Value) < 0.02f, $"Cursor changed physical size with split UI scaling: {width} vs {cursorSize}");
    }

    private bool CheckLiveModOverlay(ModSelectOverlay overlay)
    {
        var observers = hotkeys.GetField("Observers", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object?[] args = { overlay, null };
        if (!(bool)observers.GetType().GetMethod("TryGetValue")!.Invoke(observers, args)!) return false;
        var observer = args[1]!;
        var forRuleset = observer.GetType().GetMethod("ForRuleset", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var bindings = (Dictionary<int, KeyCombination>)forRuleset.Invoke(observer, new object[] { 0 })!;
        if (!bindings.ContainsKey(ModAction("DT"))) return false;
        require(!((Dictionary<int, KeyCombination>)forRuleset.Invoke(observer, new object[] { 1 })!).ContainsKey(ModAction("DT")), "Cleared taiko assignment must not revive the global fallback");
        var column = descendants(overlay).OfType<ModColumn>().First();
        var dt = overlay.AvailableMods.Value.Values.SelectMany(mods => mods).First(mod => mod.Mod.Acronym == "DT");
        bool initial = dt.Active.Value;
        var input = new InputState();
        input.Keyboard.Keys.SetPressed(Key.LControl, true);
        input.Keyboard.Keys.SetPressed(Key.D, true);
        var handler = typeof(ModColumn).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Press() => (bool)handler.Invoke(column, new object[] { new KeyDownEvent(input, Key.D) })!;
        require(Press() && dt.Active.Value != initial, "Actual F1 column must use the saved custom binding across columns");
        require(Press() && dt.Active.Value == initial, "Second actual F1 input must restore the selected mod");
        Console.WriteLine("PASS saved Ctrl+D through actual ModColumn and live Realm subscription");
        return true;
    }
}
