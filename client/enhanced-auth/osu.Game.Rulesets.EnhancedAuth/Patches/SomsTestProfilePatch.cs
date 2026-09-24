#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using osu.Framework;
using osu.Framework.Platform;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

/// <summary>Opt-in isolated test profiles. Never redirect storage for ordinary launches.</summary>
internal static class SomsTestProfile
{
    public static string? DirectoryPath
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable("PRIVATE_OSU_TEST_PROFILE_DIR");
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("SOMS! test profile must use an absolute path.");
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string marker = Path.Combine(path, ".soms-test-profile");
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != "SOMS-TEST-PROFILE-1")
                throw new InvalidOperationException("SOMS! test profile marker is missing. Start this profile using START_TEST_CLIENTS.bat.");
            for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("SOMS! test profile cannot use a directory link.");
            return path;
        }
    }

    public static string PipeName(string path) => "soms-test-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24];

    public static string RedirectSharedStorage(string path)
    {
        if (DirectoryPath is not { } profile) return path;
        string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(shared, StringComparison.OrdinalIgnoreCase) ? profile : path;
    }
}

// The installed Auth loader creates its own DesktopStorage for AppData/osu,
// independently of GameHost's and OsuGameBase's default storage factories.
// Keep its native library and updater files inside the same isolated profile.
[HarmonyPatch(typeof(DesktopStorage), MethodType.Constructor, typeof(string), typeof(DesktopGameHost))]
internal static class SomsTestAuxiliaryStoragePatch
{
    private static void Prefix(ref string path) => path = SomsTestProfile.RedirectSharedStorage(path);
}

[HarmonyPatch(typeof(Host), nameof(Host.GetSuitableDesktopHost))]
internal static class SomsTestHostPatch
{
    private static void Prefix(ref string gameName, ref HostOptions? hostOptions)
    {
        if (SomsTestProfile.DirectoryPath is not { } path) return;
        gameName = SomsTestProfile.PipeName(path);
        hostOptions ??= new HostOptions();
        hostOptions.IPCPipeName = gameName;
        hostOptions.PortableInstallation = false;
        hostOptions.FriendlyGameName = "SOMS! · " + Path.GetFileName(path);
    }
}

[HarmonyPatch(typeof(DesktopGameHost), "GetDefaultGameStorage")]
internal static class SomsTestDefaultStoragePatch
{
    private static bool Prefix(DesktopGameHost __instance, ref Storage __result)
    {
        if (SomsTestProfile.DirectoryPath is not { } path) return true;
        __result = __instance.GetStorage(path);
        return false;
    }
}

[HarmonyPatch(typeof(OsuGameBase), "CreateStorage")]
internal static class SomsTestGameStoragePatch
{
    private static bool Prefix(GameHost host, ref Storage __result)
    {
        if (SomsTestProfile.DirectoryPath is not { } path) return true;
        // Do not follow a storage.ini copied from a normal profile into a test profile.
        __result = host.GetStorage(path);
        return false;
    }
}

[HarmonyPatch(typeof(TcpIpcProvider), nameof(TcpIpcProvider.Bind))]
internal static class SomsTestLegacyIpcPatch
{
    private static bool Prefix(TcpIpcProvider __instance, ref bool __result)
    {
        if (SomsTestProfile.DirectoryPath == null || __instance.GetType().FullName != "osu.Desktop.LegacyIpc.LegacyTcpIpcProvider") return true;
        // File imports belong to the normal installation's legacy port. Each test
        // profile still has its own named pipe and accepts drag-and-drop imports.
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(OsuGame), "updateWindowTitle")]
internal static class SomsTestWindowTitlePatch
{
    private static void Postfix(OsuGame __instance)
    {
        if (SomsTestProfile.DirectoryPath is not { } path) return;
        var host = (GameHost?)AccessTools.Property(typeof(osu.Framework.Game), "Host").GetValue(__instance);
        string prefix = "SOMS! · " + Path.GetFileName(path) + " | ";
        if (host?.Window is { } window && !window.Title.StartsWith(prefix, StringComparison.Ordinal))
            window.Title = prefix + window.Title;
    }
}
