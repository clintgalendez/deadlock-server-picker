using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DeadlockServerPicker.Core.Firewall;

/// <summary>A process to run: executable, arguments (never shell-parsed) and optional stdin.</summary>
public sealed record Command(string File, IReadOnlyList<string> Args, string? Stdin = null)
{
    /// <summary>Shell-like rendering for --dry-run, logs and golden files. Stdin is shown as a heredoc.</summary>
    public string Display()
    {
        var sb = new StringBuilder(Quote(File));
        foreach (var a in Args)
            sb.Append(' ').Append(Quote(a));
        if (Stdin is not null)
            sb.Append(" <<'EOF'\n").Append(Stdin.EndsWith('\n') ? Stdin : Stdin + "\n").Append("EOF");
        return sb.ToString();
    }

    private static string Quote(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@%+,".Contains(c)) ? s : "'" + s.Replace("'", "'\\''") + "'";
}

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Seam between command generation and execution so tests can assert exact commands.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(Command command, CancellationToken ct = default);
}

public sealed class ProcessRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(Command command, CancellationToken ct = default)
    {
        Log.Info($"run: {command.Display()}");
        var psi = new ProcessStartInfo(command.File)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = command.Stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in command.Args)
            psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new Win32Exception("process did not start");
        }
        catch (Win32Exception e)
        {
            Log.Warn($"could not start {command.File}: {e.Message}");
            return new CommandResult(127, "", $"{command.File}: {e.Message}");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            if (command.Stdin is not null)
            {
                await process.StandardInput.WriteAsync(command.Stdin.AsMemory(), ct);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(ct);
            var result = new CommandResult(process.ExitCode, await stdout, await stderr);
            if (result.ExitCode == 0)
                Log.Info($"exit 0 ({command.File})");
            else
                Log.Warn($"exit {result.ExitCode} ({command.File}): {result.Stderr.Trim()}");
            return result;
        }
    }
}
