using System.Reflection;
using System.Runtime.Loader;

internal static class StartupHook
{
    public static void Initialize()
    {
        string logPath = Path.Combine(
            Path.GetTempPath(),
            "soms-startup-hook.txt"
        );

        void Log(string message)
        {
            File.AppendAllText(
                logPath,
                message + Environment.NewLine
            );
        }

        try
        {
            Log($"HOOK EXECUTED {DateTime.Now:O}");

            string? pluginPath =
                Environment.GetEnvironmentVariable(
                    "PRIVATE_OSU_ENHANCED_AUTH_PATH"
                );

            Log($"PRIVATE_OSU_ENHANCED_AUTH_PATH = {pluginPath}");

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

            string clientDirectory = Path.GetDirectoryName(
                Environment.ProcessPath!
            )!;

            string pluginDirectory = Path.GetDirectoryName(
                pluginPath
            )!;

            Log($"Client directory = {clientDirectory}");
            Log($"Plugin directory = {pluginDirectory}");

            AssemblyLoadContext.Default.Resolving +=
                ResolveAssembly;

            Log($"Loading: {pluginPath}");

            Assembly plugin =
                AssemblyLoadContext.Default.LoadFromAssemblyPath(
                    pluginPath
                );

            Log($"Assembly loaded: {plugin.FullName}");

            Type rulesetType = plugin.GetType(
                "osu.Game.Rulesets.EnhancedAuth.EnhancedAuthRuleset",
                throwOnError: true
            )!;

            Log($"Type found: {rulesetType.FullName}");

            _ = Activator.CreateInstance(rulesetType)
                ?? throw new InvalidOperationException(
                    "EnhancedAuth could not be initialised."
                );

            Log("EnhancedAuth initialized successfully");

            Assembly? ResolveAssembly(
                AssemblyLoadContext context,
                AssemblyName assemblyName)
            {
                if (string.IsNullOrEmpty(assemblyName.Name))
                    return null;

                Log(
                    $"Resolving: {assemblyName.FullName}"
                );

                // osu! assemblies MUST come from the running
                // osu! client, not from the EnhancedAuth build folder.
                if (assemblyName.Name.StartsWith(
                        "osu.",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string clientPath = Path.Combine(
                        clientDirectory,
                        assemblyName.Name + ".dll"
                    );

                    if (File.Exists(clientPath))
                    {
                        Assembly? alreadyLoaded =
                            context.Assemblies.FirstOrDefault(
                                a => string.Equals(
                                    a.GetName().Name,
                                    assemblyName.Name,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            );

                        if (alreadyLoaded != null)
                        {
                            Log(
                                $"Using already loaded client assembly: {alreadyLoaded.FullName}"
                            );

                            return alreadyLoaded;
                        }

                        Assembly loaded =
                            context.LoadFromAssemblyPath(
                                clientPath
                            );

                        Log(
                            $"Loaded client assembly: {loaded.FullName}"
                        );

                        return loaded;
                    }
                }

                // Non-osu dependencies such as Harmony can come
                // from the EnhancedAuth build directory.
                string pluginPathCandidate = Path.Combine(
                    pluginDirectory,
                    assemblyName.Name + ".dll"
                );

                if (File.Exists(pluginPathCandidate))
                {
                    Assembly? alreadyLoaded =
                        context.Assemblies.FirstOrDefault(
                            a => string.Equals(
                                a.GetName().Name,
                                assemblyName.Name,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );

                    if (alreadyLoaded != null)
                    {
                        Log(
                            $"Using already loaded plugin dependency: {alreadyLoaded.FullName}"
                        );

                        return alreadyLoaded;
                    }

                    Assembly loaded =
                        context.LoadFromAssemblyPath(
                            pluginPathCandidate
                        );

                    Log(
                        $"Loaded plugin dependency: {loaded.FullName}"
                    );

                    return loaded;
                }

                Log(
                    $"Assembly not found: {assemblyName.FullName}"
                );

                return null;
            }
        }
        catch (Exception ex)
        {
            Log(
                $"ERROR:{Environment.NewLine}{ex}"
            );

            throw;
        }
    }
}