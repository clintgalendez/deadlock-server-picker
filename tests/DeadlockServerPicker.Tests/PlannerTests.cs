using DeadlockServerPicker.Core;
using DeadlockServerPicker.Core.Firewall;

namespace DeadlockServerPicker.Tests;

public class PlannerTests
{
    private static readonly Core.Sdr.SdrConfig Cfg = Fixtures.Deadlock;

    private static SortedSet<string> Ips(string pop) => new(Cfg.Pops[pop].Relays.Select(r => r.Ipv4), StringComparer.Ordinal);

    private static FirewallState State(params (string Pop, IEnumerable<string> Ips)[] pops) => new(
        pops.ToDictionary(p => p.Pop, p => (IReadOnlySet<string>)new SortedSet<string>(p.Ips, StringComparer.Ordinal)),
        [],
        []);

    [Fact]
    public void ResolvesCodesAliasesAndRegions()
    {
        var s = Planner.Resolve(Cfg, ["FRA", "bom", "sa"]);
        Assert.Equal(["bom2", "eze", "fra", "gru", "lim", "scl"], s.Pops);
        Assert.Empty(s.Notes);
    }

    [Fact]
    public void RegionSelectionSkipsUnblockablePopsSilently()
    {
        var s = Planner.Resolve(Cfg, ["eu"]);
        Assert.DoesNotContain("hel", s.Pops);
        Assert.Contains("sto", s.Pops);
        Assert.Empty(s.Notes);
    }

    [Fact]
    public void ExplicitUnblockablePopGetsANoteNamingItsRelays()
    {
        var s = Planner.Resolve(Cfg, ["hel"]);
        Assert.Empty(s.Pops);
        Assert.Contains("reached through sto (7 ms), sto2 (8 ms)", Assert.Single(s.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSelectorThrows() => Assert.Throws<ArgumentException>(() => Planner.Resolve(Cfg, ["atlantis"]));

    [Fact]
    public void NoChangeMeansEmptyDiff()
    {
        var current = State(("ams", Ips("ams")));
        Assert.Empty(Planner.Diff(current, Planner.Block(Cfg, current, ["ams"])));
    }

    [Fact]
    public void BlockAddsAndKeepsExisting()
    {
        var current = State(("ams", Ips("ams")));
        var diff = Planner.Diff(current, Planner.Block(Cfg, current, ["fra"]));
        var change = Assert.Single(diff);
        Assert.Equal(ChangeKind.Add, change.Kind);
        Assert.Equal("fra", change.Pop);
        Assert.Equal(16, change.After.Count);
    }

    [Fact]
    public void StaleIpsAreUpdatedAndVanishedPopsRemoved()
    {
        var current = State(("ams", ["155.133.248.36", "9.9.9.9"]), ("gone", ["8.8.8.8"]));
        var diff = Planner.Diff(current, Planner.Block(Cfg, current, []));
        Assert.Equal([("ams", ChangeKind.Update), ("gone", ChangeKind.Remove)], diff.Select(c => (c.Pop, c.Kind)));
        Assert.Equal(Ips("ams"), diff[0].After);
    }

    [Fact]
    public void UnblockRemovesOnlyTheSelection()
    {
        var current = State(("ams", Ips("ams")), ("fra", Ips("fra")));
        var diff = Planner.Diff(current, Planner.Unblock(Cfg, current, ["fra"]));
        Assert.Equal([("fra", ChangeKind.Remove)], diff.Select(c => (c.Pop, c.Kind)));
    }

    [Fact]
    public void PartiallyAppliedPopIsRepaired()
    {
        // The read side reports an incomplete PoP with zero IPs; re-planning must restore it.
        var current = State(("ams", []));
        var diff = Planner.Diff(current, Planner.Block(Cfg, current, []));
        Assert.Equal(ChangeKind.Update, Assert.Single(diff).Kind);
    }

    [Fact]
    public void OnlyRegionBlocksEverythingElseAndLeavesTheRegionOpen()
    {
        var desired = Planner.OnlyRegions(Cfg, new HashSet<Region> { Region.EU });
        Assert.Contains("hkg", desired.Blocked.Keys);
        Assert.Contains("iad", desired.Blocked.Keys);
        Assert.DoesNotContain("fra", desired.Blocked.Keys);
        Assert.DoesNotContain("hel", desired.Blocked.Keys); // not blockable
        Assert.Equal(33 - 12, desired.Blocked.Count); // 12 blockable EU PoPs stay open
        Assert.Null(Planner.OverBlockingWarning(Cfg, desired));
    }

    [Fact]
    public void WithinBlocksSlowPopsButNeverTimeouts()
    {
        var pings = new Dictionary<string, long?> { ["ams"] = 20, ["fra"] = 81, ["sgp"] = null, ["syd"] = 300 };
        var (desired, timedOut) = Planner.Within(Cfg, pings, 80);
        Assert.Equal(["fra", "syd"], desired.Blocked.Keys);
        Assert.Equal(["sgp"], timedOut);
    }

    [Fact]
    public void WarnsWhenTooFewPopsRemainOpen()
    {
        var all = Cfg.Pops.Values.Where(p => p.Blockable).Select(p => p.Code).ToList();
        Assert.Contains("Only 2 PoPs left open (ams, fra)", Planner.OverBlockingWarning(Cfg, Planner.Desired(Cfg, all.Except(["ams", "fra"]))), StringComparison.Ordinal);
        Assert.Contains("Every blockable PoP is blocked", Planner.OverBlockingWarning(Cfg, Planner.Desired(Cfg, all)), StringComparison.Ordinal);
        Assert.Null(Planner.OverBlockingWarning(Cfg, Planner.Desired(Cfg, ["ams"])));
    }
    [Fact]
    public void StaleBlocksReportNewAndOldRelaysAndVanishedPops()
    {
        var current = State(("ams", ["155.133.248.36", "9.9.9.9"]), ("fra", Ips("fra")), ("gone", ["8.8.8.8"]));
        var stale = Planner.StaleBlocks(Cfg, current);
        Assert.Equal(["ams", "gone"], stale.Select(s => s.Pop));
        Assert.Equal(["155.133.248.37", "155.133.248.40", "155.133.248.41"], stale[0].Missing);
        Assert.Equal(["9.9.9.9"], stale[0].Extra);
        Assert.True(stale[1].Vanished);
        Assert.Equal("ams: 3 new relay IPs not blocked yet, 1 old IP still blocked", stale[0].Describe());
    }

    [Fact]
    public void ResyncKeepsTheSamePopsWithCurrentIps()
    {
        var current = State(("ams", ["9.9.9.9"]), ("gone", ["8.8.8.8"]));
        var desired = Planner.Resync(Cfg, current);
        Assert.Equal(["ams"], desired.Blocked.Keys);
        Assert.Equal(Ips("ams"), desired.Blocked["ams"]);
    }
}
