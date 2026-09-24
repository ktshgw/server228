using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Linked only into our test executables, never into the distributed game module.
internal static class QuietTestProcess
{
    [ModuleInitializer]
    internal static void Initialise()
    {
        if (OperatingSystem.IsWindows())
            SetErrorMode(GetErrorMode() | 0x0001u | 0x0002u | 0x8000u);
    }

    internal static int Run(Func<int> test)
    {
        try { return test(); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
}
