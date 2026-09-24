using System.Reflection;
using Newtonsoft.Json;
using osu.Framework.Bindables;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth;
using osu.Game.Rulesets.EnhancedAuth.Configuration;

internal static class BeatmapRequestChecks
{
    internal static void Run(string fixture)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        typeof(GlobalConfigManager).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new EnhancedRulesetConfig { ApiUrl = "https://beatmap-test.invalid" });
        _ = new EnhancedAuthRuleset();
        using var overlay = new BeatmapSetOverlay();
        using var api = new DummyAPIAccess { HandleRequest = _ => true };
        typeof(BeatmapSetOverlay).GetProperty("api", flags)!.SetValue(overlay, api);
        // Exercise request ownership without loading native image/metadata views
        // (their async loaders require a GameHost).
        var state = new Bindable<APIBeatmapSet>();
        typeof(BeatmapSetOverlay).GetField("beatmapSet", flags)!.SetValue(overlay, state);
        var patch = typeof(EnhancedAuthRuleset).Assembly.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.SomsBeatmapLoadingPatch")!;
        var registry = patch.GetField("states", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object FetchState()
        {
            object?[] args = { overlay, null };
            registry.GetType().GetMethod("TryGetValue")!.Invoke(registry, args);
            return args[1]!;
        }
        GetBeatmapSetRequest Pending() => (GetBeatmapSetRequest)FetchState().GetType().GetField("request", flags)!.GetValue(FetchState())!;
        void Success(GetBeatmapSetRequest req, APIBeatmapSet value) => ((Delegate)typeof(APIRequest<APIBeatmapSet>).GetField("Success", flags)!.GetValue(req)!).DynamicInvoke(value);
        void Failure(GetBeatmapSetRequest req) => ((Delegate)typeof(APIRequest).GetField("Failure", flags)!.GetValue(req)!).DynamicInvoke(new IOException("Simulated transport failure"));
        void Require(bool value, string text) { if (!value) throw new Exception(text); }
        APIBeatmapSet Response(int id)
        {
            var response = JsonConvert.DeserializeObject<APIBeatmapSet>(File.ReadAllText(fixture))!;
            response.OnlineID = id;
            return response;
        }
        overlay.FetchAndShowBeatmapSet(100);
        var stale = Pending();
        overlay.FetchAndShowBeatmapSet(200);
        var current = Pending();
        Success(stale, Response(100));
        Require(state.Value == null, "Old map callback must not replace the newly requested map");
        Success(current, Response(200));
        Require(state.Value?.OnlineID == 200, "Latest map response must populate the overlay");
        overlay.FetchAndShowBeatmapSet(300);
        var failed = Pending();
        Failure(failed);
        Require(Pending() != failed && Pending().ID == 300, "A transient failure must retry the same map exactly once");
        var retried = Pending();
        Failure(retried);
        var retry = FetchState().GetType().GetField("retry", flags)!.GetValue(FetchState())!;
        Require((float)retry.GetType().GetProperty("Alpha")!.GetValue(retry)! == 1, "Second failure must expose the retry button");
        ((Action)retry.GetType().GetProperty("Action")!.GetValue(retry)!)();
        Success(Pending(), Response(300));
        Require(state.Value?.OnlineID == 300, "Manual retry must recover the page");
        overlay.FetchAndShowBeatmapSet(400);
        var closing = Pending();
        overlay.Hide();
        Success(closing, Response(400));
        Require(state.Value == null, "Closing the overlay must invalidate pending callbacks");
        overlay.FetchAndShowBeatmapSet(450);
        var beforeDirect = Pending();
        overlay.ShowBeatmapSet(Response(451));
        Success(beforeDirect, Response(450));
        Require(state.Value?.OnlineID == 451, "An already populated map must invalidate older requests too");
        overlay.FetchAndShowBeatmapSet(500);
        var disposed = Pending();
        overlay.Dispose();
        Success(disposed, Response(500));
        Require(state.Value == null, "Disposed overlay must ignore late completion");
        Console.WriteLine("PASS real beatmap JSON deserialization, stale completion, one automatic retry, manual recovery, close and disposal");
    }
}
