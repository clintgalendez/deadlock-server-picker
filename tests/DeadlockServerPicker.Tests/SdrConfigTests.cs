using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Tests;

public class SdrConfigTests
{
    [Fact]
    public void ParsesDeadlockFixture()
    {
        var c = Fixtures.Deadlock;
        Assert.Equal(1794268433, c.Revision);
        Assert.Equal(37, c.Pops.Count);
        Assert.Equal(33, c.Pops.Values.Count(p => p.Blockable));
        Assert.Equal(151, c.Pops.Values.Sum(p => p.Relays.Count));
        Assert.Equal(["dsto", "eat", "fsn", "hel"], c.Pops.Values.Where(p => !p.Blockable).Select(p => p.Code));

        var ams = c.Pops["ams"];
        Assert.Equal("Amsterdam (Netherlands)", ams.Description);
        Assert.Equal(new Relay("155.133.248.36", 27015, 27060), ams.Relays[0]);
        Assert.Equal(["tyo1", "tyo2", "tyo3"], c.Pops["tyo"].Aliases);
        Assert.NotEmpty(c.TypicalPings);
    }

    [Fact]
    public void ParsesCs2FixtureWithSameCode() => Assert.Equal(53, Fixtures.Cs2.Pops.Count);

    [Fact]
    public void RelaylessPopsPointAtNearbyRelays()
    {
        var near = Fixtures.Deadlock.NearbyBlockable("hel");
        Assert.Equal(["sto", "sto2"], near.Take(2).Select(x => x.Code));
        Assert.All(near, x => Assert.True(Fixtures.Deadlock.Pops[x.Code].Blockable));
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3.4; Remove-NetFirewallRule", null)]
    [InlineData("1", null)]
    [InlineData("0x7f.0.0.1", null)]
    [InlineData("01.2.3.4", null)]
    [InlineData("::1", null)]
    [InlineData("1.2.3.4/24", null)]
    [InlineData("", null)]
    public void OnlyLiteralIpv4Survives(string input, string? expected) => Assert.Equal(expected, SdrConfig.NormalizeIpv4(input));

    [Fact]
    public void DropsHostilePopCodesAndAddresses()
    {
        var c = SdrConfig.Parse("""
            {"revision": 5, "pops": {
              "ams": {"desc": "A", "relays": [{"ipv4": "1.2.3.4", "port_range": [1, 2]}, {"ipv4": "1.2.3.4"}, {"ipv4": "evil\"; drop"}, {"ipv6": "::1"}]},
              "x y": {"relays": [{"ipv4": "5.6.7.8"}]},
              "AMS": {"relays": [{"ipv4": "5.6.7.8"}]},
              "new": {"future_field": {"a": 1}}
            }, "unknown_top_level": true}
            """);
        Assert.Equal(["ams", "new"], c.Pops.Keys);
        Assert.Equal([new Relay("1.2.3.4", 1, 2)], c.Pops["ams"].Relays);
        Assert.False(c.Pops["new"].Blockable);
        Assert.Equal("new", c.Pops["new"].Description);
    }

    [Theory]
    [InlineData("""{"revision": 1, "pops": {"ams": {}}, "success": false}""")]
    [InlineData("""{"pops": {"ams": {}}}""")]
    [InlineData("""{"revision": 1, "pops": {}}""")]
    [InlineData("""[]""")]
    public void RejectsInvalidConfigs(string json) => Assert.Throws<FormatException>(() => SdrConfig.Parse(json));
}
