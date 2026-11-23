using DeadlockServerPicker.Core;
using DeadlockServerPicker.Core.Firewall;

namespace DeadlockServerPicker.Tests;

public class NftablesBackendTests
{
    private static readonly NftablesBackend B = new(new FakeRunner());

    [Fact]
    public void ApplyRegeneratesTheWholeTableAtomically()
    {
        var (current, desired) = States.Mixed;
        Golden.Match("nftables-apply", Golden.Render(B.PlanApply(current, desired)));
    }

    [Fact]
    public void ApplyIsIdempotent()
    {
        var (_, desired) = States.Mixed;
        var current = States.Current(desired.Blocked.Select(kv => (kv.Key, kv.Value.ToArray())).ToArray());
        Assert.Empty(B.PlanApply(current, desired));
    }

    [Fact]
    public void UnblockAllDropsTheTable()
    {
        var (current, _) = States.Mixed;
        Golden.Match("nftables-reset", Golden.Render(B.PlanApply(current, DesiredState.Empty)));
        Assert.Equal(Golden.Render(B.PlanReset(current)), Golden.Render(B.PlanApply(current, DesiredState.Empty)));
    }

    [Fact]
    public void ResetOnACleanSystemDoesNothing() => Assert.Empty(B.PlanReset(FirewallState.Empty));

    [Fact]
    public void EmptyLeftoverTableIsCleanedUpOnUnblockAll()
    {
        var leftover = FirewallState.Empty with { Present = true };
        Assert.Single(B.PlanApply(leftover, DesiredState.Empty));
    }

    [Fact]
    public void GeneratedRulesetParsesBackToTheDesiredState()
    {
        var (_, desired) = States.Mixed;
        var state = NftablesBackend.Parse(NftablesBackend.Ruleset(desired));
        Assert.Empty(Planner.Diff(state, desired));
        Assert.Empty(state.Warnings);
    }
}
