using System.Reflection;
using System.Runtime.Loader;

string path = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string dependency = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
    return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
var settings = assembly.GetType("osu.Server.Spectator.AppSettings", true)!;
bool saveReplays = (bool)settings.GetProperty("SaveReplays")!.GetValue(null)!;
Console.WriteLine($"SAVE_REPLAYS={Environment.GetEnvironmentVariable("SAVE_REPLAYS")}; effective SaveReplays={saveReplays}");
Console.WriteLine($"ReplayUploaderConcurrency={settings.GetProperty("ReplayUploaderConcurrency")!.GetValue(null)}");
if (args.Length > 1 && saveReplays != bool.Parse(args[1]))
    throw new Exception("Unexpected replay storage setting");
