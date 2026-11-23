using System.Text.Json;
using DeadlockServerPicker.Cli;
using DeadlockServerPicker.Core;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Tests;

internal sealed class MapPinger(Func<string, long?> rtt) : IPinger
{
    public Task<long?> PingAsync(string ipv4, int timeoutMs, CancellationToken ct) => Task.FromResult(rtt(ipv4));
}

/// <summary>Runs the CLI against fixtures, a fake firewall and a fake pinger.</summary>
internal sealed class CliHarness
{
    public static readonly DateTimeOffset FetchedAt = new(2026, 11, 24, 6, 45, 0, TimeSpan.Zero);

    public FakeRunner Runner { get; } = new();
    public StringWriter Out { get; } = new();
    public StringWriter Err { get; } = new();
    public bool Elevated { get; set; } = true;
    public IPinger Pinger { get; set; } = new MapPinger(ip => ip.StartsWith("155.", StringComparison.Ordinal) ? 30 : ip.StartsWith("103.", StringComparison.Ordinal) ? null : 120);
    public string CacheDir { get; } = Fixtures.TempDir();

    /// <summary>Seeds the on-disk cache with the Deadlock fixture so --offline works deterministically.</summary>
    public CliHarness WithCachedConfig()
    {
        var path = new SdrConfigSource(new HttpClient(StubHandler.Fail()), CacheDir).CachePath(AppInfo.DeadlockAppId);
        File.WriteAllText(path, Fixtures.Read("sdr-1422450.json"));
        File.SetLastWriteTimeUtc(path, FetchedAt.UtcDateTime);
        return this;
    }

    public async Task<int> RunAsync(params string[] argv)
    {
        var env = new CliEnv(new SdrConfigSource(new HttpClient(StubHandler.Fail()), CacheDir), Runner, Pinger, Out, Err, () => Elevated);
        return await Cli.Cli.RunAsync(argv, env, TestContext.Current.CancellationToken);
    }
}

public class CliTests
{
    [Fact]
    public void ParsesFlagsAndPositionals()
    {
        var a = CliArgs.Parse(["block", "EU", "fra", "--dry-run", "--json", "--app", "730", "--backend", "iptables"]);
        Assert.Equal("block", a.Command);
        Assert.Equal(["eu", "fra"], a.Positional);
        Assert.True(a.DryRun && a.Json);
        Assert.Equal(730, a.AppId);
        Assert.Equal("iptables", a.Backend);
    }

    [Theory]
    [InlineData("--app")]
    [InlineData("--app", "x")]
    [InlineData("list", "--frobnicate")]
    [InlineData("frobnicate")]
    public async Task UsageErrorsExitWith2(params string[] argv)
    {
        var h = new CliHarness();
        Assert.Equal(ExitCodes.Usage, await h.RunAsync(argv));
        Assert.StartsWith("error:", h.Err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListGroupsByRegionAndExplainsUnblockablePops()
    {
        var h = new CliHarness().WithCachedConfig();
        Assert.Equal(0, await h.RunAsync("list", "--offline"));
        Golden.Match("cli-list", h.Out.ToString());
        Assert.Contains("using cached SDR config from 2026-11-24 06:45 UTC (offline mode)", h.Err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListJson()
    {
        var h = new CliHarness().WithCachedConfig();
        Assert.Equal(0, await h.RunAsync("list", "--offline", "--json"));
        using var doc = JsonDocument.Parse(h.Out.ToString());
        Assert.Equal(1794268433, doc.RootElement.GetProperty("config").GetProperty("revision").GetInt64());
        Assert.True(doc.RootElement.GetProperty("config").GetProperty("stale").GetBoolean());
        var hel = doc.RootElement.GetProperty("pops").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "hel");
        Assert.False(hel.GetProperty("blockable").GetBoolean());
        Assert.Equal("sto", hel.GetProperty("reachedVia")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task PingShowsMsAndTimeouts()
    {
        var h = new CliHarness().WithCachedConfig();
        Assert.Equal(0, await h.RunAsync("ping", "--offline"));
        Golden.Match("cli-ping", h.Out.ToString());
    }

    [Fact]
    public async Task NoConfigAnywhereExitsWith3()
    {
        var h = new CliHarness();
        Assert.Equal(ExitCodes.ConfigUnavailable, await h.RunAsync("list"));
    }

    [Fact]
    public async Task StatusReadsTheFirewallAndComparesWithConfig()
    {
        var h = new CliHarness().WithCachedConfig();
        h.Runner.On("list tables", "table inet deadlock_server_picker\n").On("list table inet deadlock_server_picker", """
            table inet deadlock_server_picker {
            	set pop_ams {
            		type ipv4_addr
            		elements = { 155.133.248.36, 155.133.248.37, 155.133.248.40, 155.133.248.41 }
            	}
            	set pop_sgp {
            		type ipv4_addr
            		elements = { 1.1.1.1 }
            	}
            	chain output {
            		type filter hook output priority filter; policy accept;
            		ip daddr @pop_ams drop
            		ip daddr @pop_sgp drop
            	}
            	chain input {
            		type filter hook input priority filter; policy accept;
            		ip saddr @pop_ams drop
            		ip saddr @pop_sgp drop
            	}
            }
            """);

        Assert.Equal(0, await h.RunAsync("status", "--backend", "nftables", "--offline"));
        Golden.Match("cli-status", h.Out.ToString());
    }

    [Fact]
    public async Task StatusOnLinuxBackendsNeedsRoot()
    {
        var h = new CliHarness { Elevated = false };
        Assert.Equal(ExitCodes.NeedsElevation, await h.RunAsync("status", "--backend", "nftables"));
        Assert.Contains("sudo dsp status", h.Err.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(h.Runner.Ran, c => c.Args.Contains("list"));
    }

    [Fact]
    public async Task VersionJson()
    {
        var h = new CliHarness();
        Assert.Equal(0, await h.RunAsync("version", "--json"));
        using var doc = JsonDocument.Parse(h.Out.ToString());
        Assert.Equal(AppInfo.Version, doc.RootElement.GetProperty("version").GetString());
    }
}
