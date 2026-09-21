using System.Reflection;
using System.Runtime.Loader;

string clientPath = Path.GetFullPath(args[0]);
string pluginPath = Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(clientPath, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Game.dll"));
var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
var configType = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Configuration.EnhancedRulesetConfig", true)!;
var manager = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Configuration.GlobalConfigManager", true)!;
var configField = manager.GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)!;
var config = Activator.CreateInstance(configType)!;
configField.SetValue(null, config);

var harmonyType = plugin.GetType("HarmonyLib.Harmony", true)!;
var harmony = Activator.CreateInstance(harmonyType, "soms.rankedmods.check")!;
var patchType = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.RankedModsPatch", true)!;
var processor = harmonyType.GetMethod("CreateClassProcessor")!.Invoke(harmony, new object[] { patchType })!;
processor.GetType().GetMethod("Patch")!.Invoke(processor, null);

int checks = 0;
foreach (string ruleset in new[] { "Osu", "Taiko", "Catch", "Mania" })
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(
        Path.Combine(clientPath, "osu.Game.Rulesets." + ruleset + ".dll"));
    foreach (string modName in new[] { "DoubleTime", "Nightcore", "DifficultyAdjust", "Classic", "Autoplay", "HalfTime" })
    {
        var type = assembly.GetType($"osu.Game.Rulesets.{ruleset}.Mods.{ruleset}Mod{modName}", true)!;
        var mod = Activator.CreateInstance(type)!;
        var speedProperty = type.GetProperty("SpeedChange");
        if (speedProperty != null)
        {
            var speed = speedProperty.GetValue(mod)!;
            speed.GetType().GetProperty("Value")!.SetValue(speed, modName == "HalfTime" ? 0.8 : 1.3);
        }
        else if (modName == "DifficultyAdjust")
        {
            var od = type.GetProperty("OverallDifficulty")!.GetValue(mod)!;
            od.GetType().GetProperty("Value")!.SetValue(od, (float?)9);
        }
        var ranked = type.GetProperty("Ranked")!;
        bool target = modName is "DoubleTime" or "Nightcore" || modName == "Classic" && ruleset == "Osu";
        foreach (var (url, nonG0V0, expected) in new[] {
            ("https://osu.ppy.sh", false, false),
            ("https://soms.invalid", false, target),
            ("https://soms.invalid", true, false),
            ("https://dev.ppy.sh", false, false),
        })
        {
            configType.GetProperty("ApiUrl")!.SetValue(config, url);
            configType.GetProperty("NonG0V0Server")!.SetValue(config, nonG0V0);
            bool actual = (bool)ranked.GetValue(mod)!;
            if (actual != expected)
                throw new Exception($"{ruleset}/{modName} {url} nonG0V0={nonG0V0}: ranked={actual}, expected={expected}");
            checks++;
        }
    }
}
Console.WriteLine($"PASS: {checks} runtime ranked-mod checks against installed lazer assemblies.");

// Verify Harmony's signatures against the installed client, not just the NuGet
// reference assembly used to build the injection.
foreach (string name in new[]
{
    "SomsSkinDefaultBindingsPatch", "SomsSkinActionDescriptionPatch", "SomsSkinNativeBindingConflictsPatch", "SomsSkinHotkeySettingsPatch", "SomsSkinSelectionPatch",
    "SomsSkinElementSettingsPatch", "SomsSkinElementVisibilityPatch", "SomsLeaderboardCollapsePatch", "SomsLeaderboardPreferenceBindingPatch",
})
{
    var type = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Patches." + name, true)!;
    var classProcessor = harmonyType.GetMethod("CreateClassProcessor")!.Invoke(harmony, new object[] { type })!;
    classProcessor.GetType().GetMethod("Patch")!.Invoke(classProcessor, null);
}

var containerType = game.GetType("osu.Game.Input.Bindings.GlobalActionContainer", true)!;
var container = Activator.CreateInstance(containerType, new object?[] { null })!;
var defaultsProperty = containerType.GetProperty("DefaultKeyBindings")!;
var hotkeysType = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.SomsSkinHotkeys", true)!;
var isSkinAction = hotkeysType.GetMethod("IsSkinAction")!;
foreach (var (url, nonG0V0, expected) in new[]
{
    ("https://osu.ppy.sh", false, 0), ("https://soms.invalid", false, 5), ("https://soms.invalid", true, 0),
})
{
    configType.GetProperty("ApiUrl")!.SetValue(config, url);
    configType.GetProperty("NonG0V0Server")!.SetValue(config, nonG0V0);
    var bindings = ((System.Collections.IEnumerable)defaultsProperty.GetValue(container)!).Cast<object>().ToArray();
    int count = bindings.Count(binding => (bool)isSkinAction.Invoke(null,
        new object[] { Convert.ToInt32(binding.GetType().GetProperty("Action")!.GetValue(binding)) })!);
    if (count != expected)
        throw new Exception($"{url} nonG0V0={nonG0V0}: registered skin slots={count}, expected={expected}");
}

// Test preferences through a fresh manager, including skin UUIDs and the global
// leaderboard override. Never read or write the user's actual client settings.
var preferencesType = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Configuration.SomsClientPreferences", true)!;
var skinSectionType = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.UI.SomsSkinHotkeysSection", true)!;
var skinSection = Activator.CreateInstance(skinSectionType)!;
var sections = ((System.Collections.IEnumerable)skinSectionType.GetProperty("Children")!.GetValue(skinSection)!).Cast<object>().ToArray();
if (sections.Length != 6)
    throw new Exception("Skin shortcuts must have five slots and one leaderboard subsection.");
var slotActions = new List<int>();
foreach (var section in sections)
{
    var defaults = ((System.Collections.IEnumerable)section.GetType().GetProperty("Defaults", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(section)!).Cast<object>().ToArray();
    if (defaults.Length != 1)
        throw new Exception("Each skin slot must show only its own binding row.");
    slotActions.Add(Convert.ToInt32(defaults[0].GetType().GetProperty("Action")!.GetValue(defaults[0])));
}
if (slotActions.Distinct().Count() != 6)
    throw new Exception("Skin slots and leaderboard shortcut must have distinct persisted action IDs.");
var resolveExpanded = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.SomsLeaderboardCollapsePatch", true)!.GetMethod("ResolveExpanded")!;
foreach (bool preference in new[] { false, true })
foreach (bool skinExpanded in new[] { false, true })
{
    bool actual = (bool)resolveExpanded.Invoke(null, new object[] { preference, skinExpanded })!;
    bool expected = !preference || skinExpanded;
    if (actual != expected)
        throw new Exception($"Leaderboard preference {preference}: skin={skinExpanded}");
}
string preferencesPath = Path.Combine(Path.GetTempPath(), "soms-preferences-check-" + Guid.NewGuid() + ".json");
try
{
    var preferences = Activator.CreateInstance(preferencesType, preferencesPath)!;
    var collapse = preferencesType.GetField("UseSkinLeaderboardCollapse")!.GetValue(preferences)!;
    var collapseValue = collapse.GetType().GetProperty("Value")!;
    string[] toggles = { "UseSkinLeaderboardCollapse", "ShowSliderEndMiss", "ShowSliderFollowCircle" };
    foreach (string toggle in toggles)
    {
        var bindable = preferencesType.GetField(toggle)!.GetValue(preferences)!;
        if (!(bool)collapseValue.GetValue(bindable)!)
            throw new Exception(toggle + " must be enabled by default");
        collapseValue.SetValue(bindable, false);
    }
    var slots = (Array)preferencesType.GetField("SkinSlots")!.GetValue(preferences)!;
    Guid selectedSkin = Guid.NewGuid();
    slots.GetValue(3)!.GetType().GetProperty("Value")!.SetValue(slots.GetValue(3), selectedSkin);

    var restored = Activator.CreateInstance(preferencesType, preferencesPath)!;
    var restoredSlots = (Array)preferencesType.GetField("SkinSlots")!.GetValue(restored)!;
    if (toggles.Any(toggle => (bool)collapseValue.GetValue(preferencesType.GetField(toggle)!.GetValue(restored))!)
        || (Guid)restoredSlots.GetValue(3)!.GetType().GetProperty("Value")!.GetValue(restoredSlots.GetValue(3))! != selectedSkin)
        throw new Exception("SOMS! client preferences did not persist across manager instances.");

    foreach (int legacy in new[] { 0, 1, 2, 999 })
    {
        File.WriteAllText(preferencesPath, "{\"LeaderboardCollapse\":" + legacy + ",\"SkinSlots\":null}");
        var migrated = Activator.CreateInstance(preferencesType, preferencesPath)!;
        if ((bool)collapseValue.GetValue(preferencesType.GetField("UseSkinLeaderboardCollapse")!.GetValue(migrated))! != (legacy != 2)
            || !(bool)collapseValue.GetValue(preferencesType.GetField("ShowSliderEndMiss")!.GetValue(migrated))!
            || !(bool)collapseValue.GetValue(preferencesType.GetField("ShowSliderFollowCircle")!.GetValue(migrated))!)
            throw new Exception("Legacy preferences migration failed for " + legacy);
    }
    File.WriteAllText(preferencesPath, "{\"LeaderboardCollapse\":2,\"UseSkinLeaderboardCollapse\":true}");
    var modern = Activator.CreateInstance(preferencesType, preferencesPath)!;
    if (!(bool)collapseValue.GetValue(preferencesType.GetField("UseSkinLeaderboardCollapse")!.GetValue(modern))!)
        throw new Exception("Explicit boolean preference must override the legacy dropdown");
}
finally
{
    File.Delete(preferencesPath);
    File.Delete(preferencesPath + ".tmp");
}
Console.WriteLine("PASS: installed client patch signatures, private-only skin shortcuts, and persisted SOMS! preferences.");

// Exercise the native Realm registration and native row builder in an isolated
// temporary profile. This catches failures which reflected default counts cannot.
string realmDirectory = Path.Combine(Path.GetTempPath(), "soms-keybindings-check-" + Guid.NewGuid());
Directory.CreateDirectory(realmDirectory);
object? realmAccess = null;
object? realmInstance = null;
try
{
    System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(clientPath, "realm-wrappers.dll"));
    var framework = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Framework.dll"));
    var storageType = framework.GetType("osu.Framework.Platform.NativeStorage", true)!;
    var storage = Activator.CreateInstance(storageType, new object?[] { realmDirectory, null })!;
    var realmAccessType = game.GetType("osu.Game.Database.RealmAccess", true)!;
    realmAccess = Activator.CreateInstance(realmAccessType, new object?[] { storage, "keybindings", null })!;
    var provider = Activator.CreateInstance(framework.GetType("osu.Framework.Input.ReadableKeyCombinationProvider", true)!)!;
    var storeType = game.GetType("osu.Game.Input.RealmKeyBindingStore", true)!;
    var store = Activator.CreateInstance(storeType, realmAccess, provider)!;
    configType.GetProperty("ApiUrl")!.SetValue(config, "https://soms.invalid");
    configType.GetProperty("NonG0V0Server")!.SetValue(config, false);
    var emptyRulesets = Array.CreateInstance(game.GetType("osu.Game.Rulesets.RulesetInfo", true)!, 0);
    storeType.GetMethod("Register")!.Invoke(store, new[] { container, emptyRulesets });
    storeType.GetMethod("Register")!.Invoke(store, new[] { container, emptyRulesets });

    var openRealm = realmAccessType.GetMethod("getRealmInstance", BindingFlags.Instance | BindingFlags.NonPublic)!;
    realmInstance = openRealm.Invoke(realmAccess, null)!;
    var realmType = realmInstance.GetType();
    var bindingType = game.GetType("osu.Game.Input.Bindings.RealmKeyBinding", true)!;
    var query = realmType.GetMethods().Single(method => method.Name == "All" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
        .MakeGenericMethod(bindingType);
    var persistedBindings = ((System.Collections.IEnumerable)query.Invoke(realmInstance, null)!).Cast<object>().ToArray();
    var actionInt = bindingType.GetProperty("ActionInt")!;
    var slotsInRealm = persistedBindings.Where(binding => (bool)isSkinAction.Invoke(null, new[] { actionInt.GetValue(binding) })!).ToArray();
    if (slotsInRealm.Length != 5)
        throw new Exception("Native keybinding registration must persist exactly five skin actions, even when repeated.");

    var subsectionType = game.GetType("osu.Game.Overlays.Settings.Sections.Input.KeyBindingsSubsection", true)!;
    var loadRows = subsectionType.GetMethod("load", BindingFlags.Instance | BindingFlags.NonPublic)!;
    foreach (var section in sections)
    {
        subsectionType.GetProperty("realm", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(section, realmAccess);
        loadRows.Invoke(section, null);
        var children = ((System.Collections.IEnumerable)section.GetType().GetProperty("Children")!.GetValue(section)!).Cast<object>().ToArray();
        var rows = children.Where(child => child.GetType().FullName == "osu.Game.Overlays.Settings.Sections.Input.KeyBindingRow").ToArray();
        if (rows.Length != 1 || ((System.Collections.IEnumerable)rows[0].GetType().GetProperty("KeyBindings")!.GetValue(rows[0])!).Cast<object>().Count() != 1)
            throw new Exception("Native skin row creation did not bind exactly one persisted action.");
    }

    var nativeSubsectionType = game.GetType("osu.Game.Overlays.Settings.Sections.Input.GlobalKeyBindingsSubsection", true)!;
    var categoryType = game.GetType("osu.Game.Input.Bindings.GlobalActionCategory", true)!;
    var textType = framework.GetType("osu.Framework.Localisation.LocalisableString", true)!;
    var title = textType.GetMethods(BindingFlags.Static | BindingFlags.Public).Single(method => method.Name == "op_Implicit"
        && method.GetParameters()[0].ParameterType == typeof(string)).Invoke(null, new object[] { "General" })!;
    var nativeSection = Activator.CreateInstance(nativeSubsectionType, title, Enum.Parse(categoryType, "General"))!;
    var getNativeBindings = nativeSubsectionType.GetMethod("GetKeyBindings", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var nativeBindings = ((System.Collections.IEnumerable)getNativeBindings.Invoke(nativeSection, new[] { realmInstance })!).Cast<object>().ToArray();
    if (nativeBindings.Count(binding => (bool)isSkinAction.Invoke(null, new[] { actionInt.GetValue(binding) })!) != 5)
        throw new Exception("Native shortcut conflict detection does not include all skin slots.");

    // Persist a real combination, close the Realm handle, and verify it survives.
    var combination = bindingType.GetProperty("KeyCombinationString")!;
    string nativeCombination = (string)combination.GetValue(persistedBindings.First(binding => !(bool)isSkinAction.Invoke(null, new[] { actionInt.GetValue(binding) })!))!;
    var transaction = realmType.GetMethod("BeginWrite")!.Invoke(realmInstance, null)!;
    combination.SetValue(slotsInRealm.Single(binding => (int)actionInt.GetValue(binding)! == slotActions[0]), nativeCombination);
    transaction.GetType().GetMethod("Commit")!.Invoke(transaction, null);
    ((IDisposable)transaction).Dispose();
    ((IDisposable)realmInstance).Dispose();
    realmInstance = openRealm.Invoke(realmAccess, null)!;
    var restoredSlot = ((System.Collections.IEnumerable)query.Invoke(realmInstance, null)!).Cast<object>()
        .Single(binding => (int)actionInt.GetValue(binding)! == slotActions[0]);
    if ((string)combination.GetValue(restoredSlot)! != nativeCombination)
        throw new Exception("Native Realm did not preserve the skin hotkey combination.");
    Console.WriteLine("PASS: native Realm registration, native skin binding rows, native-to-skin conflict discovery, and persisted key combinations.");
}
finally
{
    (realmInstance as IDisposable)?.Dispose();
    (realmAccess as IDisposable)?.Dispose();
    // Delete only this check's own randomly named temporary profile.
    if (Path.GetFullPath(realmDirectory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(realmDirectory, recursive: true);
}
