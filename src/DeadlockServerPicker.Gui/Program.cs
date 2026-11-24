using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using DeadlockServerPicker.Core;

namespace DeadlockServerPicker.Gui;

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Log.Init(Log.DefaultPath, "gui");
        try
        {
            return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            Log.Error("gui crashed", e);
            throw;
        }
    }
}
