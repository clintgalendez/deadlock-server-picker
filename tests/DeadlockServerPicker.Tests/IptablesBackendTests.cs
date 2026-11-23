using DeadlockServerPicker.Core;
using DeadlockServerPicker.Core.Firewall;

namespace DeadlockServerPicker.Tests;

public class IptablesBackendTests
{
    private static readonly IptablesBackend B = new(new FakeRunner());

    private static FirewallState Hooked(FirewallState s, params string[] hooks) => s with { Hooks = hooks };

    [Fact]
    public void ApplyRestoresOurChainAndAddsMissingJumps()
    {
        var (current, desired) = States.Mixed;
        Golden.Match("iptables-apply", Golden.Render(B.PlanApply(current, desired)));
    }

    [Fact]
    public void ApplyOnlyRepairsMissingJumpsWhenRulesAlreadyMatch()
    {
        var (_, desired) = States.Mixed;
        var current = Hooked(States.Current(desired.Blocked.Select(kv => (kv.Key, kv.Value.ToArray())).ToArray()), "INPUT");
        var cmds = B.PlanApply(current, desired);
        Assert.Equal("iptables -w -I OUTPUT 1 -j DSP_DEADLOCK_SERVER_PICKER", cmds[^1].Display());
        Assert.Equal(2, cmds.Count);
    }

    [Fact]
    public void ApplyIsIdempotentWhenRulesAndJumpsMatch()
    {
        var (_, desired) = States.Mixed;
        var current = Hooked(States.Current(desired.Blocked.Select(kv => (kv.Key, kv.Value.ToArray())).ToArray()), "OUTPUT", "INPUT");
        Assert.Empty(B.PlanApply(current, desired));
    }

    [Fact]
    public void ResetRemovesEveryJumpThenOurChain()
    {
        var current = Hooked(States.Mixed.Current, "OUTPUT", "INPUT", "OUTPUT"); // a duplicated jump
        Golden.Match("iptables-reset", Golden.Render(B.PlanReset(current)));
        Assert.Equal(Golden.Render(B.PlanReset(current)), Golden.Render(B.PlanApply(current, DesiredState.Empty)));
    }

    [Fact]
    public void ResetOnACleanSystemDoesNothing() => Assert.Empty(B.PlanReset(FirewallState.Empty));
}
