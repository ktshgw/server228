using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

string clientPath = Path.GetFullPath(args[0]);
string pluginPath = Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(clientPath, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Game.dll"));
if (args[1] == "--inspect")
{
    foreach (var method in game.GetType("osu.Game.Users.Drawables.DrawableAvatar", true)!.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Where(m => m.Name == "load"))
        Console.WriteLine(method + " / " + string.Join(", ", method.GetParameters().Select(p => p.Name + ":" + p.ParameterType.FullName)));
    foreach (var ctor in game.GetType("osu.Game.Scoring.ScoreInfo", true)!.GetConstructors())
        Console.WriteLine(ctor);
    return;
}
var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
var framework = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(clientPath, "osu.Framework.dll"));
var configType = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Configuration.EnhancedRulesetConfig", true)!;
var manager = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Configuration.GlobalConfigManager", true)!;
var config = Activator.CreateInstance(configType)!;
manager.GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, config);
configType.GetProperty("ApiUrl")!.SetValue(config, "https://soms.invalid/");

var harmonyType = plugin.GetType("HarmonyLib.Harmony", true)!;
var harmony = Activator.CreateInstance(harmonyType, "soms.replay-avatar.check")!;
foreach (string name in new[] { "ReplayUserPatch", "SomsAvatarPatch" })
{
    var patch = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Patches." + name, true)!;
    var processor = harmonyType.GetMethod("CreateClassProcessor")!.Invoke(harmony, new object[] { patch })!;
    processor.GetType().GetMethod("Patch")!.Invoke(processor, null);
}

int checks = 0;
void check(bool condition, string reason)
{
    if (!condition) throw new Exception(reason);
    checks++;
}

var scoreType = game.GetType("osu.Game.Scoring.ScoreInfo", true)!;
var score = Activator.CreateInstance(scoreType, new object?[] { null, null, null })!;
var userProperty = scoreType.GetProperty("User")!;
var cachedUser = userProperty.GetValue(score)!;
var userType = cachedUser.GetType();
var userId = userType.GetProperty("Id")!;
var avatarUrl = userType.GetField("AvatarUrl")!;
var realmUser = scoreType.GetProperty("RealmUser")!.GetValue(score)!;
realmUser.GetType().GetProperty("OnlineID")!.SetValue(realmUser, 1_500_000_001);
var resolvedUser = userProperty.GetValue(score)!;
check((int)userId.GetValue(resolvedUser)! == 1_500_000_001, "Replay's cached guest ID was not updated");

string uploadedAvatar = "https://soms.invalid/files/avatars/content-hash.png";
avatarUrl.SetValue(resolvedUser, uploadedAvatar);
check((string?)avatarUrl.GetValue(userProperty.GetValue(score)) == uploadedAvatar, "Known avatar was discarded");

var avatarPatch = plugin.GetType("osu.Game.Rulesets.EnhancedAuth.Patches.SomsAvatarPatch", true)!;
var resolve = avatarPatch.GetMethod("ResolveAvatarUrl", BindingFlags.Static | BindingFlags.NonPublic)!;
string? resolveAvatar(object? user) => (string?)resolve.Invoke(null, new[] { user });
check(resolveAvatar(null) == null, "Guest avatar should be offline");
check(resolveAvatar(resolvedUser) == uploadedAvatar, "Uploaded avatar was not preserved");
avatarUrl.SetValue(resolvedUser, null);
check(resolveAvatar(resolvedUser) == "https://soms.invalid/users/1500000001/avatar", "Local replay used official avatar lookup");
avatarUrl.SetValue(resolvedUser, "/site/soms-default-avatar.png");
check(resolveAvatar(resolvedUser) == "https://soms.invalid/site/soms-default-avatar.png", "Relative avatar URL was not resolved");
foreach (var defaultUrl in new[] { "https://lazer.g0v0.top/default.jpg", "https://lazer-data.g0v0.top/default.jpg" })
{
    avatarUrl.SetValue(resolvedUser, defaultUrl);
    check(resolveAvatar(resolvedUser) == null, "Legacy default avatar was not replaced by embedded SOMS asset");
}

var avatarPrefix = avatarPatch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (var (url, nonG0V0) in new[] { ("https://osu.ppy.sh", false), ("https://dev.ppy.sh", false), ("https://soms.invalid", true) })
{
    configType.GetProperty("ApiUrl")!.SetValue(config, url);
    configType.GetProperty("NonG0V0Server")!.SetValue(config, nonG0V0);
    userId.SetValue(resolvedUser, 1);
    check((int)userId.GetValue(userProperty.GetValue(score))! == 1, "Replay patch affected another server");
    check((bool)avatarPrefix.Invoke(null, new object?[3])!, "Avatar patch affected another server");
}

using var embedded = plugin.GetManifestResourceStream("osu.Game.Rulesets.EnhancedAuth.Resources.soms-default-avatar.png")!;
check(embedded != null, "Default avatar resource missing from merged plugin");
check(Convert.ToHexString(SHA256.HashData(embedded!)) == "F723FD152F4998B7CA956CE6137CE70FFB8F981AC259D8B7679B6C9DDDA38AF2", "Embedded avatar differs from site's asset");
var resourceType = framework.GetType("osu.Framework.IO.Stores.DllResourceStore", true)!;
var loaderType = framework.GetType("osu.Framework.Graphics.Textures.TextureLoaderStore", true)!;
using var resources = (IDisposable)Activator.CreateInstance(resourceType, plugin)!;
using var loader = (IDisposable)Activator.CreateInstance(loaderType, resources)!;
using var upload = (IDisposable?)loaderType.GetMethod("Get")!.Invoke(loader, new object[] { "Resources/soms-default-avatar" });
check(upload != null, "Default avatar cannot be decoded by the installed framework");
check((int)upload!.GetType().GetProperty("Width")!.GetValue(upload)! == 512, "Unexpected avatar texture width");
if (args.Length > 2)
{
    using var replay = new BinaryReader(File.OpenRead(args[2]));
    string readOsuString() => replay.ReadByte() == 0x0b ? replay.ReadString() : "";
    replay.ReadByte();
    check(replay.ReadInt32() >= 30_000_001, "Replay has no lazer metadata");
    readOsuString();
    readOsuString();
    readOsuString();
    replay.ReadBytes(6 * 2 + 4 + 2 + 1 + 4);
    readOsuString();
    replay.ReadInt64();
    replay.ReadBytes(replay.ReadInt32());
    replay.ReadInt64();
    byte[] metadata = replay.ReadBytes(replay.ReadInt32());
    var decoderType = game.GetType("osu.Game.Scoring.Legacy.LegacyScoreDecoder", true)!;
    object decoder = RuntimeHelpers.GetUninitializedObject(game.GetType("osu.Game.Scoring.Legacy.DatabasedLegacyScoreDecoder", true)!);
    string? json = null;
    decoderType.GetMethod("readCompressedData", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
        decoder, new object[] { metadata, (Action<StreamReader>)(r => json = r.ReadToEnd()) });
    using var decoded = JsonDocument.Parse(json!);
    check(decoded.RootElement.GetProperty("user_id").GetInt32() > 1, "Installed replay decoder lost user identity");
    check(decoded.RootElement.GetProperty("online_id").GetInt64() > 0, "Installed replay decoder lost online score identity");
}
Console.WriteLine($"PASS: {checks} replay identity, avatar routing and embedded texture checks against installed lazer assemblies.");
