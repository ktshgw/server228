using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string clientPath = Path.GetFullPath(args[0]), pluginPath = Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(clientPath, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Game.dll"));
var framework = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Framework.dll"));
var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
Type G(string name) => game.GetType("osu.Game." + name, true)!;
Type P(string name) => plugin.GetType("osu.Game.Rulesets.EnhancedAuth." + name, true)!;
object? Get(object owner, string name) => owner.GetType().GetProperty(name, flags)?.GetValue(owner) ?? owner.GetType().GetField(name, flags)?.GetValue(owner);
void Set(object owner, string name, object? value) => owner.GetType().GetProperty(name, flags)!.SetValue(owner, value);
void Postfix(string patch, object drawable) => P("Patches." + patch).GetMethod("Postfix", flags)!.Invoke(null, new[] { drawable });
void Require(bool test, string message) { if (!test) throw new Exception(message); }
IEnumerable<object> Children(object owner) => ((IEnumerable)Get(owner, "Children")!).Cast<object>();
void UpdateScheduler(object owner) => Get(owner, "Scheduler")!.GetType().GetMethod("Update", Type.EmptyTypes)!.Invoke(Get(owner, "Scheduler"), null);

string temporaryDirectory = Path.Combine(Path.GetTempPath(), "soms-lifecycle-check-" + Guid.NewGuid());
Directory.CreateDirectory(temporaryDirectory);
var disposables = new List<IDisposable>();
try
{
    // A real, isolated OsuConfigManager supplies the normal preferences path.
    // No Realm is opened and no user config or skin files are touched.
    var storage = Activator.CreateInstance(framework.GetType("osu.Framework.Platform.NativeStorage", true)!, new object?[] { temporaryDirectory, null })!;
    var localConfig = Activator.CreateInstance(G("Configuration.OsuConfigManager"), storage)!;
    disposables.Add((IDisposable)localConfig);
    var fakeGame = RuntimeHelpers.GetUninitializedObject(G("OsuGame"));
    G("OsuGameBase").GetProperty("LocalConfig", flags)!.SetValue(fakeGame, localConfig);
    var globalManager = P("Configuration.GlobalConfigManager");
    globalManager.GetMethod("InitializeGameBase")!.Invoke(null, new[] { fakeGame });
    var authConfig = Activator.CreateInstance(P("Configuration.EnhancedRulesetConfig"))!;
    Set(authConfig, "ApiUrl", "https://soms.invalid");
    globalManager.GetField("instance", flags)!.SetValue(null, authConfig);
    var preference = P("Configuration.SomsClientPreferences").GetProperty("Instance")!.GetValue(null)!;

    if (args.Contains("--teamvs-only"))
    {
        var teamPatch = P("Patches.SomsTeamLeaderboardPatch");
        var resolve = teamPatch.GetMethod("ResolveAlpha", flags)!;
        float Resolve(bool enabled, bool teams, float current, bool? leaderboard) =>
            (float)resolve.Invoke(null, new object?[] { enabled, teams, current, leaderboard })!;
        var bindableLongType = framework.GetType("osu.Framework.Bindables.BindableLong", true)!;
        var localScore = Activator.CreateInstance(bindableLongType, 0L)!;
        var remoteScore = Activator.CreateInstance(bindableLongType, 0L)!;
        var isBound = teamPatch.GetMethod("isBound", flags)!;
        Require(!(bool)isBound.Invoke(null, new[] { localScore })!, "A fresh team score was incorrectly detected as bound");
        bindableLongType.GetProperty("BindTarget")!.SetValue(localScore, remoteScore);
        Require((bool)isBound.Invoke(null, new[] { localScore })!, "A bound team score was not detected while hidden");
        Require(Resolve(true, true, 1, false) == 0, "Team score remained visible with the leaderboard disabled");
        Require(Resolve(true, true, 0, true) == 1, "Team score did not return with the leaderboard enabled");
        Require(Resolve(true, true, Resolve(true, true, Resolve(true, true, Resolve(true, true, 1, false), true), false), true) == 1,
            "Team score did not return after two leaderboard hide/show cycles");
        Require(Resolve(false, true, 0, false) == 1, "Disabled SOMS option still hid the team score");
        Require(Resolve(true, false, 0, true) == 0, "A non-team match unexpectedly gained the score bar");
        Require(Resolve(true, true, 1, null) == 1, "Score bar disappeared before its HUD leaderboard was attached");
        // Exercise the installed Harmony hook through the native update entry
        // point. Testing ResolveAlpha alone misses hooks skipped at Alpha = 0.
        var harmony = Activator.CreateInstance(plugin.GetType("HarmonyLib.Harmony", true)!, "teamvs-lifecycle-check")!;
        var processor = harmony.GetType().GetMethod("CreateClassProcessor")!.Invoke(harmony, new object[] { teamPatch })!;
        processor.GetType().GetMethod("Patch")!.Invoke(processor, null);
        var hudType = G("Screens.Play.HUDOverlay");
        var hud = Activator.CreateInstance(hudType, new object?[] { null, Array.CreateInstance(G("Rulesets.Mods.Mod"), 0), Activator.CreateInstance(G("Screens.Play.PlayerConfiguration")) })!;
        disposables.Add((IDisposable)hud);
        var visibility = Activator.CreateInstance(framework.GetType("osu.Framework.Bindables.BindableBool", true)!, new object[] { false })!;
        hudType.GetField("configLeaderboardVisibility", flags)!.SetValue(hud, visibility);
        var display = Activator.CreateInstance(G("Screens.OnlinePlay.Multiplayer.GameplayMatchScoreDisplay"))!;
        disposables.Add((IDisposable)display);
        // Link the ancestry without loading graphics, audio or a beatmap.
        Set(display, "Parent", hud);
        foreach (string score in new[] { "Team1Score", "Team2Score" })
            Set(Get(display, score)!, "BindTarget", Activator.CreateInstance(bindableLongType, 0L)!);
        Set(Get(preference, "TeamVsInLeaderboards")!, "Value", true);
        Set(display, "Alpha", 0f);
        var update = display.GetType().GetMethod("UpdateSubTree")!;
        var actionType = G("Input.Bindings.GlobalAction");
        var teamPressType = framework.GetType("osu.Framework.Input.Events.KeyBindingPressEvent`1", true)!.MakeGenericType(actionType);
        var teamInputState = Activator.CreateInstance(framework.GetType("osu.Framework.Input.States.InputState", true)!, new object?[6])!;
        var press = Activator.CreateInstance(teamPressType, new object[] { teamInputState, Enum.Parse(actionType, "ToggleInGameLeaderboard"), false })!;
        update.Invoke(display, null);
        Require((float)Get(display, "Alpha")! == 0, "Initially hidden leaderboard exposed team scores");
        for (int i = 0; i < 6; i++)
        {
            hudType.GetMethod("OnPressed")!.Invoke(hud, new[] { press });
            update.Invoke(display, null);
            Require((float)Get(display, "Alpha")! == (i % 2 == 0 ? 1 : 0), "Native Tab/update failed to toggle team scores at cycle " + i);
        }
        Set(Get(preference, "TeamVsInLeaderboards")!, "Value", false);
        update.Invoke(display, null);
        Require((float)Get(display, "Alpha")! == 1, "Disabling the option did not restore the hidden team bar");
        Console.WriteLine("PASS: native Tab action and patched update restore an initially hidden bar, survive three hide/show cycles, and restore visibility when the option is disabled.");
        return;
    }

    var rulesetType = P("EnhancedAuthRuleset");
    if (args.Contains("--binding-only"))
    {
        Activator.CreateInstance(rulesetType);
        var leaderboard = Activator.CreateInstance(G("Screens.Play.HUD.DrawableGameplayLeaderboard"))!;
        disposables.Add((IDisposable)leaderboard);
        for (int callback = 0; callback < 30; callback++) Postfix("SomsLeaderboardPreferenceBindingPatch", leaderboard);
        Console.WriteLine("PASS: 30 repeated callbacks on the same leaderboard do not throw or re-register.");
        return;
    }
    for (int i = 0; i < 30; i++) Activator.CreateInstance(rulesetType);
    var harmonyType = plugin.GetType("HarmonyLib.Harmony", true)!;
    var allMethods = (IEnumerable)harmonyType.GetMethod("GetAllPatchedMethods")!.Invoke(null, null)!;
    int inspected = 0;
    foreach (var method in allMethods.Cast<MethodBase>())
    {
        var info = harmonyType.GetMethod("GetPatchInfo")!.Invoke(null, new object[] { method })!;
        foreach (string kind in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
        {
            var ownPatches = ((IEnumerable)Get(info, kind)!).Cast<object>().Where(patch => (string)Get(patch, "owner")! == "enhancedauthruleset");
            Require(ownPatches.GroupBy(patch => Get(patch, "PatchMethod")).All(group => group.Count() == 1), "Repeated ruleset construction duplicated " + kind + " for " + method);
        }
        inspected++;
    }
    Require(inspected > 10, "Ruleset constructor did not install the production patches");
    Console.WriteLine($"PASS: 30 ruleset constructions keep one copy of each patch across {inspected} methods.");

    var panel = Activator.CreateInstance(G("Overlays.Settings.Sections.Input.KeyBindingPanel"))!;
    disposables.Add((IDisposable)panel);
    for (int i = 0; i < 30; i++) Postfix("SomsSkinHotkeySettingsPatch", panel);
    var sections = ((IEnumerable)G("Overlays.SettingsPanel").GetField("loadableSections", flags)!.GetValue(panel)!).Cast<object>();
    Require(sections.Count(section => section.GetType() == P("UI.SomsSkinHotkeysSection")) == 1, "Skin hotkey panel was duplicated");

    var skinSettings = Activator.CreateInstance(G("Overlays.Settings.Sections.SkinSection"))!;
    disposables.Add((IDisposable)skinSettings);
    for (int i = 0; i < 30; i++) Postfix("SomsSkinElementSettingsPatch", skinSettings);
    Require(Children(skinSettings).Count() == 5, "Global skin settings were duplicated");
    Require(Children(skinSettings).Select(child => Get(child, "Name")).Distinct().Count() == 5, "Skin setting IDs must be distinct");
    Require(Children(skinSettings).Count(child => Get(child, "Control")!.GetType().Name == "FormCheckBox") == 4, "Four skin preferences must be toggles");
    Require(Children(skinSettings).Count(child => Get(child, "Control")!.GetType().Name.StartsWith("FormDropdown")) == 1, "Slider misses use one dropdown");

    var pools = Activator.CreateInstance(G("Screens.OnlinePlay.Matchmaking.Queue.PoolSelector"))!;
    disposables.Add((IDisposable)pools);
    for (int i = 0; i < 30; i++) Postfix("MatchmakingEmptyPoolsPatch", pools);
    var messages = ((IEnumerable)Get(pools, "InternalChildren")!).Cast<object>().Count(child => (string)Get(child, "Name")! == "soms-matchmaking-empty-pools");
    Require(messages == 1, "Empty matchmaking message was duplicated");
    Console.WriteLine("PASS: 30 repeated callbacks add one hotkey section, four skin toggles, one miss dropdown and one empty-pool message.");

    var poolType = G("Online.Matchmaking.MatchmakingPool");
    for (int cycle = 0; cycle < 30; cycle++)
    {
        var selector = Activator.CreateInstance(G("Screens.OnlinePlay.Matchmaking.Queue.PoolSelector"))!;
        var available = Get(selector, "AvailablePools")!;
        var changedEvent = available.GetType().GetEvent("ValueChanged")!;
        var changedType = changedEvent.EventHandlerType!;
        var eventArgumentType = changedType.GetMethod("Invoke")!.GetParameters()[0].ParameterType;
        var unrelated = Expression.Lambda(changedType, Expression.Empty(), Expression.Parameter(eventArgumentType)).Compile();
        changedEvent.AddEventHandler(available, unrelated);
        for (int callback = 0; callback < 30; callback++) Postfix("MatchmakingEmptyPoolsPatch", selector);
        var handlers = ((Delegate)Get(available, "ValueChanged")!).GetInvocationList();
        Require(handlers.Length == 2, "Repeated pool load callbacks duplicated the empty-message subscription");
        var ownHandler = handlers.Single(handler => handler != unrelated);
        var message = ((IEnumerable)Get(selector, "InternalChildren")!).Cast<object>().Single(child => (string)Get(child, "Name")! == "soms-matchmaking-empty-pools");
        Set(available, "Value", null);
        Require((float)Get(message, "Alpha")! == 0, "Loading message should be hidden before disposal");

        // Both child removal and closing its owning screen must release our handler.
        if (cycle % 2 == 0)
        {
            ((IDisposable)message).Dispose();
            var remaining = ((Delegate?)Get(available, "ValueChanged"))?.GetInvocationList() ?? [];
            Require(remaining.Length == 1 && remaining[0] == unrelated, "Message disposal did not remove only its own subscription");
        }
        else
            ((IDisposable)selector).Dispose();

        var empty = Array.CreateInstance(poolType, 0);
        Set(available, "Value", empty);
        ownHandler.DynamicInvoke(Activator.CreateInstance(eventArgumentType, new object?[] { null, empty }));
        Require((float)Get(message, "Alpha")! == 0, "A captured pool callback changed an already disposed message/selector");
        ((IDisposable)selector).Dispose();
    }
    Console.WriteLine("PASS: 30 empty-pool lifecycles remove only the owned subscription and ignore callbacks after child/selector disposal.");

    var queueType = G("Screens.OnlinePlay.Matchmaking.Queue.ScreenQueue");
    var poolKind = Enum.GetValues(G("Online.Matchmaking.MatchmakingPoolType")).GetValue(0)!;
    var observe = P("Patches.MatchmakingLoadingPatch").GetMethod("Observe", flags)!;
    for (int cycle = 0; cycle < 30; cycle++)
    {
        var queue = Activator.CreateInstance(queueType, poolKind)!;
        var available = Get(queue, "availablePools")!;
        var scheduler = Get(queue, "Scheduler")!;
        Set(available, "Value", null);
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = (Task)observe.Invoke(null, new object[] { queue, request.Task })!;
        if (cycle % 2 == 0)
        {
            ((IDisposable)queue).Dispose();
            int before = (int)Get(scheduler, "TotalPendingTasks")!;
            request.SetException(new InvalidOperationException("Simulated pool request completed after screen closed"));
            observed.GetAwaiter().GetResult();
            Require((int)Get(scheduler, "TotalPendingTasks")! == before, "Failed pool request scheduled work on an already disposed screen");
        }
        else
        {
            request.SetException(new InvalidOperationException("Simulated pool request failed just before screen closed"));
            observed.GetAwaiter().GetResult();
            ((IDisposable)queue).Dispose();
        }
        UpdateScheduler(queue);
        Require(Get(available, "Value") == null, "Queued failure callback changed pools after screen disposal");
    }
    Console.WriteLine("PASS: 30 failed pool requests across disposal before/after completion neither schedule nor change a closed screen.");

    // Observe the actual scheduled native method without requiring a renderer.
    // Its production postfix still executes; only the visual animation body is skipped.
    var leaderboardType = G("Screens.Play.HUD.DrawableGameplayLeaderboard");
    var updateState = leaderboardType.GetMethod("updateState", flags)!;
    var harness = Activator.CreateInstance(harmonyType, "soms.lifecycle.check")!;
    var harmonyMethod = plugin.GetType("HarmonyLib.HarmonyMethod", true)!;
    var prefix = Activator.CreateInstance(harmonyMethod, typeof(LifecycleProbe).GetMethod(nameof(LifecycleProbe.UpdateState))!)!;
    var patchMethod = harmonyType.GetMethods().Single(method => method.Name == "Patch" && method.GetParameters().Length >= 5);
    var patchArguments = new object?[patchMethod.GetParameters().Length];
    patchArguments[0] = updateState; patchArguments[1] = prefix;
    patchMethod.Invoke(harness, patchArguments);

    // Exercise the real patched native layout, including the two endpoints and
    // Player identity (replay/pause/break use the same screen identity).
    var scaleType = G("Graphics.Containers.ScalingContainer+ScalingDrawSizePreservingFillContainer");
    var vectorType = Assembly.Load("osuTK").GetType("osuTK.Vector2", true)!;
    G("OsuGame").GetField("<ScalingContainerTargetDrawSize>k__BackingField", flags)!
        .SetValue(fakeGame, Activator.CreateInstance(vectorType, 1024f, 768f));
    var parentType = framework.GetType("osu.Framework.Graphics.Containers.Container", true)!;
    var drawableBase = framework.GetType("osu.Framework.Graphics.Drawable", true)!;
    var parent = Activator.CreateInstance(parentType)!;
    disposables.Add((IDisposable)parent);
    Set(parent, "Size", Activator.CreateInstance(vectorType, 1280f, 720f));
    var ui = Activator.CreateInstance(scaleType, new object[] { true })!;
    var playfield = Activator.CreateInstance(scaleType, new object[] { false })!;
    foreach (var scaled in new[] { ui, playfield })
    {
        parentType.GetMethod("Add", new[] { drawableBase })!.Invoke(parent, new[] { scaled });
        drawableBase.GetProperty("Parent", flags)!.SetValue(scaled, parent);
        scaleType.GetMethod("load", flags)!.Invoke(scaled, new[] { localConfig });
        scaleType.GetProperty("game", flags)!.SetValue(scaled, fakeGame);
    }
    var screenStack = Activator.CreateInstance(G("Screens.OsuScreenStack"))!;
    disposables.Add((IDisposable)screenStack);
    G("OsuGame").GetProperty("ScreenStack", flags)!.SetValue(fakeGame, screenStack);
    var stack = framework.GetType("osu.Framework.Screens.ScreenStack", true)!.GetField("stack", flags)!.GetValue(screenStack)!;
    var separate = Get(preference, "SeparateInterfaceScales")!;
    Set(separate, "Value", true);
    Set(Get(preference, "MenuInterfaceScale")!, "Value", 0.1f);
    Set(Get(preference, "GameplayInterfaceScale")!, "Value", 2f);
    void CheckScale(object scaled, float expected)
    {
        scaleType.GetMethod("Update", flags)!.Invoke(scaled, null);
        Require(Math.Abs((float)Get(Get(scaled, "Scale")!, "X")! - expected) < 0.0001f, "Actual native scale must be " + expected);
        Require(Math.Abs((float)Get(Get(scaled, "Size")!, "X")! - 1 / expected) < 0.0001f, "Native inverse size must match scale");
    }
    CheckScale(ui, 0.1f);
    CheckScale(playfield, 1);
    stack.GetType().GetMethod("Push")!.Invoke(stack, new[] { RuntimeHelpers.GetUninitializedObject(G("Screens.Play.ReplayPlayer")) });
    CheckScale(ui, 2);
    Set(Get(preference, "MenuInterfaceScale")!, "Value", 1.5f);
    CheckScale(ui, 2);
    CheckScale(playfield, 1);
    stack.GetType().GetMethod("Clear")!.Invoke(stack, null);
    CheckScale(ui, 1.5f);
    var restoredPreferences = Activator.CreateInstance(P("Configuration.SomsClientPreferences"), Path.Combine(temporaryDirectory, "soms_client_preferences.json"))!;
    Require((bool)Get(Get(restoredPreferences, "SeparateInterfaceScales")!, "Value")!
        && (float)Get(Get(restoredPreferences, "MenuInterfaceScale")!, "Value")! == 1.5f
        && (float)Get(Get(restoredPreferences, "GameplayInterfaceScale")!, "Value")! == 2f, "Split scale preferences must survive restart");
    Set(separate, "Value", false);
    CheckScale(ui, (float)scaleType.GetProperty("CurrentScale", flags)!.GetValue(ui)!);
    Set(separate, "Value", true);
    Set(authConfig, "ApiUrl", "https://osu.ppy.sh");
    CheckScale(ui, (float)scaleType.GetProperty("CurrentScale", flags)!.GetValue(ui)!);
    Set(authConfig, "ApiUrl", "https://soms.invalid");
    Set(separate, "Value", false);
    Console.WriteLine("PASS: real native scale and inverse sizing at 0.1/2, menu/player independence, preserved playfield, persisted values, native and official restore.");
    var collapse = Get(preference, "UseSkinLeaderboardCollapse")!;

    // Exercise the real patched input handler and the already bound settings
    // checkbox. No SkinManager is needed to toggle this global preference.
    int toggleAction = Convert.ToInt32(Enum.Parse(P("Patches.SomsSkinAction"), "ToggleLeaderboardCollapse"));
    var globalAction = G("Input.Bindings.GlobalAction");
    var inputStateConstructor = framework.GetType("osu.Framework.Input.States.InputState", true)!.GetConstructors()
        .Single(constructor => constructor.GetParameters().All(parameter => parameter.IsOptional));
    var inputState = inputStateConstructor.Invoke(inputStateConstructor.GetParameters().Select(parameter => parameter.DefaultValue).ToArray());
    var pressType = framework.GetType("osu.Framework.Input.Events.KeyBindingPressEvent`1", true)!.MakeGenericType(globalAction);
    var pressed = G("OsuGame").GetMethod("OnPressed", new[] { pressType })!;
    bool PressToggle(bool repeat) => (bool)pressed.Invoke(fakeGame, new[]
    {
        Activator.CreateInstance(pressType, new object?[] { inputState, Enum.ToObject(globalAction, toggleAction), repeat })!
    })!;
    var collapseControl = Get(Children(skinSettings).Single(child => (string)Get(child, "Name")! == "soms-skin-leaderboard-collapse"), "Control")!;
    var liveLeaderboard = Activator.CreateInstance(leaderboardType)!;
    Postfix("SomsLeaderboardPreferenceBindingPatch", liveLeaderboard);
    Set(collapse, "Value", true);
    Require(PressToggle(false), "Leaderboard hotkey was not consumed");
    Require(!(bool)Get(collapse, "Value")! && !(bool)Get(Get(collapseControl, "Current")!, "Value")!, "Hotkey did not change the existing checkbox");
    UpdateScheduler(liveLeaderboard);
    Require(LifecycleProbe.Count(liveLeaderboard) == 1, "Hotkey did not update the active gameplay leaderboard");
    for (int repeat = 0; repeat < 30; repeat++) Require(PressToggle(true), "Held hotkey must remain consumed");
    Require(!(bool)Get(collapse, "Value")!, "Held hotkey toggled the preference again");
    using (var stored = System.Text.Json.JsonDocument.Parse(File.ReadAllText((string)Get(preference, "filePath")!)))
        Require(!stored.RootElement.GetProperty("UseSkinLeaderboardCollapse").GetBoolean(), "Hotkey preference was not saved");
    Require(PressToggle(false) && (bool)Get(collapse, "Value")!, "Second press did not restore the skin rule");
    UpdateScheduler(liveLeaderboard);
    Require(LifecycleProbe.Count(liveLeaderboard) == 2, "Second press did not refresh the leaderboard");
    ((IDisposable)liveLeaderboard).Dispose();

    var hotkeysSection = sections.Single(section => section.GetType() == P("UI.SomsSkinHotkeysSection"));
    Require(Children(hotkeysSection).Count() == 7, "Hotkey section must contain five skin slots, leaderboard and practice subsections");
    var leaderboardSection = Children(hotkeysSection).Single(child => child.GetType().Name == "LeaderboardSubsection");
    Require(((IEnumerable)Get(leaderboardSection, "Defaults")!).Cast<object>().Count() == 1, "Leaderboard hotkey row duplicated");
    var globalBindings = Activator.CreateInstance(G("Input.Bindings.GlobalActionContainer"), new object?[] { null })!;
    disposables.Add((IDisposable)globalBindings);
    var defaultsPatch = P("Patches.SomsSkinDefaultBindingsPatch").GetMethod("Postfix", flags)!;
    object?[] defaults = { Get(globalBindings, "DefaultKeyBindings")! };
    for (int repeat = 0; repeat < 30; repeat++) defaultsPatch.Invoke(null, defaults);
    var bindingsList = ((IEnumerable)defaults[0]!).Cast<object>().ToArray();
    foreach (var action in Enum.GetValues(P("Patches.SomsSkinAction")))
        Require(bindingsList.Count(binding => Convert.ToInt32(Get(binding, "Action")) == Convert.ToInt32(action)) == 1, "Repeated default binding callback duplicated a SOMS action");
    var realmBinding = Activator.CreateInstance(G("Input.Bindings.RealmKeyBinding"), nonPublic: true)!;
    Set(realmBinding, "ActionInt", toggleAction);
    var descriptionAction = realmBinding.GetType().GetMethod("GetAction")!.Invoke(realmBinding, new object?[] { null })!;
    Require(descriptionAction.GetType() == P("Patches.SomsSkinAction"), "Native binding editor cannot resolve the hotkey description");
    Console.WriteLine("PASS: actual hotkey toggles/saves the checkbox and live leaderboard, ignores 30 repeats, and registers one named binding/subsection.");

    var registry = P("Patches.SomsLeaderboardPreferenceBindingPatch").GetField("bindings", flags)!.GetValue(null)!;
    var tryGet = registry.GetType().GetMethod("TryGetValue")!;
    object? Registration(object owner)
    {
        var values = new object?[] { owner, null };
        return (bool)tryGet.Invoke(registry, values)! ? values[1] : null;
    }

    for (int skin = 0; skin < 30; skin++)
    {
        var leaderboard = Activator.CreateInstance(leaderboardType)!;
        for (int callback = 0; callback < 30; callback++) Postfix("SomsLeaderboardPreferenceBindingPatch", leaderboard);
        var registration = Registration(leaderboard);
        Require(registration != null, "Leaderboard did not get a preference subscription");
        Set(collapse, "Value", true);
        Set(collapse, "Value", false);
        Set(collapse, "Value", true);
        UpdateScheduler(leaderboard);
        Require(LifecycleProbe.Count(leaderboard) == 1, "Repeated preference callbacks were not coalesced into one update");

        // Reproduce a skin replacement while an old leaderboard has queued work.
        Set(collapse, "Value", false);
        ((IDisposable)leaderboard).Dispose();
        Require(Registration(leaderboard) == null, "Disposed leaderboard remained in the preference registry");
        Require((bool)Get(registration!, "disposed")!, "Disposed leaderboard kept an active preference subscription");
        Set(collapse, "Value", true);
        UpdateScheduler(leaderboard);
        Require(LifecycleProbe.Count(leaderboard) == 1, "Preference change ran on a disposed skin leaderboard");
        Postfix("SomsLeaderboardPreferenceBindingPatch", leaderboard);
        Require(Registration(leaderboard) == null, "Repeated callback recreated a disposed leaderboard subscription");
    }
    Console.WriteLine("PASS: 30 skin leaderboard replacements × 30 load callbacks, single live update, cancellation and unsubscribe on disposal.");

    var osuRuleset = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Game.Rulesets.Osu.dll"));
    var componentType = osuRuleset.GetType("osu.Game.Rulesets.Osu.OsuSkinComponents", true)!;
    var lookupType = osuRuleset.GetType("osu.Game.Rulesets.Osu.OsuSkinComponentLookup", true)!;
    var hitResultType = G("Rulesets.Scoring.HitResult");
    var resultLookupType = G("Skinning.SkinComponentLookup`1").MakeGenericType(hitResultType);
    var skinnableType = G("Skinning.SkinnableDrawable");
    var nativeSkinChanged = skinnableType.GetMethod("SkinChanged", flags)!;
    var nativeSkinnableUpdate = skinnableType.GetMethod("Update", flags)!;
    var source = DispatchProxy.Create(G("Skinning.ISkinSource"), typeof(SkinSourceProbe));
    var visibilityPatch = P("Patches.SomsSkinElementVisibilityPatch");
    var visibilityPostfix = visibilityPatch.GetMethod("Postfix", flags)!;
    var visibilityRegistry = visibilityPatch.GetField("bindings", flags)!.GetValue(null)!;
    object? VisibilityRegistration(object owner)
    {
        var values = new object?[] { owner, null };
        return (bool)visibilityRegistry.GetType().GetMethod("TryGetValue")!.Invoke(visibilityRegistry, values)! ? values[1] : null;
    }
    object MakeSkinnable(object lookup) => Activator.CreateInstance(skinnableType,
        new object?[] { lookup, null, Enum.Parse(G("Skinning.ConfineMode"), "NoScaling") })!;

    foreach (var (key, lookup) in new[]
    {
        ("ShowSliderEndMiss", Activator.CreateInstance(resultLookupType, Enum.Parse(hitResultType, "IgnoreMiss"))!),
        ("ShowSliderEndMiss", Activator.CreateInstance(resultLookupType, Enum.Parse(hitResultType, "LargeTickMiss"))!),
        ("ShowSliderFollowCircle", Activator.CreateInstance(lookupType, Enum.Parse(componentType, "SliderFollowCircle"))!),
        ("DrawFollowPoints", Activator.CreateInstance(lookupType, Enum.Parse(componentType, "FollowPoint"))!),
    })
    {
        var visible = Get(preference, key)!;
        for (int cycle = 0; cycle < 30; cycle++)
        {
            Set(visible, "Value", true);
            var drawable = MakeSkinnable(lookup);
            for (int callback = 0; callback < 30; callback++) visibilityPostfix.Invoke(null, new[] { drawable, lookup });
            var registration = VisibilityRegistration(drawable);
            Require(registration != null && (float)Get(drawable, "Alpha")! == 1, key + " must start visible");
            var component = Activator.CreateInstance(framework.GetType("osu.Framework.Graphics.Shapes.Box", true)!)!;
            ((SkinSourceProbe)source).Component = component;
            nativeSkinChanged.Invoke(drawable, new[] { source });
            Require(ReferenceEquals(Get(drawable, "Drawable"), component), key + " replaced the native skin component");
            Set(visible, "Value", false);
            UpdateScheduler(drawable);
            Require((float)Get(drawable, "Alpha")! == 0 && (bool)Get(drawable, "AlwaysPresent")!, key + " must hide while allowing animation/skin callbacks");
            // A skin-load-only hook misses restoration of alpha between skin
            // callbacks (pool reuse/rewind). Execute the real patched update.
            Set(drawable, "Alpha", 0.8f);
            nativeSkinnableUpdate.Invoke(drawable, null);
            Require((float)Get(drawable, "Alpha")! == 0, key + " reappeared after a visibility reset without a skin change");
            for (int callback = 0; callback < 30; callback++)
            {
                component = Activator.CreateInstance(framework.GetType("osu.Framework.Graphics.Shapes.Box", true)!)!;
                ((SkinSourceProbe)source).Component = component;
                nativeSkinChanged.Invoke(drawable, new[] { source });
                Require(ReferenceEquals(Get(drawable, "Drawable"), component), key + " interfered with native skin replacement");
            }
            Require(ReferenceEquals(registration, VisibilityRegistration(drawable)) && (float)Get(drawable, "Alpha")! == 0,
                key + " skin reload must retain one subscription and remain hidden");
            Set(visible, "Value", true);
            UpdateScheduler(drawable);
            Require((float)Get(drawable, "Alpha")! == 1 && !(bool)Get(drawable, "AlwaysPresent")!, key + " must restore native visibility");
            Set(visible, "Value", false);
            ((IDisposable)drawable).Dispose();
            Require(VisibilityRegistration(drawable) == null && (bool)Get(registration!, "disposed")!, key + " leaked a disposed subscription");
            UpdateScheduler(drawable);
            Require((float)Get(drawable, "Alpha")! == 1, key + " pending update ran after disposal");
            Set(visible, "Value", true);
            visibilityPostfix.Invoke(null, new[] { drawable, lookup });
            Require(VisibilityRegistration(drawable) == null, key + " rebound an already disposed skin element");
        }
    }
    // Existing drawables may have loaded while connected to the official server
    // or before SOMS patch activation. They must obey the preference next frame.
    var lateLookup = Activator.CreateInstance(resultLookupType, Enum.Parse(hitResultType, "IgnoreMiss"))!;
    var late = MakeSkinnable(lateLookup);
    Set(authConfig, "ApiUrl", "https://osu.ppy.sh");
    ((SkinSourceProbe)source).Component = Activator.CreateInstance(framework.GetType("osu.Framework.Graphics.Shapes.Box", true)!);
    nativeSkinChanged.Invoke(late, new[] { source });
    Require(VisibilityRegistration(late) == null, "Official skin load must remain unmodified");
    Set(Get(preference, "ShowSliderEndMiss")!, "Value", false);
    Set(authConfig, "ApiUrl", "https://soms.invalid");
    nativeSkinnableUpdate.Invoke(late, null);
    Require(VisibilityRegistration(late) != null && (float)Get(late, "Alpha")! == 0, "Already loaded judgement escaped sliderendmiss preference");
    Set(authConfig, "ApiUrl", "https://osu.ppy.sh");
    nativeSkinnableUpdate.Invoke(late, null);
    Require((float)Get(late, "Alpha")! == 1, "Leaving SOMS must restore native visibility");
    Set(authConfig, "ApiUrl", "https://soms.invalid");
    ((IDisposable)late).Dispose();
    foreach (object lookup in new[]
    {
        Activator.CreateInstance(resultLookupType, Enum.Parse(hitResultType, "Miss"))!,
        Activator.CreateInstance(lookupType, Enum.Parse(componentType, "SliderBall"))!,
    })
    {
        var drawable = MakeSkinnable(lookup);
        visibilityPostfix.Invoke(null, new[] { drawable, lookup });
        Require(VisibilityRegistration(drawable) == null && (float)Get(drawable, "Alpha")! == 1, "Unrelated skin element was changed");
        ((IDisposable)drawable).Dispose();
    }
    Console.WriteLine("PASS: both slider toggles × 30 skin lifecycles × 30 actual native skin replacements; native update after alpha reset, late activation, official restore, hide/show, native children, cancellation and disposal; other judgements and slider ball untouched.");
}
finally
{
    foreach (var disposable in disposables.AsEnumerable().Reverse()) disposable.Dispose();
    if (Path.GetFullPath(temporaryDirectory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(temporaryDirectory, recursive: true);
}

public static class LifecycleProbe
{
    private static readonly Dictionary<object, int> counts = new(ReferenceEqualityComparer.Instance);
    public static bool UpdateState(object __instance)
    {
        counts[__instance] = Count(__instance) + 1;
        return false;
    }
    public static int Count(object value) => counts.GetValueOrDefault(value);
}

public class SkinSourceProbe : DispatchProxy
{
    public object? Component;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name == "GetDrawableComponent" ? Component : null;
}
