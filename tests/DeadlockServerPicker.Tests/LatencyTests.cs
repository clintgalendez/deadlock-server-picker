using DeadlockServerPicker.Core;

namespace DeadlockServerPicker.Tests;

public class LatencyTests
{
    private sealed class FakePinger(Func<string, long?> rtt) : IPinger
    {
        private int _inFlight;
        public int MaxInFlight;
        public int Calls;

        public async Task<long?> PingAsync(string ipv4, int timeoutMs, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref MaxInFlight, now);
            Interlocked.Increment(ref Calls);
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref _inFlight);
            return rtt(ipv4);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    [Fact]
    public async Task PingsFirstRelayOfEveryBlockablePopWithinTheConcurrencyLimit()
    {
        var pops = Fixtures.Deadlock.Pops.Values;
        var pinger = new FakePinger(_ => 42);

        var results = await Latency.MeasureAsync(pops, pinger, concurrency: 4, probes: 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(33, results.Count);
        Assert.DoesNotContain("hel", results.Keys);
        Assert.Equal(66, pinger.Calls);
        Assert.InRange(pinger.MaxInFlight, 1, 4);
        Assert.All(results.Values, v => Assert.Equal(42, v));
    }

    [Fact]
    public async Task ReportsBestProbeAndNullForTimeouts()
    {
        var ams = Fixtures.Deadlock.Pops["ams"];
        var fra = Fixtures.Deadlock.Pops["fra"];
        var calls = 0;
        var pinger = new FakePinger(ip => ip == fra.Relays[0].Ipv4 ? null : Interlocked.Increment(ref calls) == 1 ? 50 : 30);

        var results = await Latency.MeasureAsync([ams, fra], pinger, concurrency: 1, probes: 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(30, results["ams"]);
        Assert.Null(results["fra"]);
    }
}
