using DeadlockServerPicker.Core;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Tests;

public class RegionsTests
{
    public static TheoryData<string> AllFixturePops()
    {
        var data = new TheoryData<string>();
        foreach (var code in Fixtures.Deadlock.Pops.Keys.Union(Fixtures.Cs2.Pops.Keys).Order(StringComparer.Ordinal))
            data.Add(code);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllFixturePops))]
    public void EveryFixturePopIsMappedExplicitly(string code) => Assert.True(Regions.IsMapped(code), $"{code} missing from regions.json");

    [Theory]
    [InlineData("ams", Region.EU)]
    [InlineData("dfra", Region.EU)] // geo is swapped in the live data
    [InlineData("hel", Region.EU)]
    [InlineData("gum", Region.Asia)]
    [InlineData("dxb", Region.MiddleEast)]
    [InlineData("jnb", Region.Africa)]
    [InlineData("syd", Region.Oceania)]
    [InlineData("gru", Region.SA)]
    [InlineData("eat", Region.NA)]
    public void KnownPops(string code, Region expected)
    {
        var info = Regions.Of(Fixtures.Deadlock.Pops[code], Fixtures.Deadlock);
        Assert.Equal(new RegionInfo(expected, false), info);
    }

    [Fact]
    public void UnknownPopUsesNearestMappedPopEvenWithBadGeo()
    {
        // Geo says Asia ([139, 35]) but Valve's typical_pings put it next to fra.
        var cfg = SdrConfig.Parse("""
            {"revision": 1, "pops": {
              "fra": {"relays": [{"ipv4": "1.1.1.1"}]},
              "zzz": {"geo": [139, 35], "relays": [{"ipv4": "2.2.2.2"}]}
            }, "typical_pings": [["fra", "zzz", 3]]}
            """);
        Assert.Equal(new RegionInfo(Region.EU, true), Regions.Of(cfg.Pops["zzz"], cfg));
    }

    [Theory]
    [InlineData(-100, 40, Region.NA)]
    [InlineData(-46.6, -23.5, Region.SA)]
    [InlineData(-77, -12, Region.SA)]
    [InlineData(10, 50, Region.EU)]
    [InlineData(55.3, 25.2, Region.MiddleEast)]
    [InlineData(28, -26, Region.Africa)]
    [InlineData(151, -33, Region.Oceania)]
    [InlineData(139, 35, Region.Asia)]
    [InlineData(103.8, 1.3, Region.Asia)]
    [InlineData(35, 139, Region.Asia)] // impossible latitude: treated as swapped [lat, lon]
    public void GeoFallback(double lon, double lat, Region expected) => Assert.Equal(expected, Regions.FromGeo(lon, lat));

    [Fact]
    public void UnknownPopWithoutPingsFallsBackToGeoAndIsFlagged()
    {
        var cfg = SdrConfig.Parse("""{"revision": 1, "pops": {"zzz": {"geo": [151.2, -33.8]}}}""");
        Assert.Equal(new RegionInfo(Region.Oceania, true), Regions.Of(cfg.Pops["zzz"], cfg));
    }

    [Theory]
    [InlineData("EU", Region.EU)]
    [InlineData("oce", Region.Oceania)]
    [InlineData("me", Region.MiddleEast)]
    [InlineData("Asia", Region.Asia)]
    public void ParsesRegionNames(string s, Region expected)
    {
        Assert.True(Regions.TryParse(s, out var r));
        Assert.Equal(expected, r);
    }

    [Fact]
    public void RejectsUnknownRegionNames() => Assert.False(Regions.TryParse("mars", out _));

    [Fact]
    public void CitadelOverrideMatchesTheInGameTable()
    {
        Assert.Equal(0, Regions.CitadelRegionOverride(Region.NA));
        Assert.Equal(1, Regions.CitadelRegionOverride(Region.EU));
        Assert.Equal(2, Regions.CitadelRegionOverride(Region.Asia));
        Assert.Equal(3, Regions.CitadelRegionOverride(Region.SA));
        Assert.Equal(5, Regions.CitadelRegionOverride(Region.Oceania));
        Assert.Null(Regions.CitadelRegionOverride(Region.MiddleEast));
    }
}
