using DeadlockServerPicker.Core;

namespace DeadlockServerPicker.Cli;

public sealed class UsageException(string message) : Exception(message);

/// <summary>Hand-rolled parser: one verb, positionals, and a handful of flags.</summary>
public sealed class CliArgs
{
    public string Command { get; private set; } = "help";
    public List<string> Positional { get; } = [];
    public bool Json { get; private set; }
    public bool DryRun { get; private set; }
    public bool Offline { get; private set; }
    public bool All { get; private set; }
    public int AppId { get; private set; } = AppInfo.DeadlockAppId;
    public string? Backend { get; private set; }

    public static CliArgs Parse(IReadOnlyList<string> argv)
    {
        var a = new CliArgs();
        var sawCommand = false;
        for (var i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];
            switch (arg)
            {
                case "--json": a.Json = true; break;
                case "--dry-run": a.DryRun = true; break;
                case "--offline": a.Offline = true; break;
                case "--all": a.All = true; break;
                case "-h" or "--help": a.Command = "help"; sawCommand = true; break;
                case "--version": a.Command = "version"; sawCommand = true; break;
                case "--app":
                    if (!int.TryParse(Value(argv, ref i, arg), out var app) || app <= 0)
                        throw new UsageException("--app needs a positive Steam app ID, e.g. --app 730");
                    a.AppId = app;
                    break;
                case "--backend":
                    a.Backend = Value(argv, ref i, arg);
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw new UsageException($"Unknown option '{arg}'. Run `dsp help`.");
                    if (!sawCommand)
                    {
                        a.Command = arg;
                        sawCommand = true;
                    }
                    else
                    {
                        a.Positional.Add(arg.ToLowerInvariant());
                    }
                    break;
            }
        }
        return a;
    }

    private static string Value(IReadOnlyList<string> argv, ref int i, string flag) =>
        ++i < argv.Count ? argv[i] : throw new UsageException($"{flag} needs a value.");
}
