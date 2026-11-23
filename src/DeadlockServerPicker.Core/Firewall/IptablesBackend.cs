using System.Text;
using System.Text.RegularExpressions;

namespace DeadlockServerPicker.Core.Firewall;

/// <summary>
/// iptables fallback: our own chain <c>DSP_DEADLOCK_SERVER_PICKER</c>, jumped to at position 1 of OUTPUT
/// and INPUT, with every rule tagged <c>-m comment --comment dsp:&lt;pop&gt;</c> (ADR 0002).
/// </summary>
public sealed partial class IptablesBackend(ICommandRunner runner) : IFirewallBackend
{
    public const string Chain = "DSP_DEADLOCK_SERVER_PICKER";
    public static readonly IReadOnlyList<string> HookChains = ["OUTPUT", "INPUT"];

    public string Name => "iptables";

    public static Command ListRules { get; } = new("iptables", ["-w", "-S"]);

    public async Task<FirewallState> ReadAsync(CancellationToken ct = default)
    {
        var r = await runner.RunAsync(ListRules, ct);
        if (r.ExitCode != 0)
            throw FirewallText.Failure("iptables -S", r);
        return Parse(r.Stdout);
    }

    [GeneratedRegex(@"^-A (?<chain>\S+) (?<rest>.*)$")]
    private static partial Regex AppendLine();

    [GeneratedRegex(@"^-(?<dir>[ds]) (?<ip>\S+) -m comment --comment ""?dsp:(?<pop>[a-z0-9]{2,16})""? -j DROP$")]
    private static partial Regex OurRule();

    public static FirewallState Parse(string listRules)
    {
        var hooks = new List<string>();
        var present = false;
        var b = new StateBuilder();
        var rules = new List<(string Pop, string Ip, bool Inbound)>();
        foreach (var line in listRules.Split('\n', StringSplitOptions.TrimEntries))
        {
            var m = AppendLine().Match(line);
            if (!m.Success)
                continue;
            var chain = m.Groups["chain"].Value;
            var rest = m.Groups["rest"].Value;
            if (HookChains.Contains(chain) && rest == $"-j {Chain}")
                hooks.Add(chain);
            else if (chain == Chain && OurRule().Match(rest) is { Success: true } r && FirewallText.HostIp(r.Groups["ip"].Value) is { } ip)
                rules.Add((r.Groups["pop"].Value, ip, r.Groups["dir"].Value == "s"));
        }

        // A direction only counts as blocked if its chain actually jumps to ours.
        foreach (var (pop, ip, inbound) in rules)
        {
            if (hooks.Contains(inbound ? "INPUT" : "OUTPUT"))
                b.Add(pop, ip, inbound);
        }
        if (rules.Count > 0)
        {
            foreach (var missing in HookChains.Where(h => !hooks.Contains(h)))
                b.Warnings.Add($"The jump from {missing} to {Chain} is missing, so those rules are not enforced; re-apply to repair.");
        }
        return b.Build(present || hooks.Count > 0, hooks);
    }

    /// <summary>
    /// Replaces our chain's contents atomically with <c>iptables-restore --noflush</c> (declaring the
    /// chain creates it or, if it exists, flushes it), then inserts the jumps at position 1 of OUTPUT and
    /// INPUT only where the read showed them missing. Unblocking everything removes chain and jumps.
    /// </summary>
    public IReadOnlyList<Command> PlanApply(FirewallState current, DesiredState desired)
    {
        if (desired.Blocked.Count == 0)
            return PlanReset(current);
        var missingHooks = HookChains.Where(h => !current.Hooks.Contains(h)).ToList();
        if (Planner.Diff(current, desired).Count == 0 && missingHooks.Count == 0)
            return [];

        var sb = new StringBuilder("*filter\n");
        sb.Append(':').Append(Chain).Append(" - [0:0]\n");
        sb.Append("-F ").Append(Chain).Append('\n');
        foreach (var (pop, ips) in desired.Blocked)
        {
            foreach (var ip in ips)
            {
                sb.Append($"-A {Chain} -d {ip}/32 -m comment --comment dsp:{pop} -j DROP\n");
                sb.Append($"-A {Chain} -s {ip}/32 -m comment --comment dsp:{pop} -j DROP\n");
            }
        }
        sb.Append("COMMIT\n");

        var commands = new List<Command> { new("iptables-restore", ["-w", "--noflush"], sb.ToString()) };
        commands.AddRange(missingHooks.Select(h => new Command("iptables", ["-w", "-I", h, "1", "-j", Chain])));
        return commands;
    }

    public IReadOnlyList<Command> PlanReset(FirewallState current)
    {
        if (!current.Present)
            return [];
        var commands = current.Hooks.Select(h => new Command("iptables", ["-w", "-D", h, "-j", Chain])).ToList();
        commands.Add(new Command("iptables", ["-w", "-F", Chain]));
        commands.Add(new Command("iptables", ["-w", "-X", Chain]));
        return commands;
    }
}
