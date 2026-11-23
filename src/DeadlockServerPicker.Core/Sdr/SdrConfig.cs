using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockServerPicker.Core.Sdr;

public sealed record Relay(string Ipv4, int PortStart, int PortEnd);

/// <summary>A Steam Datagram Relay point of presence. Geo is [lon, lat] as published (often wrong; see docs/research.md).</summary>
public sealed record Pop(string Code, string Description, double[]? Geo, int Tier, IReadOnlyList<string> Aliases, IReadOnlyList<Relay> Relays)
{
    /// <summary>PoPs without relays can't be blocked: Valve publishes no addresses for them.</summary>
    public bool Blockable => Relays.Count > 0;
}

public sealed record TypicalPing(string A, string B, int Ms);

public sealed partial record SdrConfig(long Revision, IReadOnlyDictionary<string, Pop> Pops, IReadOnlyList<TypicalPing> TypicalPings)
{
    /// <summary>
    /// Parses a GetSDRConfig response. This is a trust boundary: PoP codes and relay IPs end up in
    /// firewall commands, so codes must match <see cref="PopCodePattern"/> and IPs must be literal IPv4.
    /// Anything else is dropped. Unknown fields are ignored.
    /// </summary>
    public static SdrConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("SDR config is not a JSON object.");
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new FormatException("SDR config reports success=false.");
        if (!root.TryGetProperty("revision", out var rev) || !rev.TryGetInt64(out var revision))
            throw new FormatException("SDR config has no numeric 'revision'.");
        if (!root.TryGetProperty("pops", out var popsEl) || popsEl.ValueKind != JsonValueKind.Object)
            throw new FormatException("SDR config has no 'pops' object.");

        var pops = new SortedDictionary<string, Pop>(StringComparer.Ordinal);
        foreach (var p in popsEl.EnumerateObject())
        {
            if (!IsValidPopCode(p.Name) || p.Value.ValueKind != JsonValueKind.Object)
                continue;
            pops[p.Name] = ParsePop(p.Name, p.Value);
        }
        if (pops.Count == 0)
            throw new FormatException("SDR config contains no PoPs.");

        var pings = new List<TypicalPing>();
        if (root.TryGetProperty("typical_pings", out var tp) && tp.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tp.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() == 3
                    && t[0].ValueKind == JsonValueKind.String && t[1].ValueKind == JsonValueKind.String
                    && t[2].TryGetInt32(out var ms))
                    pings.Add(new TypicalPing(t[0].GetString()!, t[1].GetString()!, ms));
            }
        }
        return new SdrConfig(revision, pops, pings);
    }

    public static bool IsValidPopCode(string code) => PopCodePattern().IsMatch(code);

    [GeneratedRegex("^[a-z0-9]{2,16}$")]
    private static partial Regex PopCodePattern();

    private static Pop ParsePop(string code, JsonElement el)
    {
        var desc = el.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : code;
        double[]? geo = null;
        if (el.TryGetProperty("geo", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() == 2
            && g[0].TryGetDouble(out var a) && g[1].TryGetDouble(out var b))
            geo = [a, b];
        var tier = el.TryGetProperty("tier", out var t) && t.TryGetInt32(out var ti) ? ti : 0;

        var aliases = new List<string>();
        if (el.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Array)
            foreach (var x in al.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && IsValidPopCode(x.GetString()!))
                    aliases.Add(x.GetString()!);

        var relays = new List<Relay>();
        if (el.TryGetProperty("relays", out var rs) && rs.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rs.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("ipv4", out var ip) || ip.ValueKind != JsonValueKind.String)
                    continue;
                var ipv4 = NormalizeIpv4(ip.GetString()!);
                if (ipv4 is null || relays.Any(x => x.Ipv4 == ipv4))
                    continue;
                int lo = 0, hi = 0;
                if (r.TryGetProperty("port_range", out var pr) && pr.ValueKind == JsonValueKind.Array && pr.GetArrayLength() == 2)
                {
                    pr[0].TryGetInt32(out lo);
                    pr[1].TryGetInt32(out hi);
                }
                relays.Add(new Relay(ipv4, lo, hi));
            }
        }
        return new Pop(code, desc, geo, tier, aliases, relays);
    }

    /// <summary>Returns the canonical dotted-quad form, or null unless the input is exactly a literal IPv4 address.</summary>
    public static string? NormalizeIpv4(string s)
    {
        if (!IPAddress.TryParse(s, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
            return null;
        var canonical = addr.ToString();
        // Reject forms IPAddress accepts but that aren't plain dotted quads ("1", "0x7f.1", "1.2.3").
        return canonical == s ? canonical : null;
    }

    /// <summary>Blockable PoPs whose typical ping to <paramref name="code"/> is within <paramref name="maxMs"/>, nearest first.</summary>
    public IReadOnlyList<(string Code, int Ms)> NearbyBlockable(string code, int maxMs = 15) =>
        TypicalPings
            .Select(t => t.A == code ? (Code: t.B, t.Ms) : t.B == code ? (Code: t.A, t.Ms) : (Code: "", Ms: int.MaxValue))
            .Where(x => x.Ms <= maxMs && Pops.TryGetValue(x.Code, out var p) && p.Blockable)
            .OrderBy(x => x.Ms).ThenBy(x => x.Code, StringComparer.Ordinal)
            .ToList();
}
