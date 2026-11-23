using System.Reflection;

namespace DeadlockServerPicker.Core;

public static class AppInfo
{
    public const string Name = "Deadlock Server Picker";
    public const int DeadlockAppId = 1422450;

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
