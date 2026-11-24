using DeadlockServerPicker.Core;

namespace DeadlockServerPicker.Tests;

public class PickerModelTests
{
    private static PickerModel Model() => new(Fixtures.Deadlock);

    [Fact]
    public void StartsWithEverythingAllowedAndUnknownStatus()
    {
        var m = Model();
        Assert.Equal(33, m.Allowed.Count);
        Assert.Equal(0, m.PendingChanges);
        Assert.All(m.Rows.Where(r => r.Pop.Blockable), r => Assert.Equal(PopStatus.Unknown, r.Status));
        var hel = m.Rows.Single(r => r.Pop.Code == "hel");
        Assert.Equal(PopStatus.NotBlockable, hel.Status);
        Assert.Equal("sto", hel.ReachedVia[0].Code);
    }

    [Fact]
    public void FirewallStateDrivesCheckboxesAndStatuses()
    {
        var m = Model();
        var ams = Fixtures.Deadlock.Pops["ams"].Relays.Select(r => r.Ipv4).ToArray();
        m.SetFirewall(States.Current(("ams", ams), ("fra", ["1.1.1.1"])));

        Assert.DoesNotContain("ams", m.Allowed);
        Assert.Equal(PopStatus.Blocked, m.Rows.Single(r => r.Pop.Code == "ams").Status);
        Assert.Equal(PopStatus.BlockedStale, m.Rows.Single(r => r.Pop.Code == "fra").Status);
        Assert.Equal(PopStatus.Open, m.Rows.Single(r => r.Pop.Code == "sgp").Status);
        Assert.Equal(1, m.PendingChanges); // fra needs its relay IPs refreshed
    }

    [Fact]
    public void RegionToggleIsTriState()
    {
        var m = Model();
        Assert.True(m.RegionState(Region.EU));
        m.SetAllowed("fra", false);
        Assert.Null(m.RegionState(Region.EU));
        m.SetRegion(Region.EU, false);
        Assert.False(m.RegionState(Region.EU));
        Assert.Equal(12, m.PendingChanges);
    }

    [Fact]
    public void UnblockablePopsCannotBeUnchecked()
    {
        var m = Model();
        m.SetAllowed("hel", false);
        Assert.Equal(0, m.PendingChanges);
    }

    [Fact]
    public void OnlyRegionGivesTheMatchingInGameTip()
    {
        var m = Model();
        m.OnlyRegion(Region.SA);
        Assert.Equal(["eze", "gru", "lim", "scl"], m.Allowed.Order());
        Assert.Contains("citadel_region_override 3 (South America)", m.RegionOverrideTip, StringComparison.Ordinal);
        Assert.Null(m.OverBlockingWarning);

        m.OnlyRegion(Region.MiddleEast); // no in-game value, and only two PoPs
        Assert.Null(m.RegionOverrideTip);
        Assert.NotNull(m.OverBlockingWarning);
    }

    [Fact]
    public void WithinKeepsUnknownPingsAllowed()
    {
        var m = Model();
        m.SetPings(new Dictionary<string, long?> { ["ams"] = 20, ["fra"] = 200, ["sgp"] = null });
        var unknown = m.Within(100);
        Assert.Contains("ams", m.Allowed);
        Assert.DoesNotContain("fra", m.Allowed);
        Assert.Contains("sgp", m.Allowed);
        Assert.Contains("sgp", unknown);
        Assert.Contains("hkg", unknown); // never pinged
    }

    [Fact]
    public void DesiredBlocksEverythingNotAllowed()
    {
        var m = Model();
        m.OnlyRegion(Region.EU);
        Assert.Equal(21, m.Desired.Blocked.Count);
        m.AllowAll();
        Assert.Empty(m.Desired.Blocked);
    }
}
