using System.Reflection;
using System.Runtime.Loader;

internal static class StartupHook
{
    public static void Initialize()
    {
        string? pluginPath =
            Environment.GetEnvironmentVariable(
                "PRIVATE_OSU_ENHANCED_AUTH_PATH"
            );

        if (string.IsNullOrWhiteSpace(pluginPath))
            throw new InvalidOperationException(
                "PRIVATE_OSU_ENHANCED_AUTH_PATH is missing."
            );

        pluginPath = Path.GetFullPath(pluginPath);

        if (!File.Exists(pluginPath))
            throw new FileNotFoundException(
                "EnhancedAuth assembly was not found.",
                pluginPath
            );

        string clientDirectory =
            Path.GetDirectoryName(Environment.ProcessPath!)!;

        string pluginDirectory =
            Path.GetDirectoryName(pluginPath)!;

        AssemblyLoadContext.Default.Resolving +=
            (_, assemblyName) =>
            {
                if (string.IsNullOrEmpty(assemblyName.Name))
                    return null;

                // osu! assemblies must come from the running lazer client.
                if (assemblyName.Name.StartsWith(
                        "osu.",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string clientPath = Path.Combine(
                        clientDirectory,
                        assemblyName.Name + ".dll"
                    );

                    if (!File.Exists(clientPath))
                        return null;

                    Assembly? alreadyLoaded =
                        AssemblyLoadContext.Default.Assemblies
                            .FirstOrDefault(
                                a => string.Equals(
                                    a.GetName().Name,
                                    assemblyName.Name,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            );

                    if (alreadyLoaded != null)
                        return alreadyLoaded;

                    return AssemblyLoadContext.Default
                        .LoadFromAssemblyPath(clientPath);
                }

                // Plugin dependencies, e.g. 0Harmony.dll.
                string pluginDependency = Path.Combine(
                    pluginDirectory,
                    assemblyName.Name + ".dll"
                );

                if (!File.Exists(pluginDependency))
                    return null;

                Assembly? alreadyLoadedDependency =
                    AssemblyLoadContext.Default.Assemblies
                        .FirstOrDefault(
                            a => string.Equals(
                                a.GetName().Name,
                                assemblyName.Name,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );

                if (alreadyLoadedDependency != null)
                    return alreadyLoadedDependency;

                return AssemblyLoadContext.Default
                    .LoadFromAssemblyPath(pluginDependency);
            };

        Assembly plugin =
            AssemblyLoadContext.Default.LoadFromAssemblyPath(
                pluginPath
            );

        Type rulesetType = plugin.GetType(
            "osu.Game.Rulesets.EnhancedAuth.EnhancedAuthRuleset",
            throwOnError: true
        )!;

        _ = Activator.CreateInstance(rulesetType)
            ?? throw new InvalidOperationException(
                "EnhancedAuth could not be initialised."
            );
    }
}