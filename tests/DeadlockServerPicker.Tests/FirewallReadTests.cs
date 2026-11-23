using DeadlockServerPicker.Core.Firewall;

namespace DeadlockServerPicker.Tests;

public class FirewallReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Windows -------------------------------------------------------------------------

    [Fact]
    public void WindowsReadCommandIsStable() =>
        Golden.Match("windows-read", new WindowsFirewallBackend(new FakeRunner()).ReadCommand.Display());

    [Fact]
    public void WindowsParsesOnlyOurRulesAndIntersectsDirections()
    {
        const string json = """
            {"Rules":[
              {"Name":"DeadlockServerPicker_ams_out","Remote":["155.133.248.36","155.133.248.37/255.255.255.255"]},
              {"Name":"DeadlockServerPicker_ams_in","Remote":["155.133.248.36","155.133.248.37"]},
              {"Name":"DeadlockServerPicker_fra_out","Remote":["155.133.226.68"]},
              {"Name":"DeadlockServerPicker_Evil_out","Remote":["9.9.9.9"]},
              {"Name":"SomeoneElse_sgp_out","Remote":["103.10.124.116"]}
            ],"Profiles":[{"Name":"Domain","Enabled":"True"},{"Name":"Public","Enabled":"False"}]}
            """;
        var s = WindowsFirewallBackend.ParseRead(json);

        Assert.Equal(["ams", "fra"], s.Blocked.Keys);
        Assert.Equal(["155.133.248.36", "155.133.248.37"], s.Blocked["ams"]);
        Assert.Empty(s.Blocked["fra"]); // outbound only: incomplete, so nothing counts as blocked
        Assert.Contains(s.Warnings, w => w.StartsWith("fra:", StringComparison.Ordinal));
        Assert.Contains(s.Warnings, w => w.Contains("OFF for the Public profile", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsEmptyRead() =>
        Assert.Empty(WindowsFirewallBackend.ParseRead("""{"Rules":[],"Profiles":[{"Name":"Domain","Enabled":"True"}]}""").Blocked);

    [Fact]
    public async Task WindowsReadFailureExplainsTheService()
    {
        var runner = new FakeRunner().On(_ => true, new CommandResult(1, "", "The RPC server is unavailable."));
        var e = await Assert.ThrowsAsync<FirewallException>(() => new WindowsFirewallBackend(runner).ReadAsync(Ct));
        Assert.Contains("Windows Defender Firewall service", e.Message);
    }

    // ---- nftables ------------------------------------------------------------------------

    private const string NftTables = "table inet other_tool\ntable inet deadlock_server_picker\n";

    // Captured from nftables 1.1.6 (note the wrapped element list).
    private const string NftTable = """
        table inet deadlock_server_picker {
        	set pop_fra {
        		type ipv4_addr
        		elements = { 155.133.226.68, 155.133.226.69,
        			     155.133.226.70, 162.254.197.39 }
        	}

        	set pop_ams {
        		type ipv4_addr
        		elements = { 155.133.248.36, 155.133.248.37 }
        	}

        	set pop_sgp {
        		type ipv4_addr
        		elements = { 103.10.124.116 }
        	}

        	chain output {
        		type filter hook output priority filter; policy accept;
        		ip daddr @pop_fra drop
        		ip daddr @pop_ams drop
        		ip daddr @pop_sgp drop
        	}

        	chain input {
        		type filter hook input priority filter; policy accept;
        		ip saddr @pop_fra drop
        		ip saddr @pop_ams drop
        	}
        }
        """;

    [Fact]
    public async Task NftReadsSetsAndRequiresBothChains()
    {
        var runner = new FakeRunner().On("list tables", NftTables).On("list table inet deadlock_server_picker", NftTable);

        var s = await new NftablesBackend(runner).ReadAsync(Ct);

        Golden.Match("nftables-read", Golden.Render(runner.Ran));
        Assert.Equal(["155.133.226.68", "155.133.226.69", "155.133.226.70", "162.254.197.39"], s.Blocked["fra"]);
        Assert.Equal(["155.133.248.36", "155.133.248.37"], s.Blocked["ams"]);
        Assert.Empty(s.Blocked["sgp"]); // no input rule
        Assert.Contains(s.Warnings, w => w.StartsWith("sgp:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NftWithoutOurTableIsEmptyAndDoesNotListIt()
    {
        var runner = new FakeRunner().On("list tables", "table inet firewalld\n");
        var s = await new NftablesBackend(runner).ReadAsync(Ct);
        Assert.Empty(s.Blocked);
        Assert.Single(runner.Ran);
    }

    [Fact]
    public async Task NftFailureIsAFirewallException()
    {
        var runner = new FakeRunner().On("list tables", "Error: Operation not permitted", exit: 1);
        var e = await Assert.ThrowsAsync<FirewallException>(() => new NftablesBackend(runner).ReadAsync(Ct));
        Assert.Contains("Operation not permitted", e.Message);
    }

    // ---- iptables ------------------------------------------------------------------------

    // Captured from iptables 1.8.11 (nf_tables), with unrelated docker/other rules.
    private const string IptablesS = """
        -P INPUT ACCEPT
        -P FORWARD ACCEPT
        -P OUTPUT ACCEPT
        -N DEADLOCK_SERVER_PICKER
        -N DOCKER-USER
        -A INPUT -j DEADLOCK_SERVER_PICKER
        -A OUTPUT -j DEADLOCK_SERVER_PICKER
        -A OUTPUT -m comment --comment "other tool" -j ACCEPT
        -A DEADLOCK_SERVER_PICKER -d 155.133.248.36/32 -m comment --comment "dsp:ams" -j DROP
        -A DEADLOCK_SERVER_PICKER -s 155.133.248.36/32 -m comment --comment "dsp:ams" -j DROP
        -A DEADLOCK_SERVER_PICKER -d 103.10.124.116/32 -m comment --comment dsp:sgp -j DROP
        -A DEADLOCK_SERVER_PICKER -d 6.6.6.6/32 -m comment --comment "someone:else" -j DROP
        -A DOCKER-USER -j RETURN
        """;

    [Fact]
    public async Task IptablesReadsTaggedRulesAndHooks()
    {
        var runner = new FakeRunner().On("-w -S", IptablesS);

        var s = await new IptablesBackend(runner).ReadAsync(Ct);

        Golden.Match("iptables-read", Golden.Render(runner.Ran));
        Assert.Equal(["ams", "sgp"], s.Blocked.Keys);
        Assert.Equal(["155.133.248.36"], s.Blocked["ams"]);
        Assert.Empty(s.Blocked["sgp"]);
        Assert.Equal(["INPUT", "OUTPUT"], s.Hooks.Order());
    }

    [Fact]
    public void IptablesMissingHookMeansNotEnforced()
    {
        var s = IptablesBackend.Parse(IptablesS.Replace("-A INPUT -j DEADLOCK_SERVER_PICKER\n", "", StringComparison.Ordinal));
        Assert.Empty(s.Blocked["ams"]);
        Assert.Contains(s.Warnings, w => w.Contains("jump from INPUT", StringComparison.Ordinal));
    }

    // ---- detection -----------------------------------------------------------------------

    [Theory]
    [InlineData("nftables")]
    [InlineData("iptables")]
    [InlineData("windows")]
    public async Task ExplicitBackendWins(string name) =>
        Assert.Equal(name, (await FirewallBackends.CreateAsync(new FakeRunner(), name, Ct)).Name);

    [Fact]
    public async Task UnknownBackendIsRejected() =>
        await Assert.ThrowsAsync<FirewallException>(() => FirewallBackends.CreateAsync(new FakeRunner(), "pf", Ct));
}
