using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SomsLauncher
{
    // Shared by the downloadable switcher and the workspace's PowerShell launchers.
    public static class LazerProcessGuard
    {
        public static bool IsLazerExecutable(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            if (IsLazerProduct(version.ProductName) || IsLazerProduct(version.FileDescription))
                return true;

            // Also recognise portable/development lazer installations. Stable has
            // the same exe name, but not these .NET client assemblies and config.
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            return File.Exists(Path.Combine(directory, "osu.Game.dll"))
                && File.Exists(Path.Combine(directory, "osu.Framework.dll"))
                && File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json"));
        }

        private static bool IsLazerProduct(string product)
        {
            return !string.IsNullOrEmpty(product)
                && product.IndexOf("osu!", StringComparison.OrdinalIgnoreCase) >= 0
                && product.IndexOf("lazer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int[] FindRunning()
        {
            List<int> running = new List<int>();
            Process[] candidates = Process.GetProcessesByName("osu!");
            try
            {
                foreach (Process process in candidates)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        if (IsLazerExecutable(GetExecutablePath(process.Id)))
                            running.Add(process.Id);
                    }
                    catch (Win32Exception)
                    {
                        if (HasExited(process)) continue;
                        throw new InvalidOperationException("Не удалось определить тип запущенного osu! (PID " + process.Id + ").");
                    }
                    catch (InvalidOperationException)
                    {
                        if (HasExited(process)) continue;
                        throw;
                    }
                }
            }
            finally
            {
                foreach (Process process in candidates) process.Dispose();
            }
            return running.ToArray();
        }

        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }

        private static string GetExecutablePath(int processId)
        {
            // Limited-query access also works across 32/64-bit processes and for
            // elevated games; enumerating MainModule unnecessarily requires more.
            IntPtr handle = OpenProcess(0x1000, false, processId);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                StringBuilder path = new StringBuilder(32768);
                int length = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return path.ToString();
            }
            finally { CloseHandle(handle); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
