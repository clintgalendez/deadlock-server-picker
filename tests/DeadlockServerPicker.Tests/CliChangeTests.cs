using System.Text.Json;
using DeadlockServerPicker.Cli;
using DeadlockServerPicker.Core.Firewall;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Tests;

public class CliChangeTests
{
    private sealed class Harness
    {
        public FakeNft Nft { get; } = new();
        public StringWriter Out { get; } = new();
        public StringWriter Err { get; } = new();
        public bool Elevated { get; set; } = true;
        private readonly string _cache = Fixtures.TempDir();

        public Harness()
        {
            var path = new SdrConfigSource(new HttpClient(StubHandler.Fail()), _cache).CachePath(1422450);
            File.WriteAllText(path, Fixtures.Read("sdr-1422450.json"));
        }

        public Task<int> Run(params string[] argv)
        {
            Out.GetStringBuilder().Clear();
            Err.GetStringBuilder().Clear();
            var pinger = new MapPinger(ip => ip.StartsWith("155.133.2", StringComparison.Ordinal) ? 25 : ip.StartsWith("103.", StringComparison.Ordinal) ? null : 180);
            var env = new CliEnv(new SdrConfigSource(new HttpClient(StubHandler.Fail()), _cache), Nft, pinger, Out, Err, () => Elevated);
            return Cli.Cli.RunAsync([.. argv, "--backend", "nftables", "--offline"], env, TestContext.Current.CancellationToken);
        }

        public FirewallState Firewall => Nft.Table is null ? FirewallState.Empty : NftablesBackend.Parse(Nft.Table);
    }

    [Fact]
    public async Task BlockDryRunPrintsPlanAndExactCommandsWithoutTouchingTheFirewall()
    {
        var h = new Harness();
        Assert.Equal(0, await h.Run("block", "asia", "--dry-run"));
        Golden.Match("cli-block-asia-dry-run", h.Out.ToString());
        Assert.Empty(h.Nft.Writes);
    }

    [Fact]
    public async Task BlockAppliesVerifiesAndIsIdempotent()
    {
        var h = new Harness();
        Assert.Equal(0, await h.Run("block", "hkg", "sgp"));
        Assert.Equal(["hkg", "sgp"], h.Firewall.Blocked.Keys);
        Assert.Contains("Done. Verified in the firewall: 2 PoPs blocked (hkg, sgp).", h.Out.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await h.Run("block", "sgp"));
        Assert.Contains("Nothing to change", h.Out.ToString(), StringComparison.Ordinal);
        Assert.Single(h.Nft.Writes);
    }

    [Fact]
    public async Task UnblockRemovesOneAndUnblockAllLeavesNoTable()
    {
        var h = new Harness();
        await h.Run("block", "hkg", "sgp");
        Assert.Equal(0, await h.Run("unblock", "hkg"));
        Assert.Equal(["sgp"], h.Firewall.Blocked.Keys);

        Assert.Equal(0, await h.Run("unblock", "--all"));
        Assert.Null(h.Nft.Table);
        Assert.Contains("nothing is blocked", h.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleBlockIsResyncedByTheNextChange()
    {
        var h = new Harness();
        await h.Run("block", "ams");
        h.Nft.Table = h.Nft.Table!.Replace("155.133.248.37", "9.9.9.9", StringComparison.Ordinal); // Valve "changed" a relay
        Assert.Equal(0, await h.Run("block", "sgp"));
        Assert.Contains("~ update  ams    (relay IPs changed: +1 -1)", h.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("155.133.248.37", h.Firewall.Blocked["ams"]);
    }

    [Fact]
    public async Task RelaylessPopExplainsWhatToBlockInstead()
    {
        var h = new Harness();
        Assert.Equal(0, await h.Run("block", "hel", "--dry-run"));
        Assert.Contains("note: hel can't be blocked", h.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("sto (7 ms), sto2 (8 ms)", h.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyRegionPresetSuggestsTheInGameOverride()
    {
        var h = new Harness();
        Assert.Equal(0, await h.Run("preset", "only-region", "eu", "--dry-run"));
        Golden.Match("cli-preset-only-eu-dry-run", h.Out.ToString());
    }

    [Fact]
    public async Task WithinPresetLeavesTimeoutsOpenAndSaysSo()
    {
        var h = new Harness();
        Assert.Equal(0, await h.Run("preset", "within", "100", "--json"));
        using var doc = JsonDocument.Parse(h.Out.ToString());
        var blocked = doc.RootElement.GetProperty("blocked").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("lax", blocked);
        Assert.DoesNotContain("ams", blocked); // 25 ms
        Assert.DoesNotContain("sgp", blocked); // timeout: left open
        Assert.Contains("did not answer pings", doc.RootElement.GetProperty("notes")[0].GetString(), StringComparison.Ordinal);
        Assert.True(doc.RootElement.GetProperty("applied").GetBoolean());
    }

    [Fact]
    public async Task OverBlockingIsWarned()
    {
        var h = new Harness();
        Assert.Equal(0, await h.Run("block", "na", "eu", "asia", "sa", "oce", "me", "--dry-run"));
        Assert.Contains("warning: Only 1 PoP left open (jnb)", h.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangesNeedElevationButDryRunDoesNot()
    {
        var h = new Harness { Elevated = false };
        Assert.Equal(ExitCodes.NeedsElevation, await h.Run("block", "asia"));
        Assert.Contains("--dry-run", h.Err.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await h.Run("block", "asia", "--dry-run"));
        Assert.Contains("assumes nothing is blocked yet", h.Out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(h.Nft.Ran, c => c.Args.Contains("list"));
    }

    [Fact]
    public async Task ResetRemovesEverythingAndVerifies()
    {
        var h = new Harness();
        await h.Run("block", "asia");
        Assert.Equal(0, await h.Run("reset"));
        Assert.Null(h.Nft.Table);
        Assert.Equal(0, await h.Run("reset"));
        Assert.Contains("Nothing to change", h.Out.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("block")]
    [InlineData("unblock")]
    [InlineData("block", "atlantis")]
    [InlineData("preset")]
    [InlineData("preset", "within", "fast")]
    [InlineData("preset", "only-region", "mars")]
    public async Task BadChangeArgumentsAreUsageErrors(params string[] argv)
    {
        var h = new Harness();
        Assert.Equal(ExitCodes.Usage, await h.Run([.. argv, "--dry-run"]));
    }
}
