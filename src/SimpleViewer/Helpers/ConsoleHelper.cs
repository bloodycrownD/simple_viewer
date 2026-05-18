// Responsibility: Attach or allocate a console for CLI help output on Windows.
// Invariants: Safe to call when no console exists (falls back to AllocConsole).
// Call chain: App.OnLaunched (ShowHelp) → WriteHelpAndExit → Environment.Exit.

using System.Runtime.InteropServices;

namespace SimpleViewer.Helpers;

/// <summary>
/// Win32 console attachment for -h/--help when launched without a console (e.g. Explorer).
/// </summary>
internal static class ConsoleHelper
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    /// <summary>
    /// Ensures a console is available, writes <paramref name="text"/>, and terminates the process.
    /// </summary>
    public static void WriteHelpAndExit(string text)
    {
        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }

        Console.Out.Write(text);
        if (!text.EndsWith('\n'))
        {
            Console.Out.WriteLine();
        }

        Environment.Exit(0);
    }
}
