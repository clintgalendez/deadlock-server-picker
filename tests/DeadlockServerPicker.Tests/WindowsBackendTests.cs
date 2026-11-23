using DeadlockServerPicker.Core;
using DeadlockServerPicker.Core.Firewall;

namespace DeadlockServerPicker.Tests;

internal static class States
{
    public static SortedSet<string> Set(params string[] ips) => new(ips, StringComparer.Ordinal);

    public static FirewallState Current(params (string Pop, string[] Ips)[] pops) => new(
        pops.ToDictionary(p => p.Pop, p => (IReadOnlySet<string>)Set(p.Ips)),
        [],
        [],
        Present: pops.Length > 0);

    public static DesiredState Desired(params (string Pop, string[] Ips)[] pops) =>
        new(new SortedDictionary<string, IReadOnlySet<string>>(pops.ToDictionary(p => p.Pop, p => (IReadOnlySet<string>)Set(p.Ips)), StringComparer.Ordinal));

    /// <summary>A mixed plan: keep ams, update fra, add sgp, remove hkg.</summary>
    public static (FirewallState Current, DesiredState Desired) Mixed => (
        Current(("ams", ["155.133.248.36", "155.133.248.37"]), ("fra", ["155.133.226.68", "9.9.9.9"]), ("hkg", ["155.133.244.50"])),
        Desired(("ams", ["155.133.248.36", "155.133.248.37"]), ("fra", ["155.133.226.68", "155.133.226.69"]), ("sgp", ["103.10.124.116"])));
}

public class WindowsBackendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ApplyRemovesChangedPopsThenCreatesInAndOutRules()
    {
        var (current, desired) = States.Mixed;
        Golden.Match("windows-apply", Golden.Render(new WindowsFirewallBackend(new FakeRunner()).PlanApply(current, desired)));
    }

    [Fact]
    public void ApplyIsIdempotent()
    {
        var (_, desired) = States.Mixed;
        var current = States.Current(desired.Blocked.Select(kv => (kv.Key, kv.Value.ToArray())).ToArray());
        Assert.Empty(new WindowsFirewallBackend(new FakeRunner()).PlanApply(current, desired));
    }

    [Fact]
    public void UnblockAllIsARemoveOnlyScript()
    {
        var (current, _) = States.Mixed;
        Golden.Match("windows-unblock-all", Golden.Render(new WindowsFirewallBackend(new FakeRunner()).PlanApply(current, DesiredState.Empty)));
    }

    [Fact]
    public void ResetTouchesOnlyOurGroupAndPrefix()
    {
        var b = new WindowsFirewallBackend(new FakeRunner());
        Golden.Match("windows-reset", Golden.Render(b.PlanReset(States.Mixed.Current)));
        Assert.Empty(b.PlanReset(FirewallState.Empty));
    }

    [Fact]
    public void UnrecognisedRulesInOurGroupStillCountAsPresent()
    {
        var s = WindowsFirewallBackend.ParseRead("""{"Rules":[{"Name":"DeadlockServerPicker_Weird","Remote":["1.2.3.4"]}],"Profiles":[]}""");
        Assert.True(s.Present);
        Assert.Empty(s.Blocked);
        Assert.Contains(s.Warnings, w => w.Contains("dsp reset", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAndVerifyReadsBack()
    {
        var desired = States.Desired(("ams", ["155.133.248.36"]));
        var runner = new FakeRunner().On(
            c => c.Stdin?.Contains("ConvertTo-Json", StringComparison.Ordinal) == true,
            new CommandResult(0, """{"Rules":[{"Name":"DeadlockServerPicker_ams_out","Remote":["155.133.248.36"]},{"Name":"DeadlockServerPicker_ams_in","Remote":["155.133.248.36"]}],"Profiles":[]}""", ""));
        var b = new WindowsFirewallBackend(runner);

        var after = await FirewallBackends.ApplyAndVerifyAsync(b, runner, b.PlanApply(FirewallState.Empty, desired), desired, Ct);

        Assert.Equal(["ams"], after.Blocked.Keys);
        Assert.Equal(2, runner.Ran.Count);
    }

    [Fact]
    public async Task ApplyThatDoesNotStickIsReportedNotSwallowed()
    {
        var desired = States.Desired(("ams", ["155.133.248.36"]));
        var runner = new FakeRunner().On(
            c => c.Stdin?.Contains("ConvertTo-Json", StringComparison.Ordinal) == true,
            new CommandResult(0, """{"Rules":[],"Profiles":[]}""", ""));
        var b = new WindowsFirewallBackend(runner);

        var e = await Assert.ThrowsAsync<FirewallException>(() => FirewallBackends.ApplyAndVerifyAsync(b, runner, b.PlanApply(FirewallState.Empty, desired), desired, Ct));
        Assert.Contains("ams (add did not take effect)", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailingCommandStopsAndSurfacesStderr()
    {
        var runner = new FakeRunner().On(_ => true, new CommandResult(1, "", "Access is denied."));
        var b = new WindowsFirewallBackend(runner);
        var e = await Assert.ThrowsAsync<FirewallException>(() => FirewallBackends.ExecuteAsync(runner, b.PlanApply(FirewallState.Empty, States.Desired(("ams", ["1.2.3.4"]))), Ct));
        Assert.Contains("Access is denied.", e.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Real Windows Defender Firewall round trip. Opt-in: set DSP_INTEGRATION_WINDOWS=1 and run from an
/// elevated shell on a machine with no Deadlock Server Picker rules. Uses documentation-only
/// addresses (RFC 5737) and fake PoP codes, and always resets afterwards.
/// </summary>
public class WindowsIntegrationTests
{
    [Fact]
    public async Task RealFirewallRoundTrip()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DSP_INTEGRATION_WINDOWS") == "1", "opt-in: set DSP_INTEGRATION_WINDOWS=1");
        Assert.SkipUnless(OperatingSystem.IsWindows() && Elevation.IsElevated(), "needs an elevated Windows shell");
        var ct = TestContext.Current.CancellationToken;
        var runner = new ProcessRunner();
        var b = new WindowsFirewallBackend(runner, WindowsFirewallBackend.DefaultPowerShell);
        var before = await b.ReadAsync(ct);
        Assert.SkipWhen(before.Present, "this machine already has Deadlock Server Picker rules; run `dsp reset` first");

        var desired = States.Desired(("zzta", ["192.0.2.1", "192.0.2.2"]), ("zztb", ["198.51.100.7"]));
        try
        {
            var after = await FirewallBackends.ApplyAndVerifyAsync(b, runner, b.PlanApply(before, desired), desired, ct);
            Assert.Empty(b.PlanApply(after, desired));

            var updated = States.Desired(("zzta", ["192.0.2.3"]));
            await FirewallBackends.ApplyAndVerifyAsync(b, runner, b.PlanApply(after, updated), updated, ct);
        }
        finally
        {
            await FirewallBackends.ResetAndVerifyAsync(b, runner, await b.ReadAsync(ct), ct);
        }
        Assert.False((await b.ReadAsync(ct)).Present);
    }
}
