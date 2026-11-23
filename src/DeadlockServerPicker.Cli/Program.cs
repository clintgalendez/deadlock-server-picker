using DeadlockServerPicker.Cli;
using DeadlockServerPicker.Core;
using DeadlockServerPicker.Core.Firewall;
using DeadlockServerPicker.Core.Sdr;

Log.Init(Log.DefaultPath, "dsp");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeadlockServerPicker/{AppInfo.Version.Split('+')[0]} (+https://github.com/clintgalendez/deadlock-server-picker)");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var env = new CliEnv(
    new SdrConfigSource(http, Paths.ConfigDir),
    new ProcessRunner(),
    new IcmpPinger(),
    Console.Out,
    Console.Error,
    Elevation.IsElevated);

try
{
    return await Cli.RunAsync(args, env, cts.Token);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception e)
{
    Log.Error("unhandled", e);
    Console.Error.WriteLine($"error: {e.Message}");
    Console.Error.WriteLine($"This is a bug. Please report it with the log file: {Log.FilePath}");
    return ExitCodes.Error;
}
