using DeadlockServerPicker.Core;

if (args is ["version"] or ["--version"])
{
    Console.WriteLine($"dsp {AppInfo.Version}");
    return 0;
}

Console.Error.WriteLine("usage: dsp version");
return 2;
