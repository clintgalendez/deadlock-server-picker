using System.Text.Json;
using DeadlockServerPicker.Cli;
using DeadlockServerPicker.Core.Firewall;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Tests;

public class AgentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (FakeNft Nft, LocalFirewallService Service) Local()
    {
        var nft = new FakeNft();
        return (nft, new LocalFirewallService(new NftablesBackend(nft), nft));
    }

    public static TheoryData<string> BadApplies() =>
    [
        """{"op":"apply","blocked":{"AMS":["1.2.3.4"]}}""",
        """{"op":"apply","blocked":{"ams;rm":["1.2.3.4"]}}""",
        """{"op":"apply","blocked":{"ams":["1.2.3.4; nft flush ruleset"]}}""",
        """{"op":"apply","blocked":{"ams":["0.0.0.0/0"]}}""",
        """{"op":"apply","blocked":{"ams":[]}}""",
        """{"op":"apply"}""",
    ];

    [Theory]
    [MemberData(nameof(BadApplies))]
    public async Task ApplyRejectsAnythingButValidatedPopsAndLiteralIpv4(string request)
    {
        var (nft, service) = Local();
        var r = await Agent.HandleAsync(request, service, Ct);
        Assert.False(r.Ok);
        Assert.Empty(nft.Writes);
    }

    [Fact]
    public void ValidationCapsSize()
    {
        var many = Enumerable.Range(0, Agent.MaxIpsPerPop + 1).Select(i => $"10.0.{i / 256}.{i % 256}").ToList();
        Assert.Null(Agent.Validate(new() { ["ams"] = many }));
    }

    [Fact]
    public async Task ApplyReadResetRoundTrip()
    {
        var (nft, service) = Local();

        var applied = await Agent.HandleAsync("""{"op":"apply","blocked":{"ams":["155.133.248.36","155.133.248.37"]}}""", service, Ct);
        Assert.True(applied.Ok);
        Assert.Equal("nftables", applied.Backend);
        Assert.Equal(["155.133.248.36", "155.133.248.37"], applied.State!.Blocked["ams"]);

        var again = await Agent.HandleAsync("""{"op":"apply","blocked":{"ams":["155.133.248.36","155.133.248.37"]}}""", service, Ct);
        Assert.True(again.Ok);
        Assert.Single(nft.Writes); // idempotent: no second write

        var reset = await Agent.HandleAsync("""{"op":"reset"}""", service, Ct);
        Assert.True(reset.Ok);
        Assert.False(reset.State!.Present);
        Assert.Null(nft.Table);
    }

    [Theory]
    [InlineData("""{"op":"format-disk"}""", "unknown op")]
    [InlineData("not json", "bad request")]
    public async Task UnknownOrMalformedRequestsAreErrors(string request, string error)
    {
        var (_, service) = Local();
        var r = await Agent.HandleAsync(request, service, Ct);
        Assert.False(r.Ok);
        Assert.Contains(error, r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void WireFormatRoundTrips()
    {
        var state = States.Current(("ams", ["155.133.248.36"])) with { Warnings = ["w"] };
        var back = Agent.FromWire(Agent.ToWire(state));
        Assert.Equal(state.Blocked.Keys, back.Blocked.Keys);
        Assert.Equal(["w"], back.Warnings);
        Assert.True(back.Present);
    }

    [Fact]
    public async Task CliAgentSpeaksJsonLinesAndExitsOnEof()
    {
        var nft = new FakeNft();
        var input = new StringReader("""
            {"op":"apply","blocked":{"sgp":["103.10.124.116"]}}
            {"op":"read"}
            {"op":"reset"}
            """);
        var output = new StringWriter();
        var env = new CliEnv(new SdrConfigSource(new HttpClient(StubHandler.Fail()), Fixtures.TempDir()), nft, new MapPinger(_ => 1), output, new StringWriter(), () => true, input);

        Assert.Equal(0, await Cli.Cli.RunAsync(["agent", "--backend", "nftables"], env, Ct));

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length); // initial state + 3 responses
        Assert.All(lines, l => Assert.True(JsonDocument.Parse(l).RootElement.GetProperty("ok").GetBoolean()));
        Assert.Contains("103.10.124.116", lines[2], StringComparison.Ordinal);
        Assert.Contains("\"present\":false", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliAgentRefusesWithoutRoot()
    {
        var env = new CliEnv(new SdrConfigSource(new HttpClient(StubHandler.Fail()), Fixtures.TempDir()), new FakeNft(), new MapPinger(_ => 1), new StringWriter(), new StringWriter(), () => false, new StringReader(""));
        Assert.Equal(ExitCodes.NeedsElevation, await Cli.Cli.RunAsync(["agent", "--backend", "nftables"], env, Ct));
    }
}
