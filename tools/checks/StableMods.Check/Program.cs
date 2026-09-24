using System.Reflection;
using System.Runtime.Loader;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Input.States;
using osu.Game.Input.Bindings;
using osu.Game.Overlays.Mods;
using osu.Game.Overlays.Mods.Input;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.EnhancedAuth;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osuTK.Input;

internal static class Program
{
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<string> failures = new();
    private static int checks;

    public static int Main(string[] args)
    {
        string client = Path.GetFullPath(args[0]), plugin = Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = name.Name == "osu.Game.Rulesets.EnhancedAuth" ? plugin : Path.Combine(client, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        foreach (string assembly in new[] { "osu.Framework", "osu.Game.Resources", "osu.Game", "osu.Game.Rulesets.Osu" })
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(client, assembly + ".dll"));
        return run();
    }

    private static int run()
    {
        // No host, Realm, account or real profile is opened. Native ModStates and hotkey handlers
        // are exercised directly through the production classic-overlay keyboard entrypoints.
        typeof(GlobalConfigManager).GetField("instance", flags)!.SetValue(null, new EnhancedRulesetConfig { ApiUrl = "https://mods-check.invalid" });
        var ruleset = (Ruleset)Activator.CreateInstance(Assembly.Load("osu.Game.Rulesets.Osu").GetType("osu.Game.Rulesets.Osu.OsuRuleset", true)!)!;
        var all = ruleset.CreateAllMods().Select(mod => new ModState(mod)).ToArray();
        using var owner = new ModSelectOverlay();
        owner.Ruleset.Value = new RulesetInfo { OnlineID = 0 };
        owner.AvailableMods.Value = all.GroupBy(mod => mod.Mod.Type).ToDictionary(group => group.Key, group => (IReadOnlyList<ModState>)group.ToArray());
        owner.State.Value = Visibility.Visible;
        using var overlay = new SomsLegacyMods(owner);
        var preference = new Bindable<bool>(true);
        typeof(SomsLegacyComponent).GetField("preference", flags)!.SetValue(overlay, preference);
        var columns = owner.AvailableMods.Value.Select(group => new ModColumn(group.Key, false) { AvailableMods = group.Value }).ToArray();
        foreach (var column in columns)
            typeof(ModColumn).GetField("hotkeyHandler", flags)!.SetValue(column, new ClassicModHotkeyHandler(false));
        typeof(SomsLegacyMods).GetField("nativeColumns", flags)!.SetValue(overlay, columns);
        var hotkeys = typeof(EnhancedAuthRuleset).Assembly.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.SomsModHotkeys", true)!;
        var observerType = hotkeys.GetNestedType("BindingObserver", BindingFlags.NonPublic)!;
        using var observer = (IDisposable)Activator.CreateInstance(observerType, true)!;
        var observers = hotkeys.GetField("Observers", flags)!.GetValue(null)!;
        observers.GetType().GetMethod("Add")!.Invoke(observers, new object[] { owner, observer });
        var bindings = new Dictionary<int, KeyCombination>();
        observerType.GetField("Bindings", flags)!.SetValue(observer, new Dictionary<int, Dictionary<int, KeyCombination>> { [0] = bindings });
        var handler = typeof(SomsLegacyMods).GetMethod("OnKeyDown", flags)!;
        var cycle = typeof(SomsLegacyMods).GetMethod("cycle", flags)!;
        int ActionId(string acronym) => (int)hotkeys.GetMethod("ActionId", flags)!.Invoke(null, new object[] { acronym })!;
        ModState State(string acronym) => all.Single(mod => mod.Mod.Acronym == acronym);
        void Reset()
        {
            bindings.Clear();
            owner.SelectedMods.Disabled = false;
            owner.State.Value = Visibility.Visible;
            preference.Value = true;
            foreach (var mod in all) { mod.Active.Disabled = false; mod.Active.Value = false; mod.ValidForSelection.Value = true; mod.MatchingTextFilter.Value = true; }
            foreach (var column in columns) typeof(ModColumn).GetField("hotkeyHandler", flags)!.SetValue(column, new ClassicModHotkeyHandler(false));
        }
        InputState Input(Key key, bool ctrl = false)
        {
            var input = new InputState();
            input.Keyboard.Keys.SetPressed(key, true);
            if (ctrl) input.Keyboard.Keys.SetPressed(Key.LControl, true);
            return input;
        }
        bool Press(Key key, bool ctrl = false, bool repeat = false) => (bool)handler.Invoke(overlay, new object[] { new KeyDownEvent(Input(key, ctrl), key, repeat) })!;

        check("classic D cycles DT → NC → off", () =>
        {
            Reset();
            require(Press(Key.D) && State("DT").Active.Value, "D did not select DT");
            require(Press(Key.D) && State("NC").Active.Value && !State("DT").Active.Value, "D did not cycle to NC");
            require(Press(Key.D) && !State("NC").Active.Value, "D did not turn NC off");
        });
        check("mouse family cycles SD → PF → off", () =>
        {
            Reset();
            var family = new[] { State("SD"), State("PF") };
            cycle.Invoke(overlay, new object[] { family }); require(State("SD").Active.Value, "First click not SD");
            cycle.Invoke(overlay, new object[] { family }); require(State("PF").Active.Value && !State("SD").Active.Value, "Second click not PF");
            cycle.Invoke(overlay, new object[] { family }); require(!State("PF").Active.Value, "Third click did not clear");
        });
        check("custom Ctrl+D preserves native NC fallback and repeat guard", () =>
        {
            Reset(); bindings[ActionId("DT")] = new KeyCombination(new[] { InputKey.Control, InputKey.D });
            require(Press(Key.D, ctrl: true) && State("DT").Active.Value, "Custom Ctrl+D did not select DT");
            require(!Press(Key.D, ctrl: true, repeat: true) && State("DT").Active.Value, "Key repeat toggled DT");
            require(Press(Key.D, ctrl: true) && !State("DT").Active.Value, "Second Ctrl+D did not clear DT");
            require(Press(Key.D) && State("NC").Active.Value && !State("DT").Active.Value, "Default D lost NC fallback");
        });
        check("disabled first sequential mod does not shift S from second to third", () =>
        {
            Reset();
            var column = columns.Single(c => c.ModType == ModType.DifficultyIncrease);
            typeof(ModColumn).GetField("hotkeyHandler", flags)!.SetValue(column, SequentialModHotkeyHandler.Create(ModType.DifficultyIncrease));
            var states = column.AvailableMods.Where(mod => mod.Visible).ToArray();
            states[0].Active.Disabled = true;
            require(Press(Key.S) && states[1].Active.Value && !states[2].Active.Value, "A disabled entry shifted sequential key indices");
        });
        check("sequential key targeting a disabled mod is inert", () =>
        {
            Reset();
            var column = columns.Single(c => c.ModType == ModType.DifficultyIncrease);
            typeof(ModColumn).GetField("hotkeyHandler", flags)!.SetValue(column, SequentialModHotkeyHandler.Create(ModType.DifficultyIncrease));
            column.AvailableMods.First(mod => mod.Visible).Active.Disabled = true;
            require(Press(Key.A) && !all.Any(mod => mod.Active.Value), "Disabled sequential target activated a neighbour");
        });
        check("locked custom mod on 2 consumes the key and keeps overlay open", () =>
        {
            Reset(); bindings[ActionId("HD")] = new KeyCombination(InputKey.Number2); State("HD").Active.Disabled = true;
            require(Press(Key.Number2) && owner.State.Value == Visibility.Visible && !State("HD").Active.Value, "Locked mod key became close shortcut");
        });
        check("forbidden custom mod on 2 never becomes close shortcut", () =>
        {
            Reset(); bindings[ActionId("HD")] = new KeyCombination(InputKey.Number2); State("HD").ValidForSelection.Value = false;
            require(Press(Key.Number2) && owner.State.Value == Visibility.Visible && !State("HD").Active.Value, "Forbidden mod key became close shortcut");
        });
        check("readonly selection with custom 2 stays readonly and open", () =>
        {
            Reset(); bindings[ActionId("HD")] = new KeyCombination(InputKey.Number2); owner.SelectedMods.Disabled = true;
            require(Press(Key.Number2) && owner.State.Value == Visibility.Visible && !all.Any(mod => mod.Active.Value), "Readonly custom key closed overlay or selected a mod");
        });
        check("custom Escape precedes global Back", () =>
        {
            Reset(); bindings[ActionId("HD")] = new KeyCombination(InputKey.Escape);
            require(overlay.OnPressed(new KeyBindingPressEvent<GlobalAction>(Input(Key.Escape), GlobalAction.Back, false)) && State("HD").Active.Value,
                "Custom Escape was not handled before Back");
        });
        check("reset preserves locked mods", () =>
        {
            Reset(); State("HD").Active.Value = true; State("HR").Active.Value = true; State("HD").Active.Disabled = true;
            require(Press(Key.Number1) && State("HD").Active.Value && !State("HR").Active.Value, "Reset modified a locked mod or left an editable mod");
        });
        check("inactive and hidden legacy layer cannot handle keys", () =>
        {
            Reset(); preference.Value = false; require(!Press(Key.D), "Disabled legacy layer accepted D");
            preference.Value = true; owner.State.Value = Visibility.Hidden; require(!Press(Key.D), "Hidden overlay accepted D");
            require(!all.Any(mod => mod.Active.Value), "Inactive layer changed selected mods");
        });

        foreach (var column in columns) column.Dispose();
        Console.WriteLine($"{checks - failures.Count}/{checks} direct classic-mod keyboard and permission checks passed");
        foreach (string failure in failures) Console.Error.WriteLine(failure);
        return failures.Count == 0 ? 0 : 1;
    }

    private static void check(string name, Action action)
    {
        checks++;
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures.Add("FAIL " + name + ": " + (e.InnerException ?? e).Message); }
    }
    private static void require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
