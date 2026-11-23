using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockServerPicker.Core.Firewall;

/// <summary>
/// Windows Defender Firewall through the NetSecurity PowerShell module (ADR 0002).
/// One PowerShell process per operation; the script goes in on stdin; state comes back as JSON,
/// which is identical on every Windows display language.
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
        $rules = @(Get-NetFirewallRule -Group 'DeadlockServerPicker' -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'DeadlockServerPicker_*' })
        $filters = @($rules | Get-NetFirewallAddressFilter)
        ConvertTo-Json -Compress -Depth 4 -InputObject ([pscustomobject]@{
            Rules = @($filters | ForEach-Object { [pscustomobject]@{ Name = [string]$_.InstanceID; Remote = @($_.RemoteAddress | ForEach-Object { [string]$_ }) } })
            Profiles = @(Get-NetFirewallProfile | ForEach-Object { [pscustomobject]@{ Name = [string]$_.Name; Enabled = [string]$_.Enabled } })
        })

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

    [GeneratedRegex("^DeadlockServerPicker_([a-z0-9]{2,16})_(in|out)$")]
    private static partial Regex RuleName();

    public static FirewallState ParseRead(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var b = new StateBuilder();
        foreach (var rule in doc.RootElement.GetProperty("Rules").EnumerateArray())
        {
            var m = RuleName().Match(rule.GetProperty("Name").GetString() ?? "");
            if (!m.Success)
                continue;
            foreach (var remote in rule.GetProperty("Remote").EnumerateArray())
            {
                if (FirewallText.HostIp(remote.GetString() ?? "") is { } ip)
                    b.Add(m.Groups[1].Value, ip, inbound: m.Groups[2].Value == "in");
                else
                    b.Warnings.Add($"{m.Groups[1].Value}: unexpected remote address '{remote.GetString()}' in rule {m.Value}; re-apply to repair.");
            }
        }
        foreach (var p in doc.RootElement.GetProperty("Profiles").EnumerateArray())
        {
            if (p.GetProperty("Enabled").GetString() != "True")
                b.Warnings.Add($"Windows Defender Firewall is OFF for the {p.GetProperty("Name").GetString()} profile; rules are not enforced there.");
        }
        return b.Build();
    }
}
