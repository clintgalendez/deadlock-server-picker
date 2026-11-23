using DeadlockServerPicker.Core.Firewall;

namespace DeadlockServerPicker.Tests;

public class CommandTests
{
    [Fact]
    public void DisplayQuotesOnlyWhatNeedsIt() =>
        Assert.Equal("nft list table inet 'a b' 'it'\\''s'", new Command("nft", ["list", "table", "inet", "a b", "it's"]).Display());

    [Fact]
    public void DisplayShowsStdinAsHeredoc() =>
        Assert.Equal("nft -f - <<'EOF'\nflush\nEOF", new Command("nft", ["-f", "-"], "flush").Display());

    [Fact]
    public async Task ProcessRunnerReportsMissingExecutablesInsteadOfThrowing()
    {
        var r = await new ProcessRunner().RunAsync(new Command("dsp-definitely-not-a-real-binary", []), TestContext.Current.CancellationToken);
        Assert.Equal(127, r.ExitCode);
    }

    [Fact]
    public async Task ProcessRunnerPassesStdinAndCapturesStdout()
    {
        var cmd = OperatingSystem.IsWindows()
            ? new Command(WindowsFirewallBackend.DefaultPowerShell, ["-NoProfile", "-NonInteractive", "-Command", "[Console]::In.ReadToEnd().Trim().ToUpper()"], "hello")
            : new Command("cat", [], "hello");
        var r = await new ProcessRunner().RunAsync(cmd, TestContext.Current.CancellationToken);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(OperatingSystem.IsWindows() ? "HELLO" : "hello", r.Stdout.Trim());
    }
}
