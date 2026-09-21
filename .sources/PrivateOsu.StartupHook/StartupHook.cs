using System.Diagnostics;
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

        void Log(string text)
        {
            File.AppendAllText(
                logPath,
                text + Environment.NewLine
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

            string osuDirectory = Path.GetDirectoryName(
                Environment.ProcessPath!
            )!;

            Log($"osu directory = {osuDirectory}");

            // Explicitly load the assemblies from the running osu! client.
            LoadClientAssembly("osu.Game.dll");
            LoadClientAssembly("osu.Framework.dll");

            // Resolve dependencies requested by EnhancedAuth against
            // assemblies shipped with the running osu! client.
            AssemblyLoadContext.Default.Resolving += ResolveClientAssembly;

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

            AssemblyLoadContext.Default.Resolving -= ResolveClientAssembly;

            return;

            Assembly LoadClientAssembly(string fileName)
            {
                string path = Path.Combine(osuDirectory, fileName);

                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        $"Client assembly not found: {path}",
                        path
                    );

                AssemblyName requestedName =
                    AssemblyName.GetAssemblyName(path);

                Assembly? alreadyLoaded =
                    AssemblyLoadContext.Default.Assemblies
                        .FirstOrDefault(a =>
                            a.GetName().Name == requestedName.Name);

                if (alreadyLoaded != null)
                {
                    Log(
                        $"Already loaded: {alreadyLoaded.FullName}"
                    );

                    return alreadyLoaded;
                }

                Assembly loaded =
                    AssemblyLoadContext.Default
                        .LoadFromAssemblyPath(path);

                Log($"Loaded client assembly: {loaded.FullName}");

                return loaded;
            }

            Assembly? ResolveClientAssembly(
                AssemblyLoadContext context,
                AssemblyName assemblyName)
            {
                // Only intercept osu!/osu.Framework assemblies.
                if (string.IsNullOrEmpty(assemblyName.Name) ||
                    !assemblyName.Name.StartsWith("osu.",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string candidate =
                    Path.Combine(
                        osuDirectory,
                        assemblyName.Name + ".dll"
                    );

                if (!File.Exists(candidate))
                {
                    Log(
                        $"Resolver: not found {assemblyName} at {candidate}"
                    );

                    return null;
                }

                Assembly actual =
                    context.LoadFromAssemblyPath(candidate);

                Log(
                    $"Resolver: {assemblyName.FullName} -> {actual.FullName}"
                );

                return actual;
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR:{Environment.NewLine}{ex}");
            throw;
        }
    }
}