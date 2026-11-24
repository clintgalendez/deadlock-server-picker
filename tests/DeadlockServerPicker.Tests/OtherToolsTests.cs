using DeadlockServerPicker.Core.Firewall;

namespace DeadlockServerPicker.Tests;

public class OtherToolsTests
{
    [Fact]
    public void WindowsCountsOtherPickersRulesByPrefix()
    {
        var w = WindowsFirewallBackend.ParseOtherTools("""["server_picker_x_HongKong","server_picker_x_Japan","CS2ServerPicker_China"]""");
        Assert.Equal(2, w.Count);
        Assert.StartsWith("2 firewall rules from another server picker (server_picker_x_*)", w[0], StringComparison.Ordinal);
        Assert.StartsWith("1 firewall rule from another server picker (CS2ServerPicker_*)", w[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    public void WindowsNothingFound(string json) => Assert.Empty(WindowsFirewallBackend.ParseOtherTools(json));

    [Fact]
    public void WindowsSingleRuleSerializedAsAString() =>
        Assert.Single(WindowsFirewallBackend.ParseOtherTools("\"cs2-relay-hkg\""));

    [Fact]
    public void WindowsDetectionCommandIsStable() =>
        Golden.Match("windows-other-tools", new WindowsFirewallBackend(new FakeRunner()).OtherToolsCommand.Display());

    [Fact]
    public void NftFindsOtherPickersButNeverUs()
    {
        const string ruleset = """
            table inet dsp_deadlock_server_picker {
            	chain output {
            	}
            }
            table inet deadlock_server_picker {
            	chain block {
            	}
            }
            table ip filter {
            	chain DSP_DEADLOCK_SERVER_PICKER {
            	}
            	chain CS2_BLOCKLIST {
            	}
            	chain OUTPUT {
            		ip daddr 1.2.3.4 counter drop comment "server_picker_x"
            	}
            }
            """;
        var w = NftablesBackend.OtherToolsIn(ruleset);
        Assert.Equal(3, w.Count);
        Assert.Contains(w, x => x.Contains("(deadlock_server_picker)", StringComparison.Ordinal));
        Assert.Contains(w, x => x.Contains("(CS2_BLOCKLIST)", StringComparison.Ordinal));
        Assert.Contains(w, x => x.Contains("(server_picker_x)", StringComparison.Ordinal));
        Assert.Empty(NftablesBackend.OtherToolsIn("table inet dsp_deadlock_server_picker {\n}\n"));
    }

    [Fact]
    public void IptablesFindsOtherPickersButNeverUs()
    {
        const string rules = """
            -N DSP_DEADLOCK_SERVER_PICKER
            -N DEADLOCK_SERVER_PICKER
            -N cs2-server-picker-rules
            -A OUTPUT -d 1.2.3.4/32 -m comment --comment server_picker_x -j DROP
            -A DSP_DEADLOCK_SERVER_PICKER -d 1.2.3.4/32 -m comment --comment "dsp:ams" -j DROP
            """;
        var w = IptablesBackend.OtherToolsIn(rules);
        Assert.Equal(3, w.Count);
        Assert.Empty(IptablesBackend.OtherToolsIn("-N DSP_DEADLOCK_SERVER_PICKER\n"));
    }

    [Fact]
    public async Task DetectionIsReadOnly()
    {
        var runner = new FakeRunner().On("list ruleset", "table inet deadlock_server_picker {\n}\n");
        var warnings = await new NftablesBackend(runner).DetectOtherToolsAsync(TestContext.Current.CancellationToken);
        Assert.Single(warnings);
        Assert.Equal(["nft list ruleset"], runner.Ran.Select(c => c.Display()));
    }
}
