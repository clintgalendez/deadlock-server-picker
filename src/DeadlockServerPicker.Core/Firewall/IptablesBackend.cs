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

    public IReadOnlyList<Command> PlanApply(FirewallState current, DesiredState desired) =>
        throw new NotSupportedException("iptables apply lands with #15.");

    public IReadOnlyList<Command> PlanReset(FirewallState current) =>
        throw new NotSupportedException("iptables reset lands with #15.");
}
