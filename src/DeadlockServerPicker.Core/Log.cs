using System.Runtime.InteropServices;

namespace DeadlockServerPicker.Core;

/// <summary>
/// One append-only log file in the user config dir, rotated once to <c>.1</c> at ~1 MB.
/// Shared by the CLI and GUI. Does nothing until <see cref="Init"/> is called, and never throws:
/// logging must not break the app.
/// </summary>
public static class Log
{
    public const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string DefaultPath => Path.Combine(Paths.ConfigDir, "dsp.log");

    public static string? FilePath { get; private set; }

    public static void Init(string path, string component)
    {
        FilePath = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FilePath = null;
            return;
        }
        Info($"{component} {AppInfo.Version} | {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture} | elevated={Elevation.IsElevated()}");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        var path = FilePath;
        if (path is null)
            return;
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level,-5} [{Environment.ProcessId}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Another process may hold the file; dropping a line beats crashing.
            }
        }
    }
}
