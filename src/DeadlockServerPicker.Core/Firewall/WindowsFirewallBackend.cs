using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockServerPicker.Core.Firewall;

/// <summary>
/// Windows Defender Firewall through the NetSecurity PowerShell module (ADR 0002).
/// One PowerShell process per operation; the script goes in on stdin; state comes back as JSON,
/// which is identical on every Windows display language. Per blocked PoP there are two rules,
/// <c>DeadlockServerPicker_&lt;pop&gt;_out</c> and <c>_in</c>, in group <c>DeadlockServerPicker</c>.
/// </summary>
public sealed partial class WindowsFirewallBackend(ICommandRunner runner, string powershell = "powershell.exe") : IFirewallBackend
{
    public const string Prefix = "DeadlockServerPicker_";
    public const string Group = "DeadlockServerPicker";

    public static string DefaultPowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public string Name => "windows";

    private const string Preamble = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        Remove-TypeData -TypeName System.Array -ErrorAction SilentlyContinue

        """;

    private const string ReadBody = """
        $service = [string](Get-Service -Name MpsSvc -ErrorAction SilentlyContinue).Status
        $thirdParty = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName FirewallProduct -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.displayName })
        $rules = @(Get-NetFirewallRule -Group 'DeadlockServerPicker' -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'DeadlockServerPicker_*' })
        $filters = @($rules | Get-NetFirewallAddressFilter)
        ConvertTo-Json -Compress -Depth 4 -InputObject ([pscustomobject]@{
            Service = $service
            ThirdParty = $thirdParty
            Rules = @($filters | ForEach-Object { [pscustomobject]@{ Name = [string]$_.InstanceID; Remote = @($_.RemoteAddress | ForEach-Object { [string]$_ }) } })
            Profiles = @(Get-NetFirewallProfile -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ Name = [string]$_.Name; Enabled = [string]$_.Enabled } })
        })

        """;

    private const string ResetBody = """
        Get-NetFirewallRule -Group 'DeadlockServerPicker' -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'DeadlockServerPicker_*' } | Remove-NetFirewallRule

        """;

    public Command Script(string body) => new(
        powershell,
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "& ([scriptblock]::Create([Console]::In.ReadToEnd()))"],
        Preamble + body);

    public Command ReadCommand => Script(ReadBody);

    public async Task<FirewallState> ReadAsync(CancellationToken ct = default)
    {
        var r = await runner.RunAsync(ReadCommand, ct);
        if (r.ExitCode != 0)
            throw new FirewallException($"Reading Windows Firewall rules failed (exit {r.ExitCode}): {r.Stderr.Trim()} Is the Windows Defender Firewall service running?");
        return ParseRead(r.Stdout);
    }

    public static string RuleName(string pop, bool inbound) => $"{Prefix}{pop}_{(inbound ? "in" : "out")}";

    public IReadOnlyList<Command> PlanApply(FirewallState current, DesiredState desired)
    {
        var changes = Planner.Diff(current, desired);
        if (changes.Count == 0)
            return [];

        // Every changed PoP is removed first (also clears half-applied or malformed leftovers), then
        // the desired rules are created. All inside one script so it runs in one PowerShell process.
        var sb = new StringBuilder();
        sb.Append("Remove-NetFirewallRule -ErrorAction SilentlyContinue -Name @(")
          .AppendJoin(", ", changes.SelectMany(c => new[] { Quote(RuleName(c.Pop, false)), Quote(RuleName(c.Pop, true)) }))
          .Append(")\n");
        foreach (var c in changes.Where(c => c.Kind != ChangeKind.Remove))
        {
            foreach (var inbound in new[] { false, true })
            {
                sb.Append("New-NetFirewallRule")
                  .Append(" -Name ").Append(Quote(RuleName(c.Pop, inbound)))
                  .Append(" -DisplayName ").Append(Quote(Prefix + c.Pop))
                  .Append(" -Group ").Append(Quote(Group))
                  .Append(" -Description ").Append(Quote($"Blocks Steam Datagram Relay PoP {c.Pop}. Added by Deadlock Server Picker; remove with: dsp reset"))
                  .Append(" -Direction ").Append(inbound ? "Inbound" : "Outbound")
                  .Append(" -Action Block -Protocol Any -Profile Any -Enabled True")
                  .Append(" -RemoteAddress @(").AppendJoin(", ", c.After.Select(Quote)).Append(')')
                  .Append(" | Out-Null\n");
            }
        }
        return [Script(sb.ToString())];
    }

    public IReadOnlyList<Command> PlanReset(FirewallState current) => current.Present ? [Script(ResetBody)] : [];

    /// <summary>
    /// Single-quoted PowerShell literal. Inputs are already validated (PoP codes [a-z0-9], dotted-quad
    /// IPs, constant text), so this is defence in depth.
    /// </summary>
    private static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^DeadlockServerPicker_([a-z0-9]{2,16})_(in|out)$")]
    private static partial Regex RuleNamePattern();

    public static FirewallState ParseRead(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var b = new StateBuilder();
        var rules = doc.RootElement.GetProperty("Rules");
        foreach (var rule in rules.EnumerateArray())
        {
            var name = rule.GetProperty("Name").GetString() ?? "";
            var m = RuleNamePattern().Match(name);
            if (!m.Success)
            {
                b.Warnings.Add($"Unrecognised rule '{name}' in group {Group}; `dsp reset` removes it.");
                continue;
            }
            var pop = m.Groups[1].Value;
            b.Touch(pop);
            foreach (var remote in rule.GetProperty("Remote").EnumerateArray())
            {
                if (FirewallText.HostIp(remote.GetString() ?? "") is { } ip)
                    b.Add(pop, ip, inbound: m.Groups[2].Value == "in");
                else
                    b.Warnings.Add($"{pop}: unexpected remote address '{remote.GetString()}' in rule {name}; re-apply to repair.");
            }
        }
        foreach (var p in doc.RootElement.GetProperty("Profiles").EnumerateArray())
        {
            if (p.GetProperty("Enabled").GetString() != "True")
                b.Warnings.Add($"Windows Defender Firewall is OFF for the {p.GetProperty("Name").GetString()} profile; rules are not enforced there. Turn it on in Windows Security > Firewall & network protection.");
        }
        if (doc.RootElement.TryGetProperty("Service", out var service) && service.GetString() is { Length: > 0 } status && status != "Running")
            b.Warnings.Add($"The Windows Defender Firewall service (MpsSvc) is {status}, so no rules are enforced. Start it (services.msc) or restart Windows.");
        if (doc.RootElement.TryGetProperty("ThirdParty", out var products) && products.GetArrayLength() > 0)
        {
            var names = string.Join(", ", products.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
            b.Warnings.Add($"Another firewall is registered with Windows: {names}. Security suites often take over from Windows Defender Firewall, and then these rules are not enforced. Use that product's own blocking, or see docs/troubleshooting.md.");
        }
        return b.Build(present: rules.GetArrayLength() > 0);
    }
}
