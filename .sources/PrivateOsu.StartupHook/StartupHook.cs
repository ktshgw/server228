using System.Reflection;
using System.Runtime.Loader;

internal static class StartupHook
{
    public static void Initialize()
    {
        string? pluginPath = Environment.GetEnvironmentVariable("PRIVATE_OSU_ENHANCED_AUTH_PATH");
        if (string.IsNullOrWhiteSpace(pluginPath))
            throw new InvalidOperationException("PRIVATE_OSU_ENHANCED_AUTH_PATH is missing.");

        pluginPath = Path.GetFullPath(pluginPath);
        if (!File.Exists(pluginPath))
            throw new FileNotFoundException("EnhancedAuth assembly was not found.", pluginPath);

        Assembly plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
        Type rulesetType = plugin.GetType(
            "osu.Game.Rulesets.EnhancedAuth.EnhancedAuthRuleset",
            throwOnError: true)!;

        _ = Activator.CreateInstance(rulesetType)
            ?? throw new InvalidOperationException("EnhancedAuth could not be initialised.");
    }
}
