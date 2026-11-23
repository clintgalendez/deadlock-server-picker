using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DeadlockServerPicker.Core;

public static partial class Elevation
{
    /// <summary>Administrator (Windows, elevated token) or root (Linux, euid 0).</summary>
    public static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        return geteuid() == 0;
    }

    /// <summary>What to tell the user when a firewall change needs privileges they don't have.</summary>
    public static string Hint => OperatingSystem.IsWindows()
        ? "Changing Windows Firewall rules needs administrator rights. Run this from an elevated terminal (right-click Terminal or PowerShell > Run as administrator)."
        : "Changing or reading the firewall needs root. Re-run with sudo, e.g. `sudo dsp ...`.";

    [LibraryImport("libc", SetLastError = false)]
    private static partial uint geteuid();
}
