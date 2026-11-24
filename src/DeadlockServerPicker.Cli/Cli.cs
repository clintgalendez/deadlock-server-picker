using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DeadlockServerPicker.Core;
using DeadlockServerPicker.Core.Firewall;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Cli;

/// <summary>Everything the CLI touches in the outside world, so tests can swap it.</summary>
public sealed record CliEnv(
    SdrConfigSource Config,
    ICommandRunner Runner,
    IPinger Pinger,
    TextWriter Out,
    TextWriter Err,
    Func<bool> IsElevated,
    TextReader? In = null);

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int ConfigUnavailable = 3;
    public const int NeedsElevation = 4;
}

public static partial class Cli
{
    public const string LimitationsUrl = "https://github.com/clintgalendez/deadlock-server-picker/blob/main/docs/limitations.md";

    public static string Help => $"""
        dsp - {AppInfo.Name} {AppInfo.Version}

        Usage: dsp <command> [options]

        Commands:
          list                 List PoPs grouped by region
          ping                 Ping one relay per PoP
          status               Show what is blocked, read from the firewall
          block <pop|region>...      Block PoPs or whole regions (na eu asia sa oce me af)
          unblock <pop|region>...    Unblock PoPs or regions; `unblock --all` unblocks everything
          preset only-region <region>...   Block every PoP outside these regions
          preset within <ms>         Block every PoP slower than <ms> (timeouts are left open)
          preset unblock-all         Same as `unblock --all`
          reset                Remove every firewall rule this tool created, nothing else
          agent                Privileged helper for the Linux GUI (started via pkexec; JSON lines on stdin)
          version              Show version, config and log file paths
          help                 Show this help

        Options:
          --json               Machine-readable output
          --dry-run            Print the exact firewall commands instead of running them (no admin needed)
          --app <id>           Steam app ID (default {AppInfo.DeadlockAppId} = Deadlock; 730 = Counter-Strike 2)
          --offline            Use the cached SDR config; don't download
          --backend <name>     Force the firewall backend: windows, nftables, iptables

        Changing the firewall needs an elevated terminal (Windows) or sudo (Linux).

        Exit codes: 0 ok, 1 error, 2 usage, 3 SDR config unavailable, 4 needs admin/root
        Blocking is a strong nudge, not a guarantee: {LimitationsUrl}
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> argv, CliEnv env, CancellationToken ct = default)
    {
        CliArgs args;
        try
        {
            args = CliArgs.Parse(argv);
        }
        catch (UsageException e)
        {
            env.Err.WriteLine($"error: {e.Message}");
            return ExitCodes.Usage;
        }

        Log.Info($"dsp {string.Join(' ', argv)}");
        try
        {
            return args.Command switch
            {
                "help" => Print(env, Help),
                "version" => Version(args, env),
                "list" => await ListAsync(args, env, ping: false, ct),
                "ping" => await ListAsync(args, env, ping: true, ct),
                "status" => await StatusAsync(args, env, ct),
                "block" or "unblock" or "preset" => await ChangeAsync(args, env, ct),
                "reset" => await ResetAsync(args, env, ct),
                "agent" => await AgentAsync(args, env, ct),
                _ => throw new UsageException($"Unknown command '{args.Command}'. Run `dsp help`."),
            };
        }
        catch (Exception e) when (e is UsageException or ArgumentException)
        {
            return Fail(args, env, e.Message, ExitCodes.Usage);
        }
        catch (ConfigUnavailableException e)
        {
            return Fail(args, env, e.Message, ExitCodes.ConfigUnavailable);
        }
        catch (FirewallException e)
        {
            Log.Error("firewall error", e);
            return Fail(args, env, e.Message, ExitCodes.Error);
        }
    }

    private static int Print(CliEnv env, string text)
    {
        env.Out.WriteLine(text);
        return ExitCodes.Ok;
    }

    private static int Fail(CliArgs args, CliEnv env, string message, int code)
    {
        if (args.Json)
            env.Out.WriteLine(JsonSerializer.Serialize(new ErrorDto(message, code), CliJson.Default.ErrorDto));
        env.Err.WriteLine($"error: {message}");
        return code;
    }

    private static int Emit<T>(CliEnv env, T value, JsonTypeInfo<T> type)
    {
        env.Out.WriteLine(JsonSerializer.Serialize(value, type));
        return ExitCodes.Ok;
    }

    private static int Version(CliArgs args, CliEnv env)
    {
        var dto = new VersionDto(AppInfo.Version, Paths.ConfigDir, Log.FilePath);
        if (args.Json)
            return Emit(env, dto, CliJson.Default.VersionDto);
        env.Out.WriteLine($"dsp {dto.Version}");
        env.Out.WriteLine($"config dir: {dto.ConfigDir}");
        env.Out.WriteLine($"log file:   {dto.LogFile ?? "(logging disabled)"}");
        return ExitCodes.Ok;
    }

    // ---- list / ping -------------------------------------------------------------------

    private static async Task<LoadedConfig> LoadConfigAsync(CliArgs args, CliEnv env, CancellationToken ct)
    {
        var loaded = await env.Config.LoadAsync(args.AppId, args.Offline, ct);
        if (loaded.Stale)
            env.Err.WriteLine($"warning: using cached SDR config from {Time(loaded.FetchedAt)} ({loaded.StaleReason}). Relay data may be stale.");
        return loaded;
    }

    private static ConfigDto ToDto(LoadedConfig c) => new(c.AppId, c.Config.Revision, c.FetchedAt, c.Stale, c.StaleReason);

    private static PopDto ToDto(Pop p, SdrConfig cfg, long? ping = null, bool? blocked = null)
    {
        var region = Regions.Of(p, cfg);
        return new PopDto(
            p.Code,
            p.Description,
            region.Region.ToString(),
            region.Guessed,
            p.Blockable,
            p.Relays.Count,
            p.Relays.Select(r => r.Ipv4).ToList(),
            p.Blockable ? null : cfg.NearbyBlockable(p.Code).Select(n => new NearbyDto(n.Code, n.Ms)).ToList(),
            ping,
            blocked);
    }

    private static async Task<int> ListAsync(CliArgs args, CliEnv env, bool ping, CancellationToken ct)
    {
        var loaded = await LoadConfigAsync(args, env, ct);
        var cfg = loaded.Config;
        IReadOnlyDictionary<string, long?> pings = new Dictionary<string, long?>();
        if (ping)
        {
            if (!args.Json)
                env.Err.WriteLine($"pinging {cfg.Pops.Values.Count(p => p.Blockable)} PoPs...");
            pings = await Latency.MeasureAsync(cfg.Pops.Values, env.Pinger, ct: ct);
        }

        var pops = cfg.Pops.Values.Select(p => ToDto(p, cfg, pings.GetValueOrDefault(p.Code))).ToList();
        if (args.Json)
            return Emit(env, new ListDto(ToDto(loaded), pops), CliJson.Default.ListDto);

        env.Out.WriteLine($"{AppName(loaded.AppId)} (app {loaded.AppId}) | SDR revision {cfg.Revision} | fetched {Time(loaded.FetchedAt)}{(loaded.Stale ? " | STALE" : "")}");
        foreach (var group in pops.GroupBy(p => Enum.Parse<Region>(p.Region)).OrderBy(g => g.Key))
        {
            env.Out.WriteLine();
            env.Out.WriteLine(Regions.DisplayName(group.Key));
            var ordered = ping
                ? group.OrderBy(p => !p.Blockable).ThenBy(p => p.PingMs is null).ThenBy(p => p.PingMs).ThenBy(p => p.Code, StringComparer.Ordinal)
                : group.OrderBy(p => p.Code, StringComparer.Ordinal);
            foreach (var p in ordered)
                env.Out.WriteLine($"  {p.Code,-6} {Truncate(p.Description, 40),-40} {Detail(p, ping)}");
        }
        return ExitCodes.Ok;
    }

    private static string Detail(PopDto p, bool ping)
    {
        if (!p.Blockable)
        {
            var via = p.ReachedVia is { Count: > 0 } v
                ? " | reached via " + string.Join(", ", v.Select(n => $"{n.Code} ({n.Ms} ms)"))
                : "";
            return "not blockable (no public relays)" + via;
        }
        var s = ping ? (p.PingMs is { } ms ? $"{ms} ms" : "timeout") : $"{p.Relays} relay{(p.Relays == 1 ? "" : "s")}";
        return p.RegionGuessed ? s + " (region guessed)" : s;
    }

    // ---- status --------------------------------------------------------------------------

    private static async Task<int> StatusAsync(CliArgs args, CliEnv env, CancellationToken ct)
    {
        var backend = await FirewallBackends.CreateAsync(env.Runner, args.Backend, ct);
        if (backend.Name != "windows" && !env.IsElevated())
            return Fail(args, env, $"Reading {backend.Name} rules needs root. Re-run with sudo: `sudo dsp status`.", ExitCodes.NeedsElevation);
        var state = await backend.ReadAsync(ct);

        LoadedConfig? loaded = null;
        try
        {
            loaded = await LoadConfigAsync(args, env, ct);
        }
        catch (ConfigUnavailableException e)
        {
            env.Err.WriteLine($"warning: {e.Message} Showing firewall state without comparing it to Valve's relay list.");
        }

        var blocked = state.Blocked
            .Select(kv => new BlockedPopDto(
                kv.Key,
                kv.Value.ToList(),
                loaded?.Config.Pops.TryGetValue(kv.Key, out var pop) == true ? pop.Relays.Select(r => r.Ipv4).ToHashSet().SetEquals(kv.Value) : null))
            .ToList();
        var dto = new StatusDto(backend.Name, blocked, state.Warnings, loaded is null ? null : ToDto(loaded));
        if (args.Json)
            return Emit(env, dto, CliJson.Default.StatusDto);

        env.Out.WriteLine($"Firewall backend: {backend.Name}");
        if (blocked.Count == 0)
            env.Out.WriteLine("Nothing is blocked.");
        else
            env.Out.WriteLine($"Blocked PoPs ({blocked.Count}):");
        foreach (var b in blocked)
        {
            var note = b.MatchesConfig switch
            {
                false => " | relay IPs changed since blocking: run `dsp block " + b.Code + "` again to re-apply",
                null when loaded is not null => " | no longer in Valve's relay list",
                _ => "",
            };
            env.Out.WriteLine($"  {b.Code,-6} {b.Ips.Count} IP{(b.Ips.Count == 1 ? "" : "s")}{note}");
        }
        foreach (var w in state.Warnings)
            env.Out.WriteLine($"warning: {w}");
        return ExitCodes.Ok;
    }

    // ---- agent -----------------------------------------------------------------------------

    private static async Task<int> AgentAsync(CliArgs args, CliEnv env, CancellationToken ct)
    {
        if (!env.IsElevated())
            return Fail(args, env, "`dsp agent` must run as root; the GUI starts it with pkexec.", ExitCodes.NeedsElevation);
        var backend = await FirewallBackends.CreateAsync(env.Runner, args.Backend, ct);
        Log.Info($"agent started ({backend.Name})");
        await using var service = new LocalFirewallService(backend, env.Runner);
        await Agent.RunAsync(env.In ?? Console.In, env.Out, service, ct);
        Log.Info("agent stopped (stdin closed)");
        return ExitCodes.Ok;
    }

    // ---- helpers ---------------------------------------------------------------------------

    public static string AppName(int appId) => appId switch
    {
        AppInfo.DeadlockAppId => "Deadlock",
        730 => "Counter-Strike 2",
        _ => "Steam app",
    };

    private static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
