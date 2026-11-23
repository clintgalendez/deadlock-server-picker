using System.Text.RegularExpressions;

namespace DeadlockServerPicker.Core.Firewall;

/// <summary>
/// nftables: our own table <c>inet deadlock_server_picker</c> with one IPv4 set per blocked PoP and
/// drop rules in our own output/input base chains (ADR 0002).
/// </summary>
public sealed partial class NftablesBackend(ICommandRunner runner) : IFirewallBackend
{
    public const string Table = "deadlock_server_picker";

    public string Name => "nftables";

    public static Command ListTables { get; } = new("nft", ["list", "tables"]);
    public static Command ListTable { get; } = new("nft", ["list", "table", "inet", Table]);

    public async Task<FirewallState> ReadAsync(CancellationToken ct = default)
    {
        var tables = await runner.RunAsync(ListTables, ct);
        if (tables.ExitCode != 0)
            throw FirewallText.Failure("nft list tables", tables);
        if (!HasOurTable(tables.Stdout))
            return FirewallState.Empty;
        var table = await runner.RunAsync(ListTable, ct);
        if (table.ExitCode != 0)
            throw FirewallText.Failure("nft list table", table);
        return Parse(table.Stdout);
    }

    public static bool HasOurTable(string listTables) =>
        listTables.Split('\n').Any(l => l.Trim() == $"table inet {Table}");

    [GeneratedRegex(@"set pop_(?<pop>[a-z0-9]{2,16})\s*\{[^{}]*?(?:elements\s*=\s*\{(?<el>[^}]*)\})?[^{}]*\}")]
    private static partial Regex SetBlock();

    [GeneratedRegex(@"chain (?<name>output|input)\s*\{(?<body>[^{}]*)\}")]
    private static partial Regex ChainBlock();

    [GeneratedRegex(@"ip (?<dir>daddr|saddr) @pop_(?<pop>[a-z0-9]{2,16}) drop")]
    private static partial Regex DropRule();

    public static FirewallState Parse(string listTable)
    {
        var hooked = new HashSet<(string Pop, bool Inbound)>();
        foreach (Match chain in ChainBlock().Matches(listTable))
        {
            var inbound = chain.Groups["name"].Value == "input";
            foreach (Match rule in DropRule().Matches(chain.Groups["body"].Value))
            {
                if (rule.Groups["dir"].Value == (inbound ? "saddr" : "daddr"))
                    hooked.Add((rule.Groups["pop"].Value, inbound));
            }
        }

        var b = new StateBuilder();
        foreach (Match set in SetBlock().Matches(listTable))
        {
            var pop = set.Groups["pop"].Value;
            b.Touch(pop);
            var ips = set.Groups["el"].Value.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in ips)
            {
                if (Sdr.SdrConfig.NormalizeIpv4(raw) is not { } ip)
                    continue;
                if (hooked.Contains((pop, false)))
                    b.Add(pop, ip, inbound: false);
                if (hooked.Contains((pop, true)))
                    b.Add(pop, ip, inbound: true);
            }
            if (!hooked.Contains((pop, false)) || !hooked.Contains((pop, true)))
                b.Warnings.Add($"{pop}: set exists but is not referenced by both drop rules; re-apply to repair.");
        }
        return b.Build(present: true);
    }

    public IReadOnlyList<Command> PlanApply(FirewallState current, DesiredState desired) =>
        throw new NotSupportedException("nftables apply lands with #14.");

    public IReadOnlyList<Command> PlanReset(FirewallState current) =>
        throw new NotSupportedException("nftables reset lands with #14.");
}
