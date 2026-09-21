using System.Reflection;
using System.Runtime.Loader;

internal static class StartupHook
{
    public static void Initialize()
    {
        string log = Path.Combine(
            Path.GetTempPath(),
            "soms-startup-hook.txt"
        );

        File.AppendAllText(
            log,
            $"HOOK EXECUTED {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}"
        );

        string? pluginPath = Environment.GetEnvironmentVariable("PRIVATE_OSU_ENHANCED_AUTH_PATH");

        File.AppendAllText(
            log,
            $"PRIVATE_OSU_ENHANCED_AUTH_PATH = {pluginPath}{Environment.NewLine}"
        );

        if (string.IsNullOrWhiteSpace(pluginPath))
            throw new InvalidOperationException("PRIVATE_OSU_ENHANCED_AUTH_PATH is missing.");

        pluginPath = Path.GetFullPath(pluginPath);

        if (!File.Exists(pluginPath))
            throw new FileNotFoundException(
                "EnhancedAuth assembly was not found.",
                pluginPath
            );

        File.AppendAllText(
            log,
            $"Loading: {pluginPath}{Environment.NewLine}"
        );

        Assembly plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);

        Type rulesetType = plugin.GetType(
            "osu.Game.Rulesets.EnhancedAuth.EnhancedAuthRuleset",
            throwOnError: true
        )!;

        _ = Activator.CreateInstance(rulesetType)
            ?? throw new InvalidOperationException(
                "EnhancedAuth could not be initialised."
            );

        File.AppendAllText(
            log,
            $"EnhancedAuth initialized successfully{Environment.NewLine}"
        );
    }
}