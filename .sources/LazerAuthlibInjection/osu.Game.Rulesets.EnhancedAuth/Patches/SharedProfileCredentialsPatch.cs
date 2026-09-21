#nullable enable

using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Online;
using osu.Game.Online.API;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

/// <summary>
/// Keeps official and private OAuth credentials separate while both launches use
/// the same osu! data directory. Private credentials are stored by Windows
/// Credential Manager and never written to the shared game.ini.
/// </summary>
internal static class SharedProfileCredentials
{
    private const string credential_target_environment_variable = "PRIVATE_OSU_CREDENTIAL_TARGET";

    private static readonly object sync = new();
    // Resolve by name at runtime. The public osu.Game NuGet package can lag
    // behind the installed client, and enum numeric values may move meanwhile.
    private static readonly OsuSetting usernameSetting = Enum.Parse<OsuSetting>("Username");
    private static readonly OsuSetting tokenSetting = Enum.Parse<OsuSetting>("Token");
    private static OsuConfigManager? activeConfig;
    private static string officialUsername = string.Empty;
    private static string officialToken = string.Empty;

    public static bool Active => OperatingSystem.IsWindows()
                                 && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(credential_target_environment_variable));

    public static void ApplyPrivateCredentials(OsuConfigManager config)
    {
        if (!Active)
            return;

        lock (sync)
        {
            if (ReferenceEquals(activeConfig, config))
                return;

            officialUsername = config.Get<string>(usernameSetting);
            officialToken = config.Get<string>(tokenSetting);
            activeConfig = config;

            StoredCredentials stored = load();
            config.SetValue(usernameSetting, stored.Username ?? string.Empty);
            config.SetValue(tokenSetting, stored.Token ?? string.Empty);
        }
    }

    public static bool IsActiveConfig(object manager)
        => Active && activeConfig != null && ReferenceEquals(activeConfig, manager);

    /// <summary>
    /// Implements IniConfigManager.PerformSave for a private launch. Every
    /// ordinary setting is written from the live shared config, while the two
    /// authentication fields are replaced with the official values captured
    /// before APIAccess saw the private account.
    /// </summary>
    public static bool PerformProtectedSave(IniConfigManager<OsuSetting> manager)
    {
        if (!IsActiveConfig(manager) || activeConfig == null)
            throw new InvalidOperationException("Protected save was invoked for the wrong config manager.");

        lock (sync)
        {
            save(new StoredCredentials
            {
                Username = activeConfig.Get<string>(usernameSetting),
                Token = activeConfig.Get<string>(tokenSetting),
            });

            try
            {
                var storageField = AccessTools.Field(typeof(IniConfigManager<OsuSetting>), "storage");
                var configStoreField = AccessTools.Field(typeof(ConfigManager<OsuSetting>), "ConfigStore");
                var storage = (Storage?)storageField.GetValue(manager)
                              ?? throw new InvalidOperationException("Could not access the osu! configuration storage.");
                var configStore = (Dictionary<OsuSetting, IBindable>?)configStoreField.GetValue(manager)
                                  ?? throw new InvalidOperationException("Could not access the osu! configuration values.");

                using var stream = storage.CreateFileSafely("game.ini");
                using var writer = new StreamWriter(stream);

                foreach (var pair in configStore)
                {
                    string value = pair.Key.Equals(usernameSetting)
                        ? officialUsername
                        : pair.Key.Equals(tokenSetting)
                            ? officialToken
                            : pair.Value.ToString(null, CultureInfo.InvariantCulture);

                    writer.WriteLine("{0} = {1}", pair.Key,
                        (value ?? string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty));
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[EnhancedAuth] Protected config save failed: {ex.Message}", level: LogLevel.Error);
                return false;
            }
        }
    }

    private static string getTargetName()
    {
        string target = Environment.GetEnvironmentVariable(credential_target_environment_variable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException($"{credential_target_environment_variable} is empty.");
        return target;
    }

    private static StoredCredentials load()
    {
        try
        {
            if (!NativeCredentialManager.TryRead(getTargetName(), out byte[] blob))
                return new StoredCredentials();

            return JsonConvert.DeserializeObject<StoredCredentials>(Encoding.UTF8.GetString(blob))
                   ?? new StoredCredentials();
        }
        catch (Exception ex)
        {
            Logger.Log($"[EnhancedAuth] Private credential read failed: {ex.Message}", level: LogLevel.Error);
            return new StoredCredentials();
        }
    }

    private static void save(StoredCredentials credentials)
    {
        try
        {
            byte[] blob = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(credentials));
            NativeCredentialManager.Write(getTargetName(), credentials.Username ?? string.Empty, blob);
        }
        catch (Exception ex)
        {
            Logger.Log($"[EnhancedAuth] Private credential write failed: {ex.Message}", level: LogLevel.Error);
        }
    }

    private sealed class StoredCredentials
    {
        public string? Username { get; set; }
        public string? Token { get; set; }
    }
}

[HarmonyPatch]
internal static class SharedProfileApiAccessConstructorPatch
{
    private static MethodBase TargetMethod() => AccessTools.Constructor(
        typeof(APIAccess),
        new[] { typeof(OsuGameBase), typeof(OsuConfigManager), typeof(EndpointConfiguration), typeof(string) });

    private static void Prefix(OsuConfigManager config)
        => SharedProfileCredentials.ApplyPrivateCredentials(config);
}

[HarmonyPatch(typeof(IniConfigManager<OsuSetting>), "PerformSave")]
internal static class SharedProfileConfigSavePatch
{
    private static bool Prefix(object __instance, ref bool __result)
    {
        // Generic method bodies are shared by the runtime, so Harmony may route
        // saves for other IniConfigManager<T> instances through this patch too.
        if (!SharedProfileCredentials.IsActiveConfig(__instance))
            return true;

        __result = SharedProfileCredentials.PerformProtectedSave((IniConfigManager<OsuSetting>)__instance);
        return false;
    }
}

internal static class NativeCredentialManager
{
    private const int cred_type_generic = 1;
    private const int cred_persist_local_machine = 2;
    private const int error_not_found = 1168;

    public static bool TryRead(string targetName, out byte[] blob)
    {
        if (!CredRead(targetName, cred_type_generic, 0, out IntPtr credentialPointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == error_not_found)
            {
                blob = Array.Empty<byte>();
                return false;
            }

            throw new Win32Exception(error);
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            blob = new byte[credential.CredentialBlobSize];
            if (blob.Length > 0)
                Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return true;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public static void Write(string targetName, string username, byte[] blob)
    {
        IntPtr blobPointer = IntPtr.Zero;
        try
        {
            if (blob.Length > 0)
            {
                blobPointer = Marshal.AllocHGlobal(blob.Length);
                Marshal.Copy(blob, 0, blobPointer, blob.Length);
            }

            var credential = new NativeCredential
            {
                Type = cred_type_generic,
                TargetName = targetName,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPointer,
                Persist = cred_persist_local_machine,
                UserName = username,
            };

            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (blobPointer != IntPtr.Zero)
                Marshal.FreeHGlobal(blobPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential credential, int flags);

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
