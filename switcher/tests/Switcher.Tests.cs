using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class SwitcherTests
{
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type guard = assembly.GetType("SomsLauncher.LazerProcessGuard", true);
            Func<string, bool> isLazer = path => (bool)guard.GetMethod("IsLazerExecutable").Invoke(null, new object[] { path });
            int[] running = (int[])guard.GetMethod("FindRunning").Invoke(null, null);

            string stable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!", "osu!.exe");
            string lazer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osulazer", "current", "osu!.exe");
            Check(File.Exists(stable) && !isLazer(stable), "installed stable is ignored");
            Check(File.Exists(lazer) && isLazer(lazer), "installed lazer is recognised");
            Check(!isLazer(null) && !isLazer(Path.Combine(output, "missing.exe")), "absent paths are ignored");

            string fixture = Path.Combine(output, "portable-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            string portable = Path.Combine(fixture, "osu!.exe");
            File.Copy(assembly.Location, portable);
            Check(!isLazer(portable), "exe name alone does not identify lazer");
            File.WriteAllBytes(Path.Combine(fixture, "osu.Game.dll"), new byte[0]);
            Check(!isLazer(portable), "one stray assembly does not identify lazer");
            File.WriteAllBytes(Path.Combine(fixture, "osu.Framework.dll"), new byte[0]);
            File.WriteAllText(Path.ChangeExtension(portable, ".runtimeconfig.json"), "{}");
            Check(isLazer(portable), "portable lazer recognised independently of installation path");

            int stableCount = 0, lazerCount = 0;
            foreach (Process process in Process.GetProcessesByName("osu!"))
            {
                using (process)
                {
                    string path = process.MainModule.FileName;
                    if (string.Equals(path, stable, StringComparison.OrdinalIgnoreCase))
                    {
                        Check(!running.Contains(process.Id), "running stable PID excluded");
                        stableCount++;
                    }
                    if (string.Equals(path, lazer, StringComparison.OrdinalIgnoreCase))
                    {
                        Check(running.Contains(process.Id), "running lazer PID included");
                        lazerCount++;
                    }
                }
            }
            Console.WriteLine("Checked running clients: stable=" + stableCount + ", lazer=" + lazerCount);
            using (Icon icon = Icon.ExtractAssociatedIcon(Path.GetFullPath(args[0])))
            using (Bitmap bitmap = icon.ToBitmap())
            {
                bool somsColour = false;
                for (int x = 0; x < bitmap.Width; x++)
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    somsColour |= pixel.R > pixel.G + 30 && pixel.B > pixel.G + 20;
                }
                Check(somsColour, "SOMS icon is embedded in the executable's Windows resource");
            }

            Type formType = assembly.GetType("SomsSwitcher.MainForm", true);
            using (Form form = (Form)Activator.CreateInstance(formType, true))
            {
                Check(!form.Controls.OfType<Button>().Any(b => b.Text.Contains("Bancho")), "Bancho button removed");
                Check(formType.GetMethod("StartOfficial", BindingFlags.Instance | BindingFlags.NonPublic) == null, "official launch handler removed");
                Button play = form.Controls.OfType<Button>().Single(b => b.Text == "Играть на SOMS!");
                Button refresh = form.Controls.OfType<Button>().Single(b => b.Text == "Обновить");
                Check(refresh.Left - play.Right == 12, "play button occupies freed space");
                MethodInfo assert = formType.GetMethod("AssertNoLazerRunning", BindingFlags.Instance | BindingFlags.NonPublic);
                try { assert.Invoke(form, null); Check(lazerCount == 0, "stable alone does not block launching lazer"); }
                catch (TargetInvocationException e)
                {
                    Check(e.InnerException is InvalidOperationException && e.InnerException.Message.Contains("osu!lazer уже запущен"), "duplicate lazer launch still blocked with precise message");
                }
                try
                {
                    formType.GetMethod("VerifyOfficialExecutable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { stable });
                    throw new Exception("Stable must not be selected as the lazer executable");
                }
                catch (TargetInvocationException e)
                {
                    Check(e.InnerException is InvalidDataException && e.InnerException.Message.Contains("а не osu!stable"), "selecting stable as lazer is rejected clearly");
                }
                using (Bitmap screenshot = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(screenshot, new Rectangle(0, 0, form.Width, form.Height));
                    screenshot.Save(Path.Combine(output, "switcher.png"));
                }
            }
            Console.WriteLine("PASS " + checks + " switcher checks; no game started or stopped");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool passed, string label)
    {
        if (!passed) throw new Exception(label);
        checks++;
        Console.WriteLine("PASS " + label);
    }
}
