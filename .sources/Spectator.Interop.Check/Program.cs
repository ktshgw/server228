using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

// Run inside the spectator compose environment. Only the pool/beatmap read paths
// are called; no hubs, users, rooms, sessions or score writes are instantiated.
string assemblyPath = Path.GetFullPath(args.Length > 0 ? args[0] : "/app/osu.Server.Spectator.dll");
var runtime = new SpectatorRuntime(Path.GetDirectoryName(assemblyPath)!);
var spectator = runtime.LoadFromAssemblyPath(assemblyPath);
Type Type(string suffix) => spectator.GetType("osu.Server.Spectator." + suffix, true)!;
object? Get(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value);
async Task<object?> Invoke(object instance, string method, params object[] parameters)
{
    var task = (Task)instance.GetType().GetMethod(method)!.Invoke(instance, parameters)!;
    await task.WaitAsync(TimeSpan.FromSeconds(40));
    return task.GetType().GetProperty("Result")?.GetValue(task);
}
void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
var failures = new List<string>();
int passed = 0;
async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS: " + name); }
    catch (Exception exception)
    {
        while (exception is TargetInvocationException && exception.InnerException != null) exception = exception.InnerException;
        string detail = exception.GetType().Name;
        // Public DTO schema diagnostics are useful; never dump request headers,
        // environment variables, connection strings or arbitrary response bodies.
        if (exception is InvalidOperationException) detail += ": " + exception.Message;
        if (exception.GetType().GetProperty("Path") is { } jsonPath) detail += " at JSON path " + jsonPath.GetValue(exception);
        if (exception.GetType().GetProperty("StatusCode") is { } status) detail += " HTTP " + status.GetValue(exception);
        failures.Add(name + ": " + detail);
        Console.WriteLine("FAIL: " + name + ": " + detail);
    }
}
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
// The pinned server carries Microsoft.Extensions.* 10 while its host framework
// is .NET 8. Resolve its own types instead of forcing this check's framework copy.
var logging = runtime.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(assemblyPath)!, "Microsoft.Extensions.Logging.Abstractions.dll"));
var caching = runtime.LoadFromAssemblyName(new AssemblyName("Microsoft.Extensions.Caching.Memory"));
object Singleton(System.Type type) => type.GetProperty("Instance")?.GetValue(null) ?? type.GetField("Instance")!.GetValue(null)!;
var loggerFactory = Singleton(logging.GetType("Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory", true)!);
using var cache = (IDisposable)Activator.CreateInstance(caching.GetType("Microsoft.Extensions.Caching.Memory.MemoryCache", true)!,
    Activator.CreateInstance(caching.GetType("Microsoft.Extensions.Caching.Memory.MemoryCacheOptions", true)!)!)!;
object shared = Activator.CreateInstance(Type("Services.SharedInterop"), http, loggerFactory)!;
var rulesetManagerType = Type("Services.RulesetManager");
var nullLoggerType = logging.GetType("Microsoft.Extensions.Logging.Abstractions.NullLogger`1", true)!.MakeGenericType(rulesetManagerType);
var manager = Activator.CreateInstance(rulesetManagerType, Singleton(nullLoggerType), cache, shared)!;
var database = Activator.CreateInstance(Type("Database.DatabaseAccess"), loggerFactory, shared, manager)!;
object[] pools = [];

await Check("SOMSAI signed read uses actual spectator transport", async () =>
{
    object client = Activator.CreateInstance(Type("Services.SomsaiInteropClient"))!;
    object result = (await Invoke(client, "GetRoom", 0L))!;
    Require(Get(result, "Managed") is false, "The sentinel room must not be managed");
});

await Check("Party reservation JSON preserves nullable party and UUID", () =>
{
    var json = runtime.LoadFromAssemblyName(new AssemblyName("Newtonsoft.Json"));
    var deserialize = json.GetType("Newtonsoft.Json.JsonConvert", true)!.GetMethod("DeserializeObject", [typeof(string), typeof(System.Type)])!;
    const string id = "8248a23b-a253-4452-9219-4a731e4d5bb9";
    var party = deserialize.Invoke(null,
        ["{\"id\":null,\"captain_id\":42,\"members\":[42,43],\"reservation_id\":\"" + id + "\"}", Type("Services.RankedPartyReservation")])!;
    Require(Get(party, "Id") == null, "Solo party ID must stay nullable");
    Require(Convert.ToInt32(Get(party, "CaptainId")) == 42, "Captain was not decoded");
    Require(((IEnumerable)Get(party, "Members")!).Cast<int>().SequenceEqual([42, 43]), "Party roster changed");
    Require((string)Get(party, "ReservationId")! == id, "Reservation UUID changed");
    return Task.CompletedTask;
});

if (args.Length > 1 && int.TryParse(args[1], out int statusUserId))
    await Check("SharedInterop.GetRankedDodgeStatusAsync live read", async () =>
    {
        object status = (await Invoke(shared, "GetRankedDodgeStatusAsync", statusUserId))!;
        Require(Convert.ToInt32(Get(status, "UserId")) == statusUserId, "Dodge status returned another user");
        Require(Convert.ToInt32(Get(status, "Level")) is >= 0 and <= 14, "Invalid dodge progression level");
        Require(Get(status, "AccountBanned") is bool, "Account ban flag was not decoded");
    });

await Check("SharedInterop.GetMatchmakingPoolsAsync JSON and client pool conversion", async () =>
{
    pools = ((IEnumerable)(await Invoke(shared, "GetMatchmakingPoolsAsync"))!).Cast<object>().ToArray();
    Require(pools.Length > 0, "No active Ranked pool is currently available");
    foreach (var pool in pools)
    {
        Require((bool)Get(pool, "active")!, "An inactive pool was returned");
        var clientPool = pool.GetType().GetMethod("ToMatchmakingPool")!.Invoke(pool, null)!;
        Require(Convert.ToInt32(Get(clientPool, "Id")) == Convert.ToInt32(Get(pool, "id")), "Pool ID changed during conversion");
        Require(Get(clientPool, "Type")!.ToString() == "RankedPlay", "Pool type was not decoded as RankedPlay");
        Console.WriteLine($"  pool={Get(pool, "id")} ruleset={Get(pool, "ruleset_id")} variant={Get(pool, "variant_id")} type={Get(clientPool, "Type")}");
    }
});

foreach (var (ruleset, variant) in new[] { (0, 0), (1, 0), (2, 0), (3, 4), (3, 7) })
    await Check($"SharedInterop.GetMatchmakingBeatmapsAsync({ruleset},{variant}) JSON", async () =>
    {
        uint poolId = Convert.ToUInt32(pools.FirstOrDefault(pool => Convert.ToInt32(Get(pool, "ruleset_id")) == ruleset
            && Convert.ToInt32(Get(pool, "variant_id")) == variant) is { } matchingPool ? Get(matchingPool, "id") : 0);
        var maps = ((IEnumerable)(await Invoke(shared, "GetMatchmakingBeatmapsAsync", ruleset, variant, poolId))!).Cast<object>().ToArray();
        foreach (var map in maps)
        {
            Require(Convert.ToInt32(Get(map, "beatmap_id")) > 0, "Decoded beatmap ID is missing");
            Require(Convert.ToInt32(Get(map, "playmode")) == ruleset, "Beatmap ruleset changed in JSON conversion");
            Require(Get(map, "checksum") is string { Length: 32 }, "Beatmap checksum is missing or malformed");
            Require(Convert.ToDouble(Get(map, "difficulty_rating")) >= 0, "Beatmap difficulty is invalid");
        }
        if (pools.Any(pool => Convert.ToInt32(Get(pool, "ruleset_id")) == ruleset && Convert.ToInt32(Get(pool, "variant_id")) == variant))
            Require(maps.Length >= 10, "An advertised Ranked pool has fewer than ten playable beatmaps");
        Console.WriteLine($"  ruleset={ruleset} variant={variant} beatmaps={maps.Length}");
    });

await Check("DatabaseAccess.GetActiveMatchmakingPoolsAsync matches SharedInterop", async () =>
{
    var active = ((IEnumerable)(await Invoke(database, "GetActiveMatchmakingPoolsAsync"))!).Cast<object>().ToArray();
    Require(active.Select(pool => Convert.ToUInt32(Get(pool, "id"))).Order().SequenceEqual(pools.Select(pool => Convert.ToUInt32(Get(pool, "id"))).Order()),
        "Database and SharedInterop disagree on active pool IDs");
    foreach (var pool in active) pool.GetType().GetMethod("ToMatchmakingPool")!.Invoke(pool, null);
});

foreach (var pool in pools)
    await Check($"DatabaseAccess.GetMatchmakingPoolAsync({Get(pool, "id")}) SQL mapping", async () =>
    {
        var persisted = await Invoke(database, "GetMatchmakingPoolAsync", Convert.ToUInt32(Get(pool, "id")));
        Require(persisted != null && (bool)Get(persisted, "active")!, "Advertised pool cannot be loaded as active from SQL");
        var clientPool = persisted!.GetType().GetMethod("ToMatchmakingPool")!.Invoke(persisted, null)!;
        Require(Get(clientPool, "Type")!.ToString() == "RankedPlay", "SQL pool enum mapping disagrees with interop JSON");
    });

foreach (var pool in pools.Where(pool => Convert.ToInt32(Get(pool, "lobby_size")) == 4))
    await Check($"Ranked 2v2 pool {Get(pool, "id")} has twenty distinct playable cards", async () =>
    {
        int ruleset = Convert.ToInt32(Get(pool, "ruleset_id"));
        int variant = Convert.ToInt32(Get(pool, "variant_id"));
        var maps = ((IEnumerable)(await Invoke(shared, "GetMatchmakingBeatmapsAsync", ruleset, variant, Convert.ToUInt32(Get(pool, "id"))))!).Cast<object>().ToArray();
        Require(maps.Select(map => Convert.ToInt32(Get(map, "beatmap_id"))).Distinct().Count() >= 20, "The advertised 2v2 pool cannot deal four private hands");
    });

if (args.Length >= 3)
{
    int userId = int.Parse(args[1]);
    int targetId = int.Parse(args[2]);
    await Check("Duel relationship SQL maps local friend/block columns", async () =>
    {
        var relation = await Invoke(database, "GetUserRelation", userId, targetId);
        if (relation != null)
        {
            Require(Convert.ToInt32(Get(relation, "user_id")) == userId, "Relation owner changed");
            Require(Convert.ToInt32(Get(relation, "zebra_id")) == targetId, "Relation target changed");
            Require((bool)Get(relation, "friend")! || (bool)Get(relation, "foe")!, "Relation type was lost");
        }
        Require(await Invoke(database, "GetUserAllowsPMs", targetId) is bool, "PM preference failed to decode");
        var friends = ((IEnumerable)(await Invoke(database, "GetUserFriendsAsync", userId))!).Cast<int>().ToArray();
        if (relation != null && (bool)Get(relation, "friend")!) Require(friends.Contains(targetId), "Friend missing from local friend list");
    });
}

(database as IDisposable)?.Dispose();
Console.WriteLine($"Spectator/app boundary: {passed} passed, {failures.Count} failed.");
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

sealed class SpectatorRuntime(string directory) : AssemblyLoadContext("actual-spectator-runtime")
{
    protected override Assembly? Load(AssemblyName name)
    {
        string candidate = Path.Combine(directory, name.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}
