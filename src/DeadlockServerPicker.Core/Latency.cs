using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Core;

public interface IPinger
{
    /// <summary>Round-trip time in ms, or null on timeout or unreachable.</summary>
    Task<long?> PingAsync(string ipv4, int timeoutMs, CancellationToken ct);
}

/// <summary>
/// ICMP echo through the OS: IcmpSendEcho on Windows (no admin), unprivileged ICMP sockets or the
/// system ping binary on Linux. Never parses localized ping output.
/// </summary>
public sealed class IcmpPinger : IPinger
{
    public async Task<long?> PingAsync(string ipv4, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(IPAddress.Parse(ipv4), TimeSpan.FromMilliseconds(timeoutMs), cancellationToken: ct);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }
}

public static class Latency
{
    /// <summary>
    /// Pings the first relay of every blockable PoP, <paramref name="probes"/> times, at most
    /// <paramref name="concurrency"/> at once (dozens of simultaneous pings trip ISP/CGNAT filters).
    /// Result per PoP code: best RTT in ms, or null for timeout. PoPs without relays are omitted.
    /// A timeout does not mean blocked: some relays drop ICMP.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, long?>> MeasureAsync(
        IEnumerable<Pop> pops, IPinger pinger, int concurrency = 8, int timeoutMs = 1000, int probes = 2, CancellationToken ct = default)
    {
        var results = new ConcurrentDictionary<string, long?>(StringComparer.Ordinal);
        var options = new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct };
        await Parallel.ForEachAsync(pops.Where(p => p.Blockable), options, async (pop, token) =>
        {
            long? best = null;
            for (var i = 0; i < probes; i++)
            {
                var ms = await pinger.PingAsync(pop.Relays[0].Ipv4, timeoutMs, token);
                if (ms is { } v && (best is null || v < best))
                    best = v;
            }
            results[pop.Code] = best;
        });
        return results;
    }
}
