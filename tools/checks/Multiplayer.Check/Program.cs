using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string clientPath = Path.GetFullPath(args[0]);
string pluginPath = Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(clientPath, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Game.dll"));
var framework = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Framework.dll"));
var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
Type G(string name) => game.GetType("osu.Game." + name, true)!;
Type F(string name) => framework.GetType("osu.Framework." + name, true)!;
Type P(string name) => plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Patches." + name, true)!;
object? Get(object value, string name) => value.GetType().GetProperty(name, all)?.GetValue(value) ?? value.GetType().GetField(name, all)?.GetValue(value);
void Set(object value, string name, object? content)
{
    if (value.GetType().GetProperty(name, all) is { } property) property.SetValue(value, content);
    else value.GetType().GetField(name, all)!.SetValue(value, content);
}
object? Call(object value, string method, params object?[] values) => value.GetType().GetMethods(all)
    .Single(candidate => candidate.Name == method && candidate.GetParameters().Length == values.Length).Invoke(value, values);
void Near(double expected, object? actual, string reason)
{
    if (Math.Abs(Convert.ToDouble(actual) - expected) > 0.00001)
        throw new Exception($"{reason}: actual={actual}, expected={expected}");
}
void Expect(bool condition, string reason) { if (!condition) throw new Exception(reason); }
int passed = 0;
var failed = new List<string>();
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS: " + name); }
    catch (Exception e)
    {
        while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
        failed.Add(name + ": " + e);
        Console.WriteLine("FAIL: " + name + ": " + e.Message);
    }
}

var configType = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Configuration.EnhancedRulesetConfig", true)!;
var manager = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Configuration.GlobalConfigManager", true)!;
var config = Activator.CreateInstance(configType)!;
manager.GetField("instance", all)!.SetValue(null, config);
void Private(bool enabled = true)
{
    Set(config, "ApiUrl", enabled ? "https://soms.invalid" : "https://osu.ppy.sh");
    Set(config, "DisableServerExtensions", false);
}
Private();
var harmonyType = plugin.GetType("HarmonyLib.Harmony", true)!;
var harmony = Activator.CreateInstance(harmonyType, "soms.multiplayer.check")!;
foreach (string name in new[]
{
    "MultiplayerRateModsPatch", "MultiplayerSelectableRateModsPatch", "MatchmakingLoadingPatch", "MatchmakingEmptyPoolsPatch",
    "MultiplayerSpectatorScoreRatePatch", "MultiplayerSpectatorAudioRatePatch", "MultiplayerSpectatorClockRatePatch",
    "MultiplayerSpectatorClockReportedRatePatch", "MultiplayerSpectatorCatchupPatch",
})
    Check("Harmony target " + name, () =>
    {
        var processor = harmonyType.GetMethod("CreateClassProcessor")!.Invoke(harmony, new object[] { P(name) })!;
        processor.GetType().GetMethod("Patch")!.Invoke(processor, null);
    });

var apiModType = G("Online.API.APIMod");
var matchType = G("Online.Rooms.MatchType");
var modUtils = G("Utils.ModUtils");
var enumerateMods = modUtils.GetMethod("EnumerateUserSelectableFreeMods")!;
var validMod = modUtils.GetMethod("IsValidModForMatch")!;
object Api(string acronym)
{
    var api = Activator.CreateInstance(apiModType)!;
    Set(api, "Acronym", acronym);
    return api;
}
Array ApiList(params string[] acronyms)
{
    var array = Array.CreateInstance(apiModType, acronyms.Length);
    for (int i = 0; i < acronyms.Length; i++) array.SetValue(Api(acronyms[i]), i);
    return array;
}
foreach (string rulesetName in new[] { "Osu", "Taiko", "Catch", "Mania" })
{
    var rulesetAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Game.Rulesets." + rulesetName + ".dll"));
    var ruleset = Activator.CreateInstance(rulesetAssembly.GetType($"osu.Game.Rulesets.{rulesetName}.{rulesetName}Ruleset", true)!)!;
    foreach (string roomType in new[] { "HeadToHead", "TeamVersus" })
    foreach (bool freestyle in new[] { true, false })
        Check($"{rulesetName}/{roomType}/{(freestyle ? "freestyle" : "freemod")} guest DT/NC settings", () =>
        {
            Private();
            var selected = ((Array)enumerateMods.Invoke(null, new[] { Enum.Parse(matchType, roomType), ApiList(), freestyle ? ApiList() : ApiList("HD"), freestyle, ruleset })!).Cast<object>().ToArray();
            foreach (string acronym in new[] { "DT", "NC" })
            {
                var mod = selected.Single(candidate => (string)Get(candidate, "Acronym")! == acronym);
                Expect((bool)validMod.Invoke(null, new[] { mod, false, Enum.Parse(matchType, roomType), freestyle })!, "Rate mod rejected for a guest");
                Set(Get(mod, "SpeedChange")!, "Value", 1.3);
                var api = Activator.CreateInstance(apiModType, mod)!;
                var restored = Call(api, "ToMod", ruleset)!;
                Near(1.3, Get(Get(restored, "SpeedChange")!, "Value"), "APIMod discarded custom speed");
            }
        });
    Check(rulesetName + " required speed does not stack and locked room stays locked", () =>
    {
        Private();
        foreach (string required in new[] { "DT", "NC", "HT" })
        foreach (bool freestyle in new[] { false, true })
        {
            var selected = ((Array)enumerateMods.Invoke(null, new[] { Enum.Parse(matchType, "HeadToHead"), ApiList(required), ApiList("HD"), freestyle, ruleset })!).Cast<object>();
            Expect(!selected.Any(mod => Get(mod, "Acronym") is "DT" or "NC"), "Additional speed allowed over required " + required);
        }
        var locked = (Array)enumerateMods.Invoke(null, new[] { Enum.Parse(matchType, "HeadToHead"), ApiList(), ApiList(), false, ruleset })!;
        Expect(locked.Length == 0, "Room with no free mods became unlocked");
        Private(false);
        var official = ((Array)enumerateMods.Invoke(null, new[] { Enum.Parse(matchType, "HeadToHead"), ApiList(), ApiList(), true, ruleset })!).Cast<object>();
        Expect(!official.Any(mod => Get(mod, "Acronym") is "DT" or "NC"), "SOMS! rate mods leaked into official freestyle");
        Private();
    });
}

var clockType = G("Screens.OnlinePlay.Multiplayer.Spectate.SpectatorPlayerClock");
var ratesType = P("MultiplayerSpectatorRates");
var rates = ratesType.GetField("Rates", all)!.GetValue(null)!;
void Rate(object owner, double value) => Set(Call(rates, "GetOrCreateValue", owner)!, "Value", value);
void BeginFrame(object clock) => ratesType.GetMethod("BeginFrame", all)!.Invoke(null, new[] { clock });
object ManualMaster(double sourceRate = 1)
{
    var clock = Activator.CreateInstance(F("Timing.ManualFramedClock"))!;
    Set(clock, "Rate", sourceRate); Set(clock, "IsRunning", true); Set(clock, "FramesPerSecond", 60d);
    Rate(clock, sourceRate);
    return clock;
}
object Player(object master, double rate)
{
    var clock = Activator.CreateInstance(clockType, master)!;
    Set(clock, "IsRunning", true); Set(clock, "WaitingOnFrames", false); Rate(clock, rate);
    return clock;
}
foreach (double sourceRate in new[] { 1d, 1.3 })
    Check($"Clock elapsed and duplicate frame, audio source rate {sourceRate}", () =>
    {
        Private();
        object master = ManualMaster(sourceRate), normal = Player(master, 1), dt = Player(master, 1.3);
        Set(master, "CurrentTime", 100 * sourceRate); Set(master, "ElapsedFrameTime", 100 * sourceRate);
        foreach (var clock in new[] { normal, dt }) { BeginFrame(clock); Call(clock, "ProcessFrame"); }
        Near(100, Get(normal, "CurrentTime"), "NM wall time"); Near(130, Get(dt, "CurrentTime"), "DT wall time");
        Near(1, Get(normal, "Rate"), "NM reported rate"); Near(1.3, Get(dt, "Rate"), "DT reported rate");
        foreach (var clock in new[] { normal, dt }) Call(clock, "ProcessFrame");
        Near(100, Get(normal, "CurrentTime"), "NM repeated ProcessFrame"); Near(130, Get(dt, "CurrentTime"), "DT repeated ProcessFrame");
    });

Check("Stopped master catches up without duplicate frame acceleration", () =>
{
    var master = ManualMaster(); Set(master, "CurrentTime", 1000d); Set(master, "IsRunning", false);
    foreach (double playerRate in new[] { 1d, 1.3 })
    {
        var clock = Player(master, playerRate); Set(clock, "IsCatchingUp", true);
        for (int frame = 0; frame < 80; frame++)
        {
            BeginFrame(clock); Call(clock, "ProcessFrame");
            double before = Convert.ToDouble(Get(clock, "CurrentTime"));
            Call(clock, "ProcessFrame"); Near(before, Get(clock, "CurrentTime"), "Stopped master repeated frame");
        }
        Expect(Math.Abs(Convert.ToDouble(Get(clock, "CurrentTime")) / playerRate - 1000) <= 16, "Stopped master catch-up got stuck or overshot");
    }
});

Check("Negative count-in clocks advance in rate-adjusted wall time", () =>
{
    var master = ManualMaster(); Set(master, "CurrentTime", -3000d);
    var normal = Player(master, 1); var dt = Player(master, 1.3);
    Call(normal, "Seek", -3000d); Call(dt, "Seek", -3900d);
    Set(master, "CurrentTime", -2900d); Set(master, "ElapsedFrameTime", 100d);
    foreach (var clock in new[] { normal, dt }) { BeginFrame(clock); Call(clock, "ProcessFrame"); }
    Near(-2900, Get(normal, "CurrentTime"), "NM count-in"); Near(-3770, Get(dt, "CurrentTime"), "DT count-in");
});

// A stopped real GameplayClockContainer provides exact seeks without an audio
// device. The production sync manager can then be tested without any game profile.
object GameMaster()
{
    var source = Activator.CreateInstance(F("Timing.StopwatchClock"), new object[] { false })!;
    return Activator.CreateInstance(G("Screens.Play.GameplayClockContainer"), source, false, false)!;
}
void ProcessGameMasterFrame(object master)
{
    // GameplayClockContainer.ProcessFrame is intentionally a no-op: a real game
    // advances this source from FramedBeatmapClock.Update on every update frame.
    var framed = G("Screens.Play.GameplayClockContainer").GetField("GameplayClock", all)!.GetValue(master)!;
    Call(Get(framed, "finalClockSource")!, "ProcessFrame");
}
Check("Sync manager compares NM and DT on the same wall timeline", () =>
{
    var master = GameMaster(); Call(master, "Seek", 1000d); ProcessGameMasterFrame(master); Rate(master, 1);
    var sync = Activator.CreateInstance(G("Screens.OnlinePlay.Multiplayer.Spectate.SpectatorSyncManager"), master)!;
    var normal = Call(sync, "CreateManagedClock")!; var dt = Call(sync, "CreateManagedClock")!;
    Rate(normal, 1); Rate(dt, 1.3);
    foreach (var clock in new[] { normal, dt }) Set(clock, "WaitingOnFrames", false);
    Call(normal, "Seek", 1000d); Call(dt, "Seek", 1300d);
    Call(sync, "updatePlayerCatchup");
    Expect((bool)Get(normal, "IsRunning")! && (bool)Get(dt, "IsRunning")!, "Synchronized DT was incorrectly paused");
    Expect(!(bool)Get(normal, "IsCatchingUp")! && !(bool)Get(dt, "IsCatchingUp")!, "Synchronized clocks incorrectly catching up");
    Call(normal, "Seek", 0d); Call(dt, "Seek", 0d);
    for (int frame = 0; frame < 80; frame++)
    {
        ProcessGameMasterFrame(master);
        Call(sync, "updatePlayerCatchup");
        foreach (var clock in new[] { normal, dt }) Call(clock, "ProcessFrame");
    }
    Expect(!(bool)Get(normal, "IsCatchingUp")! && !(bool)Get(dt, "IsCatchingUp")!, "Stopped source never finished catchup");
});

Check("Matchmaking request failure clears unavailable pools", () =>
{
    var screenType = G("Screens.OnlinePlay.Matchmaking.Queue.ScreenQueue");
    var screen = Activator.CreateInstance(screenType, Enum.GetValues(G("Online.Matchmaking.MatchmakingPoolType")).GetValue(0)!)!;
    var pools = screenType.GetField("availablePools", all)!.GetValue(screen)!;
    Set(pools, "Value", null);
    var task = (Task)P("MatchmakingLoadingPatch").GetMethod("Observe", all)!.Invoke(null, new object[] { screen, Task.FromException(new InvalidOperationException("simulated unavailable pools")) })!;
    task.GetAwaiter().GetResult();
    Call(Get(screen, "Scheduler")!, "Update");
    Expect(Get(pools, "Value") is Array { Length: 0 }, "Failed pool request left null/loading forever");
});

Check("Empty matchmaking pools show a message only after loading finishes", () =>
{
    var selector = Activator.CreateInstance(G("Screens.OnlinePlay.Matchmaking.Queue.PoolSelector"))!;
    P("MatchmakingEmptyPoolsPatch").GetMethod("Postfix", all)!.Invoke(null, new[] { selector });
    var children = ((IEnumerable)Get(selector, "InternalChildren")!).Cast<object>();
    var message = children.Single(child => child.GetType() == G("Graphics.Sprites.OsuSpriteText"));
    var pools = Get(selector, "AvailablePools")!;
    Near(1, Get(message, "Alpha"), "Empty result should show an explanation");
    Set(pools, "Value", null);
    Near(0, Get(message, "Alpha"), "Loading must not show empty result yet");
    var available = Array.CreateInstance(G("Online.Matchmaking.MatchmakingPool"), 1);
    available.SetValue(Activator.CreateInstance(G("Online.Matchmaking.MatchmakingPool"))!, 0);
    Set(pools, "Value", available);
    Near(0, Get(message, "Alpha"), "Available pools must hide the empty-state message");
});

Check("Switching audible NM to DT keeps each replay at the same wall time", () =>
{
    // Use the actual MasterGameplayClockContainer and screen methods, supplying
    // only the clock/audio fields they need. No beatmap, renderer or Realm is opened.
    var baseClockType = G("Screens.Play.GameplayClockContainer");
    var masterType = G("Screens.Play.MasterGameplayClockContainer");
    var backing = GameMaster();
    var master = RuntimeHelpers.GetUninitializedObject(masterType);
    foreach (string field in new[] { "GameplayClock", "<AdjustmentsFromMods>k__BackingField" })
        baseClockType.GetField(field, all)!.SetValue(master, baseClockType.GetField(field, all)!.GetValue(backing));
    Call(master, "Seek", 1000d); Rate(master, 1);
    var normal = Player(master, 1); var dt = Player(master, 1.3);
    Call(normal, "Seek", 1000d); Call(dt, "Seek", 1300d);
    var screenType = G("Screens.OnlinePlay.Multiplayer.Spectate.MultiSpectatorScreen");
    var screen = RuntimeHelpers.GetUninitializedObject(screenType);
    screenType.GetField("masterClockContainer", all)!.SetValue(screen, master);
    var area = Activator.CreateInstance(G("Screens.OnlinePlay.Multiplayer.Spectate.PlayerArea"), 99, dt)!;
    Call(screen, "bindAudioAdjustments", area);
    Near(1300, Get(master, "CurrentTime"), "New source must refer to the DT replay time");
    foreach (var clock in new[] { normal, dt }) { BeginFrame(clock); Call(clock, "ProcessFrame"); }
    Near(1000, Get(normal, "CurrentTime"), "Switching audio advanced NM without elapsed wall time");
    Near(1300, Get(dt, "CurrentTime"), "Switching audio advanced DT without elapsed wall time");
    var normalArea = Activator.CreateInstance(G("Screens.OnlinePlay.Multiplayer.Spectate.PlayerArea"), 98, normal)!;
    Call(screen, "bindAudioAdjustments", normalArea);
    Near(1000, Get(master, "CurrentTime"), "Switching back must restore the NM source timeline");
    foreach (var clock in new[] { normal, dt }) { BeginFrame(clock); Call(clock, "ProcessFrame"); }
    Near(1000, Get(normal, "CurrentTime"), "Switching back rewound NM");
    Near(1300, Get(dt, "CurrentTime"), "Switching back rewound DT");
});

Console.WriteLine($"Multiplayer checks: {passed} passed, {failed.Count} failed.");
foreach (string error in failed) Console.WriteLine(error);
Environment.ExitCode = failed.Count == 0 ? 0 : 1;
