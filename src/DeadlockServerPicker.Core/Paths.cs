namespace DeadlockServerPicker.Core;

public static class Paths
{
    /// <summary>
    /// Per-user config dir: %APPDATA%\DeadlockServerPicker on Windows,
    /// $XDG_CONFIG_HOME/deadlock-server-picker (default ~/.config/...) elsewhere.
    /// DSP_CONFIG_DIR overrides it (portable use, tests).
    /// </summary>
    public static string ConfigDir { get; } =
        Environment.GetEnvironmentVariable("DSP_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                OperatingSystem.IsWindows() ? "DeadlockServerPicker" : "deadlock-server-picker");
}
