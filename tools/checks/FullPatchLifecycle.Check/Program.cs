using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

// No host, user Realm, storage, credentials, network requests or individual
// PatchProcessor calls: exercise the real module constructor and full PatchAll.
if (args.Length < 2)
    throw new ArgumentException("client-directory merged-plugin.dll [--parallel-first]");
string clientPath = Path.GetFullPath(args[0]);
string pluginPath = Path.GetFullPath(args[1]);
bool parallelFirst = args.Contains("--parallel-first");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(clientPath, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Game.dll"));
var framework = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Framework.dll"));
var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
const string owner = "enhancedauthruleset";
Type G(string name) => game.GetType("osu.Game." + name, true)!;
Type F(string name) => framework.GetType("osu.Framework." + name, true)!;
Type P(string name) => plugin.GetType("osu.Game.Rulesets.EnhancedAuth." + name, true)!;
object? Get(object value, string name) => value.GetType().GetProperty(name, all)?.GetValue(value) ?? value.GetType().GetField(name, all)?.GetValue(value);
void Set(object value, string name, object? content)
{
    if (value.GetType().GetProperty(name, all) is { } property) property.SetValue(value, content);
    else value.GetType().GetField(name, all)!.SetValue(value, content);
}
object? Call(object value, string name, params object?[] values) => value.GetType().GetMethods(all)
    .Single(m => m.Name == name && m.GetParameters().Length == values.Length).Invoke(value, values);
void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
void Near(double expected, object? actual, string message) => Expect(Math.Abs(Convert.ToDouble(actual) - expected) < 0.000001, $"{message}: expected={expected}, actual={actual}");
var failures = new List<string>();
int passed = 0;
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS: " + name); }
    catch (Exception e)
    {
        while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
        failures.Add(name + ": " + e.Message);
        Console.WriteLine("FAIL: " + name + ": " + e);
    }
}
Console.WriteLine($"Plugin SHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath)))}; parallel-first={parallelFirst}");
var rulesetType = P("EnhancedAuthRuleset");
var harmonyType = plugin.GetType("HarmonyLib.Harmony", true)!;
string MethodKey(MethodBase method) => method.Module.ModuleVersionId + ":" + method.MetadataToken;
List<(string Key, string Description, Type? PatchClass)> Inventory()
{
    var rows = new List<(string, string, Type?)>();
    var targets = (IEnumerable)harmonyType.GetMethod("GetAllPatchedMethods", all)!.Invoke(null, null)!;
    foreach (MethodBase target in targets)
    {
        object info = harmonyType.GetMethod("GetPatchInfo", all)!.Invoke(null, new object[] { target })!;
        foreach (string kind in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
        foreach (object patch in (IEnumerable)Get(info, kind)!)
        {
            string patchOwner = (string)Get(patch, "owner")!;
            if (patchOwner != owner) continue;
            var method = (MethodInfo)Get(patch, "PatchMethod")!;
            rows.Add(($"{MethodKey(target)}:{kind}:{patchOwner}:{MethodKey(method)}", $"{target.DeclaringType?.Name}.{target.Name}/{kind}/{method.DeclaringType?.Name}.{method.Name}", method.DeclaringType));
        }
    }
    return rows;
}
void UniqueInventory(string stage)
{
    var rows = Inventory();
    var repeated = rows.GroupBy(row => row.Key).Where(group => group.Count() != 1).ToArray();
    Console.WriteLine($"Inventory {stage}: registrations={rows.Count}, unique-method-target-pairs={rows.Select(row => row.Key).Distinct().Count()}");
    Expect(rows.Count > 0, "Full PatchAll installed no patches");
    Expect(repeated.Length == 0, string.Join("; ", repeated.Take(8).Select(group => group.First().Description + " count=" + group.Count())));
    var classes = plugin.GetTypes().Where(type => type.CustomAttributes.Any(attr => attr.AttributeType.FullName == "HarmonyLib.HarmonyPatch"));
    var missing = classes.Where(type => !rows.Any(row => row.PatchClass == type)).ToArray();
    Expect(missing.Length == 0, "Missing patch classes: " + string.Join(", ", missing.Select(type => type.Name)));
}
void Construct() => _ = Activator.CreateInstance(rulesetType);
void ParallelConstruct()
{
    ThreadPool.GetMinThreads(out int workers, out int io);
    ThreadPool.SetMinThreads(Math.Max(workers, 24), io);
    using var ready = new CountdownEvent(20);
    using var start = new ManualResetEventSlim();
    var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
    {
        ready.Signal();
        start.Wait();
        Construct();
    })).ToArray();
    Expect(ready.Wait(TimeSpan.FromSeconds(15)), "Parallel constructors did not become ready");
    start.Set();
    Task.WaitAll(tasks);
}
Check(parallelFirst ? "20 simultaneous FIRST constructors" : "first real constructor", () =>
{
    if (parallelFirst) ParallelConstruct(); else Construct();
});
Check("all patch classes present exactly once after initialization", () => UniqueInventory("initial"));
string[] initial = Inventory().Select(row => row.Key).Order().ToArray();
Check("20 sequential real constructors", () => { for (int i = 0; i < 20; i++) Construct(); });
Check("inventory unchanged after sequential constructors", () =>
{
    UniqueInventory("sequential");
    Expect(initial.SequenceEqual(Inventory().Select(row => row.Key).Order()), "Harmony inventory changed");
});
Check("20 concurrent real constructors", ParallelConstruct);
Check("inventory unchanged after concurrent constructors", () =>
{
    UniqueInventory("concurrent");
    Expect(initial.SequenceEqual(Inventory().Select(row => row.Key).Order()), "Harmony inventory changed");
});

var config = Activator.CreateInstance(P("Configuration.EnhancedRulesetConfig"))!;
var manager = P("Configuration.GlobalConfigManager");
manager.GetField("instance", all)!.SetValue(null, config);
Set(config, "ApiUrl", "https://soms.invalid");
Set(config, "DisableServerExtensions", false);
var rates = P("Patches.MultiplayerSpectatorRates").GetField("Rates", all)!.GetValue(null)!;
void Rate(object clock, double value) => Set(Call(rates, "GetOrCreateValue", clock)!, "Value", value);
foreach (double sourceRate in new[] { 1d, 1.3 })
    Check($"real patched spectator clock side effects; source={sourceRate}", () =>
    {
        var master = Activator.CreateInstance(F("Timing.ManualFramedClock"))!;
        Set(master, "Rate", sourceRate);
        Rate(master, sourceRate);
        foreach (double playerRate in new[] { 1d, 1.3 })
        {
            var clock = Activator.CreateInstance(G("Screens.OnlinePlay.Multiplayer.Spectate.SpectatorPlayerClock"), master)!;
            Rate(clock, playerRate);
            Near(playerRate, Get(clock, "Rate"), "Spectator getter multiplied rate more than once");
            Set(clock, "IsCatchingUp", true);
            Near(playerRate * 2, Get(clock, "Rate"), "Catch-up multiplier changed");
        }
    });

// Pure in-memory stand-ins: no RealmRulesetStore, client GameHost, config file,
// APIAccess constructor or outgoing request is created/started here.
var fakeGame = RuntimeHelpers.GetUninitializedObject(G("OsuGame"));
manager.GetField("gameBase", all)!.SetValue(null, fakeGame);
var hashCache = RuntimeHelpers.GetUninitializedObject(P("RulesetHashCache"));
Set(hashCache, "onlineIdToName", new Dictionary<int, string> { [42] = "test-ruleset" });
Set(hashCache, "RulesetsHashes", new Dictionary<string, string> { ["test-ruleset"] = "test-only-ruleset-hash" });
manager.GetField("hashCache", all)!.SetValue(null, hashCache);
var fakeApi = RuntimeHelpers.GetUninitializedObject(G("Online.API.APIAccess"));
var endpoints = Activator.CreateInstance(G("Online.EndpointConfiguration"))!;
Set(endpoints, "APIUrl", "https://soms.invalid");
Set(fakeApi, "<Endpoints>k__BackingField", endpoints);
var beatmap = Activator.CreateInstance(G("Beatmaps.BeatmapInfo"), new object?[3])!;
Set(beatmap, "MD5Hash", "test-only-beatmap-hash");
foreach (string kind in new[] { "Solo.CreateSoloScoreRequest", "Rooms.CreateRoomScoreRequest" })
    Check("real patched " + kind + " adds ruleset_hash once", () =>
    {
        object[] parameters = kind.StartsWith("Solo") ? new object[] { beatmap, 42, "test-version-hash" }
            : new object[] { 1L, 1L, beatmap, 42, "test-version-hash" };
        var request = Activator.CreateInstance(G("Online." + kind), parameters)!;
        G("Online.API.APIRequest").GetField("API", all)!.SetValue(request, fakeApi);
        using var web = (IDisposable)Call(request, "CreateWebRequest")!;
        var forms = (IEnumerable)F("IO.Network.WebRequest").GetField("formParameters", all)!.GetValue(web)!;
        int count = forms.Cast<object>().Count(pair => (string)Get(pair, "Item1")! == "ruleset_hash");
        Expect(count == 1, "ruleset_hash count=" + count);
    });
Set(config, "ApiUrl", "https://osu.ppy.sh");
Check("official spectator clock remains unchanged", () =>
{
    var master = Activator.CreateInstance(F("Timing.ManualFramedClock"))!;
    Set(master, "Rate", 1d);
    var clock = Activator.CreateInstance(G("Screens.OnlinePlay.Multiplayer.Spectate.SpectatorPlayerClock"), master)!;
    Rate(clock, 1.3);
    Near(1, Get(clock, "Rate"), "Private rate override leaked to official client");
});
Console.WriteLine($"RESULT: passed={passed}, failed={failures.Count}; user Realm/storage/network untouched.");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;
