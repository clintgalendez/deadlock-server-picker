using System.Runtime.CompilerServices;
using DeadlockServerPicker.Core.Firewall;

namespace DeadlockServerPicker.Tests;

/// <summary>Records every command; answers with the first matching canned result (default: exit 0, empty output).</summary>
internal sealed class FakeRunner : ICommandRunner
{
    private readonly List<(Func<Command, bool> When, CommandResult Result)> _answers = [];

    public List<Command> Ran { get; } = [];

    public FakeRunner On(Func<Command, bool> when, CommandResult result)
    {
        _answers.Add((when, result));
        return this;
    }

    public FakeRunner On(string argsJoined, string stdout, int exit = 0) =>
        On(c => string.Join(' ', c.Args) == argsJoined, new CommandResult(exit, stdout, exit == 0 ? "" : stdout));

    public Task<CommandResult> RunAsync(Command command, CancellationToken ct = default)
    {
        Ran.Add(command);
        foreach (var (when, result) in _answers)
        {
            if (when(command))
                return Task.FromResult(result);
        }
        return Task.FromResult(new CommandResult(0, "", ""));
    }
}

internal static class Golden
{
    /// <summary>
    /// Compares <paramref name="actual"/> with golden/&lt;name&gt;.txt next to the calling test file.
    /// DSP_UPDATE_GOLDEN=1 rewrites the file instead (review the diff before committing).
    /// </summary>
    public static void Match(string name, string actual, [CallerFilePath] string caller = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(caller)!, "golden", name + ".txt");
        actual = actual.ReplaceLineEndings("\n");
        if (!actual.EndsWith('\n'))
            actual += "\n";
        if (Environment.GetEnvironmentVariable("DSP_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }
        Assert.True(File.Exists(path), $"Golden file missing: {path}. Run with DSP_UPDATE_GOLDEN=1 and review it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    public static string Render(IEnumerable<Command> commands) => string.Join("\n\n", commands.Select(c => c.Display()));
}
