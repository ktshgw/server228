using System.Reflection;
using System.Runtime.Loader;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Online.Matchmaking;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Screens;
using osuTK;
using SixLabors.ImageSharp;

internal static class Program
{
    public static int Main(string[] args) => QuietTestProcess.Run(() => MainImpl(args));
    private static int MainImpl(string[] args)
    {
        // Load every game/framework dependency from the installed client, not NuGet.
        string client = Path.GetFullPath(args[0]);
        string plugin = Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = name.Name == "osu.Game.Rulesets.EnhancedAuth" ? plugin : Path.Combine(client, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        string output = Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--output="))?[9..] ?? ".test-tmp/somsai-ocean-check");
        Directory.CreateDirectory(output);
        return Run(args.Contains("--visual"), output);
    }

    private static int Run(bool visual, string output)
    {
        string name = "somsai-load-" + Guid.NewGuid().ToString("N");
        using GameHost host = visual ? Host.GetSuitableDesktopHost(name, new HostOptions { PortableInstallation = true, FriendlyGameName = "SOMSAI UI verification" }) : new HeadlessGameHost(name);
        var game = new LoadGame(visual, output, Path.Combine(output, "profile", name));
        Exception? failure = null;
        host.ExceptionThrown += exception => { Console.Error.WriteLine(exception); failure = exception; host.Exit(); return true; };
        using var timeout = new Timer(_ => host.Exit(), null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        host.Run(game);
        if (failure != null) throw failure;
        if (!game.Passed) throw new Exception("Full drawable load/layout did not finish within 150 seconds");
        return 0;
    }
}


