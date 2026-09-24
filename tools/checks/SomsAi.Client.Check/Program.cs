using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

try
{
if (args.Length < 2) throw new ArgumentException("client-directory merged-plugin.dll");
string clientPath = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(clientPath, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Game.dll"));
var framework = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Framework.dll"));
var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
Type G(string name) => game.GetType("osu.Game." + name, true)!;
Type F(string name) => framework.GetType("osu.Framework." + name, true)!;
Type P(string name) => plugin.GetType("osu.Game.Rulesets.EnhancedAuth." + name, true)!;
MemberInfo Member(Type type, string name)
{
    for (Type? current = type; current != null; current = current.BaseType)
        if (current.GetMember(name, all | BindingFlags.DeclaredOnly).FirstOrDefault() is { } member) return member;
    throw new MissingMemberException(type.FullName, name);
}
object? Get(object value, string name) => Member(value.GetType(), name) switch
{
    PropertyInfo property => property.GetValue(value),
    FieldInfo field => field.GetValue(value),
    _ => throw new Exception(name),
};
void Set(object value, string name, object? content)
{
    switch (Member(value.GetType(), name))
    {
        case PropertyInfo property: property.SetValue(value, content); break;
        case FieldInfo field: field.SetValue(value, content); break;
    }
}
object? Call(object owner, string name, params object?[] values) => ((MethodInfo)Member(owner.GetType(), name)).Invoke(owner, values);
IEnumerable<object> Children(object owner) => ((IEnumerable)Get(owner, "Children")!).Cast<object>();
IEnumerable<object> Descendants(object owner)
{
    yield return owner;
    if (!F("Graphics.Containers.CompositeDrawable").IsInstanceOfType(owner)) yield break;
    if (F("Graphics.Containers.GridContainer").IsInstanceOfType(owner) && Get(owner, "Content") is IEnumerable rows)
    {
        foreach (IEnumerable row in rows)
        foreach (object child in row)
        foreach (object item in Descendants(child)) yield return item;
        yield break;
    }
    foreach (var child in ((IEnumerable)Get(owner, "InternalChildren")!).Cast<object>())
    foreach (var item in Descendants(child)) yield return item;
}
void Expect(bool condition, string reason) { if (!condition) throw new Exception(reason); }
var json = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "Newtonsoft.Json.dll"));
var deserialize = json.GetType("Newtonsoft.Json.JsonConvert")!.GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) })!;
object State(string value) => deserialize.Invoke(null, new object[] { value, P("Online.SomsAiState") })!;
var config = Activator.CreateInstance(P("Configuration.EnhancedRulesetConfig"))!;
Set(config, "ApiUrl", "https://soms.invalid");
P("Configuration.GlobalConfigManager").GetField("instance", all)!.SetValue(null, config);

var localUser = Activator.CreateInstance(G("Online.API.Requests.Responses.APIUser"))!;
Set(localUser, "Id", 7);
Set(localUser, "Username", "ClientCheck");
object Bind(Type type, object value) => Activator.CreateInstance(F("Bindables.Bindable`1").MakeGenericType(type), value)!;
using var api = (IDisposable)Activator.CreateInstance(G("Online.API.DummyAPIAccess"))!;
Set(Get(api, "LocalUser")!, "Value", localUser);
object Clock() => Activator.CreateInstance(F("Timing.FramedClock"), new object[] { Activator.CreateInstance(F("Timing.ManualClock"))!, true })!;
Set(api, "Clock", Clock());
// Keep the installed client's real asynchronous LeaveRoom/scheduler implementation,
// replacing only the transport so the fixture has no hub connection or network.
var clientType = G("Online.Multiplayer.OnlineMultiplayerClient");
var clientConstructor = clientType.GetConstructors().Single();
var fixtureType = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("SomsAiOfflineClientFixture"), AssemblyBuilderAccess.Run)
    .DefineDynamicModule("Fixture").DefineType("OfflineMultiplayerClient", TypeAttributes.Public, clientType);
var fixtureConstructor = fixtureType.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard,
    clientConstructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
var constructorCode = fixtureConstructor.GetILGenerator();
constructorCode.Emit(OpCodes.Ldarg_0);
constructorCode.Emit(OpCodes.Ldarg_1);
constructorCode.Emit(OpCodes.Call, clientConstructor);
constructorCode.Emit(OpCodes.Ret);
var leaveTransport = fixtureType.DefineMethod("LeaveRoomInternal", MethodAttributes.Family | MethodAttributes.Virtual,
    typeof(Task), Type.EmptyTypes);
var leaveCode = leaveTransport.GetILGenerator();
leaveCode.Emit(OpCodes.Call, typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetMethod!);
leaveCode.Emit(OpCodes.Ret);
fixtureType.DefineMethodOverride(leaveTransport, clientType.GetMethod("LeaveRoomInternal", all)!);
using var multiplayer = (IDisposable)Activator.CreateInstance(fixtureType.CreateType()!, Get(api, "Endpoints")!)!;
Set(multiplayer, "API", api);
Set(multiplayer, "Clock", Clock());
var ruleset = Activator.CreateInstance(G("Rulesets.RulesetInfo"))!;
Set(ruleset, "OnlineID", 0);
var dependencies = Activator.CreateInstance(F("Allocation.DependencyContainer"), new object?[] { null })!;
using var dialogs = (IDisposable)Activator.CreateInstance(G("Overlays.DialogOverlay"))!;
using var userLookup = (IDisposable)Activator.CreateInstance(G("Database.UserLookupCache"))!;
var emptyAudioStore = Activator.CreateInstance(F("IO.Stores.ResourceStore`1").MakeGenericType(typeof(byte[])))!;
using var audioManager = (IDisposable)Activator.CreateInstance(
    F("Audio.AudioManager"), Activator.CreateInstance(F("Threading.AudioThread"))!, emptyAudioStore, emptyAudioStore, null)!;
void CacheAs(Type type, object instance) => F("Allocation.DependencyContainer").GetMethods()
    .Single(method => method.Name == "CacheAs" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1)
    .MakeGenericMethod(type).Invoke(dependencies, new[] { instance });
// OsuGame caches the dialog service ONLY by interface, never by DialogOverlay.
CacheAs(G("Overlays.IDialogOverlay"), dialogs);
CacheAs(G("Database.UserLookupCache"), userLookup);
CacheAs(F("Audio.AudioManager"), audioManager);
void ActivateOwnScreen(object screen)
{
    var activator = F("Allocation.DependencyActivator");
    activator.GetMethod("initialiseSourceGeneratedActivators", all)!.Invoke(null, new[] { screen });
    var registered = activator.GetMethod("getActivator", all)!.Invoke(null, new object[] { screen.GetType().Name == "SomsAiMatchScreen" ? P("UI.SomsAiScreen") : screen.GetType() })!;
    foreach (Delegate inject in (IEnumerable)Get(registered, "injectionActivators")!)
        inject.DynamicInvoke(screen, dependencies);
}
object CreateScreen(string type, params object[] constructorArgs)
{
    var screen = Activator.CreateInstance(P("UI." + type), constructorArgs)!;
    Set(screen, "Clock", Clock());
    Set(screen, "Api", api);
    Set(screen, "Client", multiplayer);
    Set(screen, "Ruleset", Bind(ruleset.GetType(), ruleset));
    P("UI.SomsNativeMatchScreen").GetMethod("load", all | BindingFlags.DeclaredOnly)!.Invoke(screen, new object[] { audioManager });
    ActivateOwnScreen(screen);
    return screen;
}

foreach (bool partyOnly in new[] { false, true })
{
    var screen = CreateScreen("SomsAiScreen", partyOnly);
    Expect(ReferenceEquals(Get(screen, "dialogs"), dialogs), "SOMSAI/party must resolve the real game's interface-only dialog registration");
    ((IDisposable)screen).Dispose();
    ((IDisposable)screen).Dispose();
}
using (var screen = (IDisposable)CreateScreen("SomsTeamRankedPlayScreen", 42L))
{
    Expect(ReferenceEquals(Get(screen, "dialogs"), dialogs), "Ranked 2v2 must resolve interface-only dialogs too");
    screen.Dispose();
}
Console.WriteLine("PASS: source-generated dependency activation opens SOMSAI, party and Ranked 2v2 with interface-only dialogs; repeated disposal after partial load is safe.");

// All controls are constructed against the installed client, not the NuGet build version.
using (var screen = (IDisposable)CreateScreen("SomsAiScreen", false))
{
    object textbox = Get(screen, "inviteTarget")!;
    string idle = """
        {"ratings":{"1v1":{"rating":1000,"rank":1,"wins":2,"losses":1},"2v2":{"rating":900}},
         "party":{"id":null,"captain_id":7,"members":[{"id":7,"username":"ClientCheck"}],"invites":[{"id":2,"captain":{"id":8,"username":"Inviter"}}]},
         "queue":null,"match":null,"pools":[{"id":1,"name":"Tournament","best_of":7,"average_stars":5.2}],
         "customs":[{"id":12,"name":"Custom","format":"3v3","participants":1,"capacity":6,"teams":[1,0]}]}
        """;
    for (int i = 0; i < 100; i++) { Set(screen, "state", State(idle)); Call(screen, "render"); }
    Expect(ReferenceEquals(textbox, Get(screen, "inviteTarget")), "Polling replaced the focused invitation textbox");
    string[] PartyButtons() => Descendants(Get(screen, "partyPanel")!).Where(c => G("Graphics.UserInterfaceV2.RoundedButton").IsInstanceOfType(c)).Select(c => Get(c, "Text")!.ToString()!).ToArray();
    Expect(PartyButtons().Length == 3 && PartyButtons().Contains("Принять") && PartyButtons().Contains("Отклонить"), "Party/invitation actions duplicated or lost");
    Expect(Children(Get(screen, "queuePanel")!).Count() == 1, "Search must have one action for the selected format");
    Expect(Descendants(Get(screen, "customPanel")!).Count(c => G("Graphics.UserInterfaceV2.RoundedButton").IsInstanceOfType(c)) == 2, "Custom requires separate joins for both teams");
    Set(Get(textbox, "Current")!, "Value", "  Friend Nick  ");
    Call(screen, "invitePlayer");
    string inviteBody = Get(Get(screen, "actionRequest")!, "body")!.ToString()!;
    Expect(inviteBody.Contains("\"target_username\": \"Friend Nick\"") && !inviteBody.Contains("target_user_id"), "Invitation must send a trimmed nickname");
    Call(Get(screen, "actionRequest")!, "Cancel");
    Set(screen, "actionRequest", null);
    Console.WriteLine("PASS: installed-client native UI; nullable solo party/invites, two team joins, 100 refreshes preserve controls.");
    object emptyTeamState = State("{\"match\":{\"id\":1,\"stage\":\"waiting\",\"teams\":[{\"id\":1,\"captain_id\":null,\"members\":[]}]}}");
    Expect(Get(((IEnumerable)Get(Get(emptyTeamState, "Match")!, "Teams")!).Cast<object>().Single(), "CaptainId") == null, "Empty custom team must allow a null captain");

    using var matchScreen = (IDisposable)CreateScreen("SomsAiMatchScreen", State("{\"match\":{\"id\":42}}"));
    foreach (int size in new[] { 1, 2, 3, 4 })
    foreach (string stage in new[] { "waiting", "pool_select", "banning", "picking", "ready", "playing", "results", "ended", "cancelled" })
    {
        string[] members = Enumerable.Range(0, size * 2).Select(i => $"{{\"id\":{7 + i},\"username\":\"Player{i}\",\"ready\":true}}").ToArray();
        string match = "{\"id\":42,\"owner_id\":7,\"ruleset_id\":0,\"variant_id\":0,\"format\":\"" + size + "v" + size + "\",\"revision\":15,\"ranked\":" + (size <= 2 ? "true" : "false") + ",\"stage\":\"" + stage + "\",\"room_id\":null,\"pool_selected\":true,\"teams\":[{\"id\":0,\"captain_id\":7,\"members\":[" + string.Join(',', members.Take(size)) + "]},{\"id\":1,\"captain_id\":11,\"members\":[" + string.Join(',', members.Skip(size)) + "]}],\"turn_user_id\":7,\"wins\":[2,1],\"best_of\":7,\"slots\":[{\"id\":\"NM1\",\"label\":\"NM1\",\"category\":\"NM\",\"name\":\"Map\",\"difficulty_rating\":5.4,\"beatmap_id\":44,\"status\":\"available\"},{\"id\":\"HD1\",\"category\":\"HD\",\"status\":\"banned\"}],\"history\":[{\"round\":1,\"slot_id\":\"HD2\",\"team_scores\":[500000,400000]}],\"map_slot\":\"NM1\"}";
        if (stage == "ended") match = match[..^1] + ",\"winner_team_id\":0,\"rating_changes\":[{\"user_id\":7,\"before\":1000,\"after\":1032,\"delta\":32,\"impact\":80}]}";
        Set(matchScreen, "state", State("{\"match\":" + match + "}"));
        // No room connection is present in this isolated fixture.
        Call(matchScreen, "render");
        object matchPanel = Get(matchScreen, "matchPanel")!;
        int choices = Descendants(Get(matchScreen, "mapBoard")!)
            .Where(P("UI.SomsAiScreen+MapCard").IsInstanceOfType)
            .Count(child => (bool)Get(child, "CanChoose")!);
        Expect(choices == (stage is "banning" or "picking" ? 1 : 0), $"Only active captain can ban/pick available cards ({stage}: {choices})");
        object slot = ((IEnumerable)Get(Get(Get(matchScreen, "state")!, "Match")!, "Slots")!).Cast<object>().First();
        Expect((string)Get(slot, "Title")! == "Map" && (double)Get(slot, "Stars")! == 5.4, "Pool field aliases were lost");
        string caption = (string)P("UI.SomsAiScreen").GetMethod("SlotCaption", all)!.Invoke(null, new[] { slot })!;
        Expect(!caption.Contains(" — ") && !caption.Contains("[]"), "Full slot title must not show empty artist/version punctuation");
        if (stage == "ended")
        {
            var rows = Children(matchPanel).Where(child => G("Graphics.Sprites.OsuSpriteText").IsInstanceOfType(child) || F("Graphics.Containers.TextFlowContainer").IsInstanceOfType(child))
                .Select(child => F("Graphics.Containers.TextFlowContainer").IsInstanceOfType(child)
                    ? string.Concat(((IEnumerable)Get(child, "parts")!).Cast<object>().Select(part => Get(part, "text")!.ToString()))
                    : Get(child, "Text")!.ToString()!).ToArray();
            Expect(rows.Any(text => text.StartsWith("Победитель:")), "Winner missing from result");
            Expect(rows.Any(text => text.Contains("1000 → 1032 MMR") && text.Contains("+32") && text.Contains("80/100") && text.Contains("(вы)")), "Own rating before/after/delta/impact missing");
        }
    }
    Console.WriteLine("PASS: 1v1/2v2/3v3/4v4 stages render; only captain's available slots offer draft actions; map aliases preserved.");

    Set(screen, "state", State("{\"match\":{\"id\":42,\"revision\":17,\"ruleset_id\":0,\"variant_id\":0,\"stage\":\"picking\"}}"));
    Call(screen, "action", "pick", null, 15, null);
    object pending = Get(screen, "actionRequest")!;
    string body = Get(pending, "body")!.ToString()!;
    Expect(body.Contains("\"expected_revision\": 15") && body.Contains("\"match_id\": 42"), "Draft action must send displayed revision and current match");
    Call(screen, "action", "ready", null, null, null);
    Expect(ReferenceEquals(pending, Get(screen, "actionRequest")), "Concurrent action created duplicate requests");
    Console.WriteLine("PASS: match revision is submitted; double-click does not create concurrent mutations.");
}
Console.WriteLine("PASS: pending API action cancellation on disposal is safe.");
using (var boardScreen = (IDisposable)CreateScreen("SomsAiMatchScreen", State("{\"match\":{\"id\":42}}")))
{
    string matchJson = """
        {"match":{"id":42,"stage":"picking","format":"2v2","room_id":42,"turn_user_id":7,"pool_selected":true,
          "teams":[{"id":0,"members":[{"id":7},{"id":8}]},{"id":1,"members":[{"id":9},{"id":10}]}],
          "slots":[{"id":"NM1","beatmap_id":1,"beatmapset_id":10,"category":"NM","status":"available"}]}}
        """;
    Set(boardScreen, "state", State(matchJson)); Call(boardScreen, "render");
    var card = Descendants(Get(boardScreen, "mapBoard")!).Single(P("UI.SomsAiScreen+MapCard").IsInstanceOfType);
    var preview = Get(card, "preview")!;
    for (int i = 0; i < 100; i++)
    {
        Set(boardScreen, "state", State(matchJson)); Call(boardScreen, "renderMatch");
        Expect(ReferenceEquals(card, Descendants(Get(boardScreen, "mapBoard")!).Single(P("UI.SomsAiScreen+MapCard").IsInstanceOfType)), "Polling must preserve cards and their playing preview");
    }
    var canJoin = P("UI.SomsAiScreen").GetMethod("CanJoinNativeMatch", all)!;
    var match = Get(Get(boardScreen, "state")!, "Match")!;
    Expect((bool)canJoin.Invoke(null, new[] { match })!, "Full roster must join the native room");
    var teams = ((IEnumerable)Get(match, "Teams")!).Cast<object>().ToArray();
    ((IList)Get(teams[1], "Members")!).RemoveAt(1);
    Expect(!(bool)canJoin.Invoke(null, new[] { match })!, "Incomplete custom must not cause a native join error loop");
    Call(boardScreen, "stopPreviews");
    Expect(Get(preview, "BeatmapSet") == null, "Suspension must invalidate even a pending audio preview");
    Call(boardScreen, "renderMatch");
    Expect(Get(preview, "BeatmapSet") != null, "Returning to the match must restore previews without replacing cards");
    Console.WriteLine("PASS: 100 board updates preserve preview controls; suspension cancels previews; incomplete customs defer native joins.");
}
using (var voteScreen = (IDisposable)CreateScreen("SomsAiMatchScreen", State("{\"match\":{\"id\":42}}")))
{
    Set(voteScreen, "state", State("""
        {"match":{"id":42,"revision":7,"stage":"pool_select","pool_selected":false,"target_mmr":1500,
          "teams":[{"id":0,"captain_id":7,"members":[{"id":7,"username":"ClientCheck"}]}],
          "pool_candidates":[{"id":8,"name":"A"},{"id":9,"name":"B"}],"pool_votes":{"0":8},"slots":[{"id":"NM1"}]}}
        """));
    Call(voteScreen, "render");
    var captions = Descendants(Get(voteScreen, "matchPanel")!).Where(c => G("Graphics.UserInterfaceV2.RoundedButton").IsInstanceOfType(c)).Select(c => Get(c, "Text")!.ToString()!).ToArray();
    Expect(captions.Contains("✓  Ваш выбор") && captions.Contains("Выбрать турнир") && !captions.Any(c => c.Contains("NM1")), "Vote choices must render before map draft");
    Call(voteScreen, "action", "pool_vote", null, null, null);
    string voteBody = Get(Get(voteScreen, "actionRequest")!, "body")!.ToString()!;
    Expect(voteBody.Contains("\"match_id\": 42") && !voteBody.Contains("expected_revision"), "Concurrent captain votes must not race on revisions");
    State("{\"match\":{\"target_mmr\":null}}");
}
using (var customScreen = (IDisposable)CreateScreen("SomsAiScreen", false))
{
    Set(Get(Get(customScreen, "customRankBand")!, "Current")!, "Value", "GOLD");
    Set(Get(Get(customScreen, "customRankDivision")!, "Current")!, "Value", "III");
    Call(customScreen, "createCustom", "4v4");
    string customBody = Get(Get(customScreen, "actionRequest")!, "body")!.ToString()!;
    Expect(customBody.Contains("\"target_rank\": \"GOLD III\"") && customBody.Contains("\"format\": \"4v4\"") && !customBody.Contains("pool_id"), "Custom form must request automatic pool selection by rank");
}
Console.WriteLine("PASS: captain pool votes, automatic custom rank form, and older matches without rank metadata.");

string? originalProfile = Environment.GetEnvironmentVariable("PRIVATE_OSU_TEST_PROFILE_DIR");
try
{
    var profileHelper = P("Patches.SomsTestProfile");
    string profileRoot = Path.Combine(Path.GetTempPath(), "SOMS", "checks", "SomsAi.Client.Check", Guid.NewGuid().ToString("N"));
    var pipeNames = new List<string>();
    Environment.SetEnvironmentVariable("PRIVATE_OSU_TEST_PROFILE_DIR", null);
    string sharedStorage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
    var redirectStorage = profileHelper.GetMethod("RedirectSharedStorage", all)!;
    Expect((string)redirectStorage.Invoke(null, new object[] { sharedStorage })! == sharedStorage, "Normal auxiliary storage must not be redirected");
    var storageHarmonyType = plugin.GetType("HarmonyLib.Harmony", true)!;
    var storageHarmony = Activator.CreateInstance(storageHarmonyType, "soms.test.auxiliary-storage")!;
    var storageProcessor = storageHarmonyType.GetMethod("CreateClassProcessor")!.Invoke(storageHarmony, new object[] { P("Patches.SomsTestAuxiliaryStoragePatch") })!;
    storageProcessor.GetType().GetMethod("Patch")!.Invoke(storageProcessor, null);
    object options = Activator.CreateInstance(F("HostOptions"))!;
    Set(options, "IPCPipeName", "osu-lazer-original");
    object?[] hostArguments = { "osu", options };
    var patchHost = P("Patches.SomsTestHostPatch").GetMethod("Prefix", all)!;
    patchHost.Invoke(null, hostArguments);
    Expect((string)hostArguments[0]! == "osu" && (string)Get(options, "IPCPipeName")! == "osu-lazer-original", "Normal client's host and IPC must remain unchanged");
    foreach (int index in Enumerable.Range(1, 8))
    {
        string path = Path.Combine(profileRoot, "profile-" + index);
        Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable("PRIVATE_OSU_TEST_PROFILE_DIR", path);
        bool refused = false;
        try { profileHelper.GetProperty("DirectoryPath", all)!.GetValue(null); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { refused = true; }
        Expect(refused, "An unmarked profile must be rejected before storage access");
        File.WriteAllText(Path.Combine(path, ".soms-test-profile"), "SOMS-TEST-PROFILE-1");
        object?[] isolated = { "osu", null };
        patchHost.Invoke(null, isolated);
        pipeNames.Add((string)Get(isolated[1]!, "IPCPipeName")!);
        Expect((string)profileHelper.GetProperty("DirectoryPath", all)!.GetValue(null)! == path, "Test directory must remain fixed");
        object auxiliaryStorage = Activator.CreateInstance(F("Platform.DesktopStorage"), new object?[] { sharedStorage, null })!;
        string resolvedStorage = (string)Call(auxiliaryStorage, "GetFullPath", "AuthNative.dll", false)!;
        Expect(resolvedStorage == Path.Combine(path, "AuthNative.dll"), "Real native-auth storage construction must be isolated per test profile");
        string separate = Path.Combine(path, "cache");
        Expect((string)redirectStorage.Invoke(null, new object[] { separate })! == separate, "An already isolated auxiliary folder must stay unchanged");
    }
    Expect(pipeNames.Distinct().Count() == 8, "All eight test profiles require different IPC pipes");
    var provider = F("Platform.NamedPipeIpcProvider");
    using var first = (IDisposable)Activator.CreateInstance(provider, pipeNames[0])!;
    using var second = (IDisposable)Activator.CreateInstance(provider, pipeNames[1])!;
    using var duplicate = (IDisposable)Activator.CreateInstance(provider, pipeNames[0])!;
    Expect((bool)Call(first, "Bind")! && (bool)Call(second, "Bind")!, "Two test profile IPC servers must coexist");
    Expect(!(bool)Call(duplicate, "Bind")!, "Reopening the same test profile must be refused");
}
finally { Environment.SetEnvironmentVariable("PRIVATE_OSU_TEST_PROFILE_DIR", originalProfile); }
Console.WriteLine("PASS: eight isolated test profiles and native-auth stores, concurrent named pipes, duplicate rejection and normal-launch passthrough.");
using (var readyScreen = (IDisposable)CreateScreen("SomsAiScreen", false))
{
    Set(readyScreen, "state", State("{\"match\":{\"id\":42,\"revision\":17,\"ruleset_id\":0,\"variant_id\":0,\"stage\":\"ready\"}}"));
    Call(readyScreen, "action", "ready", null, null, null);
    string requestBody = Get(Get(readyScreen, "actionRequest")!, "body")!.ToString()!;
    Expect(!requestBody.Contains("expected_revision"), "Concurrent readiness must not reject other players on a draft revision race");
}
Console.WriteLine("PASS: concurrent readiness is idempotent and does not carry a stale draft revision.");
using (var replayQueueScreen = (IDisposable)CreateScreen("SomsAiScreen", false))
{
    Set(replayQueueScreen, "state", State("{\"match\":{\"id\":42,\"room_id\":42,\"stage\":\"ended\"}}"));
    Set(multiplayer, "room", Activator.CreateInstance(G("Online.Multiplayer.MultiplayerRoom"), 42L));
    Call(replayQueueScreen, "action", "queue_join", null, null, null);
    Expect(Get(replayQueueScreen, "actionRequest") == null && (bool)Get(replayQueueScreen, "preparingAction")!, "Queue started before leaving the finished native room");
    var stopAt = DateTime.UtcNow.AddSeconds(2);
    while (Get(replayQueueScreen, "actionRequest") == null && DateTime.UtcNow < stopAt)
    {
        Call(Get(multiplayer, "Scheduler")!, "Update");
        Call(Get(replayQueueScreen, "Scheduler")!, "Update");
        await Task.Delay(5);
    }
    Expect(Get(multiplayer, "Room") == null && Get(replayQueueScreen, "actionRequest") != null, "Queue must start after completed native leave");
}
Console.WriteLine("PASS: requeue waits for the old native room to leave; final result includes winner and own MMR/impact.");

using (var teamScreen = (IDisposable)CreateScreen("SomsTeamRankedPlayScreen", 42L))
{
    object hand = Get(teamScreen, "hand")!;
    Expect(hand.GetType() == G("Screens.OnlinePlay.Matchmaking.RankedPlay.Hand.PlayerHandOfCards"), "Team screen must use native interactive card hand");
    Expect(Get(teamScreen, "Title")!.ToString() == "Ranked 2v2", "Team screen title");
    object room = Activator.CreateInstance(G("Online.Multiplayer.MultiplayerRoom"), 42L)!;
    object roomState = Activator.CreateInstance(G("Online.Multiplayer.MatchTypes.RankedPlay.RankedPlayRoomState"))!;
    Set(roomState, "Stage", Enum.Parse(G("Online.Multiplayer.MatchTypes.RankedPlay.RankedPlayStage"), "CardDiscard"));
    var roomUsers = (IDictionary)Get(roomState, "Users")!;
    for (int userId = 7; userId < 11; userId++)
    {
        var info = Activator.CreateInstance(G("Online.Multiplayer.MatchTypes.RankedPlay.RankedPlayUserInfo"))!;
        Set(info, "Rating", 1000);
        Set(info, "Life", 2000000);
        var cards = (IList)Get(info, "Hand")!;
        for (int card = 0; card < 5; card++) cards.Add(Activator.CreateInstance(G("Online.Multiplayer.MatchTypes.RankedPlay.RankedPlayCardItem"))!);
        roomUsers.Add(userId, info);
    }
    Set(room, "MatchState", roomState);
    Set(multiplayer, "room", room);
    Set(teamScreen, "teamState", deserialize.Invoke(null, new object[] { "{\"teams\":[{\"id\":0,\"user_ids\":[7,9],\"life\":2000000},{\"id\":1,\"user_ids\":[8,10],\"life\":2000000}],\"turn_order\":[7,8,9,10],\"winning_team_id\":null,\"cancelled\":false}", json.GetType("Newtonsoft.Json.Linq.JObject")! }));
    Call(teamScreen, "roomUpdated");
    var initialCards = ((IEnumerable)Get(hand, "Cards")!).Cast<object>().ToArray();
    for (int i = 0; i < 100; i++) Call(teamScreen, "roomUpdated");
    var currentCards = ((IEnumerable)Get(hand, "Cards")!).Cast<object>().ToArray();
    Expect(initialCards.Length == 5 && initialCards.SequenceEqual(currentCards), "Repeated room updates must keep exactly the local five card instances");
    Expect(Get(hand, "SelectionMode")!.ToString() == "Multiple", "Discard stage must allow multiple selection");
    Set(roomState, "Stage", Enum.Parse(G("Online.Multiplayer.MatchTypes.RankedPlay.RankedPlayStage"), "CardPlay"));
    Set(roomState, "ActiveUserId", 8);
    Call(teamScreen, "roomUpdated");
    Expect(Get(hand, "SelectionMode")!.ToString() == "Disabled", "Foreign turn must disable local selection");
    Set(roomState, "ActiveUserId", 7);
    Call(teamScreen, "roomUpdated");
    Expect(Get(hand, "SelectionMode")!.ToString() == "Single", "Own turn must allow one card");
    Set(multiplayer, "room", null);
}
Console.WriteLine("PASS: native Ranked 2v2 hand; four users, only own five cards, 100 updates preserve instances, own/foreign turn selection enforced.");

using (var menu = (IDisposable)Activator.CreateInstance(G("Screens.Menu.ButtonSystem"))!)
{
    object list = Get(menu, "buttonsMulti")!;
    object area = Get(menu, "buttonArea")!;
    var postfix = P("Patches.SomsAiMultiplayerMenuPatch").GetMethod("Postfix", all)!;
    for (int i = 0; i < 100; i++) postfix.Invoke(null, new[] { menu, list, area });
    Expect(((IList)list).Count == 1, "SOMSAI native menu button duplicated");
    Expect(Children(area).Count(button => (string)Get(button, "Name")! == "somsai-menu-button") == 1, "ButtonArea contains duplicated SOMSAI");
}
Console.WriteLine("PASS: 100 native multiplayer menu callbacks create exactly one SOMSAI button.");

using (var queue = (IDisposable)Activator.CreateInstance(G("Screens.OnlinePlay.Matchmaking.Queue.ScreenQueue"), Enum.Parse(G("Online.Matchmaking.MatchmakingPoolType"), "RankedPlay"))!)
using (var content = (IDisposable)Activator.CreateInstance(F("Graphics.Containers.Container"))!)
{
    object flow = Activator.CreateInstance(F("Graphics.Containers.FillFlowContainer"))!;
    var nativeSelector = Activator.CreateInstance(G("Screens.OnlinePlay.Matchmaking.Queue.PoolSelector"))!;
    var nativeStart = Activator.CreateInstance(G("Screens.OnlinePlay.Matchmaking.Queue.ScreenQueue+BeginQueueingButton"))!;
    var nativeHint = Activator.CreateInstance(G("Graphics.Containers.LinkFlowContainer"), new object?[] { null })!;
    Call(flow, "Add", nativeSelector);
    Call(flow, "Add", nativeStart);
    Call(flow, "Add", nativeHint);
    Set(content, "Child", flow);
    var postfix = P("Patches.SomsTeamRankedScreenPatch").GetMethod("Postfix", all)!;
    object idle = Enum.Parse(G("Screens.OnlinePlay.Matchmaking.Queue.ScreenQueue+MatchmakingScreenState"), "Idle");
    var selected = Get(queue, "selectedPool")!;
    var connected = Activator.CreateInstance(F("Bindables.Bindable`1").MakeGenericType(typeof(bool)), new object[] { false })!;
    Set(queue, "isConnected", connected);
    Set(connected, "Value", true);
    Set(Get(nativeStart, "Enabled")!, "BindTarget", connected);
    Set(Get(nativeStart, "SelectedPool")!, "BindTarget", selected);
    Set(nativeStart, "Action", (Action)(() => throw new Exception("Disabled search was called")));
    for (int i = 0; i < 100; i++)
    {
        postfix.Invoke(null, new object?[] { queue, idle, content });
        Set(selected, "Value", Activator.CreateInstance(G("Online.Matchmaking.MatchmakingPool")));
        Set(connected, "Value", i % 2 == 0);
        Expect(ReferenceEquals(Children(content).Single(), flow), "Keep the standard native queue layout");
        Expect(ReferenceEquals(Children(flow).First(), nativeSelector), "Keep the native pool selector");
        Expect(!(bool)Get(Get(nativeStart, "Enabled")!, "Value")!, "Search cannot re-enable on reconnect/selection");
        Expect(Get(Get(nativeStart, "SelectedPool")!, "Value") == null, "LoadComplete must see no actionable pool");
        Expect(Get(nativeStart, "Action") == null, "Enter and direct click must have no queue action");
    }
    Expect(Get(nativeStart, "Text")!.ToString() == "Иди в обычный лазер", "Exact disabled caption");
    Expect(Children(flow).Count() == 3, "Do not add custom Ranked or party controls");
}
Console.WriteLine("PASS: native Ranked layout retained; search disabled through 100 selector/reconnect cycles; Enter has no action.");
Console.WriteLine("RESULT: native constructor/DTO/navigation lifecycle checks passed; no game host, Realm, user files or network were accessed.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
