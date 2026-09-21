using System.Reflection;
using osu.Framework.Input.Events;
using osu.Framework.Input.States;
using osu.Game.Overlays.Toolbar;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.Mods;
using osuTK.Input;

internal sealed partial class MarathonGame
{
    private void HotkeyChecks()
    {
        var selector = ChildrenOf(this).OfType<ToolbarRulesetSelector>().Single(s => s.IsLoaded);
        var handler = typeof(ToolbarRulesetSelector).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Press(int number, bool control = true, bool repeat = false, bool right = false)
        {
            var key = Key.Number1 + number - 1;
            var state = new InputState();
            state.Keyboard.Keys.SetPressed(key, true);
            if (control) state.Keyboard.Keys.SetPressed(right ? Key.RControl : Key.LControl, true);
            return (bool)handler.Invoke(selector, new object[] { new KeyDownEvent(state, key, repeat) })!;
        }

        var names = new[] { "osu", "taiko", "fruits", "mania", "osurx", "osuap" };
        Require(selector.Items.Select(r => r.ShortName).SequenceEqual(names), "six displayed tabs in shortcut order");
        Console.WriteLine("Installed rulesets: " + string.Join(",", RulesetStore.AvailableRulesets.Select(r => r.ShortName)));
        for (int number = 1; number <= 6; number++)
        {
            Require(Press(number), "Ctrl+number handled");
            Require(selector.Current.Value.ShortName == names[number - 1], "Ctrl+" + number + " selects displayed tab");
            Require(Ruleset.Value.OnlineID == (number <= 4 ? number - 1 : 0), "native engine remains valid");
        }
        Press(1);
        SelectedMods.Value = Ruleset.Value.CreateInstance().CreateAllMods().Where(m => m.Acronym is "HD" or "DT").ToArray();
        for (int iteration = 0; iteration < 20; iteration++)
        foreach (int number in new[] { 5, 5, 6, 6 })
        {
            Press(number, right: number == 6);
            Require(SomsAssistModes.Presented.Value.ShortName == names[number - 1], "rapid repeated switch stays consistent");
            Require(Ruleset.Value.OnlineID == 0, "assist keeps osu engine");
            Require(SelectedMods.Value.Select(m => m.Acronym).ToHashSet().SetEquals(new[] { "HD", "DT", number == 5 ? "RX" : "AP" }), "compatible mods preserved");
        }
        Press(1);
        Require(SelectedMods.Value.Select(m => m.Acronym).ToHashSet().SetEquals(new[] { "HD", "DT" }), "Ctrl+1 removes only assist");
        var unchanged = SelectedMods.Value;
        Press(5, repeat: true);
        Press(6, control: false);
        foreach (int number in new[] { 7, 8, 9 }) Press(number);
        Require(ReferenceEquals(SelectedMods.Value, unchanged) && selector.Current.Value.ShortName == "osu", "repeat, plain number and missing tabs are harmless");
        selector.Current.Disabled = true;
        try
        {
            Press(5); Press(6);
            Require(selector.Current.Value.ShortName == "osu", "disabled selector ignores shortcuts");
        }
        finally { selector.Current.Disabled = false; }
        SelectedMods.Value = Array.Empty<Mod>();
        Console.WriteLine("PASS Ctrl+1..6, 80 repeated assist presses, both Control keys, HD/DT, repeat, absent tabs and disabled state");
    }
}
