using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Core.Firewall;

// The Linux GUI never runs as root. It starts ONE privileged `dsp agent` per session through pkexec
// (one password prompt) and talks to it with JSON lines on stdin/stdout. The agent only accepts
// high-level, validated operations (read / apply a desired PoP->IPs map / reset) and generates every
// firewall command itself, so a compromised GUI can at worst block or unblock IPv4 addresses inside
// our own namespace. See ADR 0003.

public sealed record AgentRequest(string Op, Dictionary<string, List<string>>? Blocked = null);

public sealed record AgentState(bool Present, Dictionary<string, List<string>> Blocked, List<string> Warnings);

public sealed record AgentResponse(bool Ok, string? Backend = null, AgentState? State = null, string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentResponse))]
internal sealed partial class AgentJson : JsonSerializerContext;

/// <summary>Firewall operations as the GUI sees them: read, apply a desired state (planned and verified), reset.</summary>
public interface IFirewallService : IAsyncDisposable
{
    string? Backend { get; }
    Task<FirewallState> ReadAsync(CancellationToken ct = default);
    Task<FirewallState> ApplyAsync(DesiredState desired, CancellationToken ct = default);
    Task<FirewallState> ResetAsync(CancellationToken ct = default);
}

/// <summary>In-process firewall access (Windows GUI elevated by its manifest, or anything already root).</summary>
public sealed class LocalFirewallService(IFirewallBackend backend, ICommandRunner runner) : IFirewallService
{
    public string? Backend => backend.Name;

    public Task<FirewallState> ReadAsync(CancellationToken ct = default) => backend.ReadAsync(ct);

    public async Task<FirewallState> ApplyAsync(DesiredState desired, CancellationToken ct = default)
    {
        var current = await backend.ReadAsync(ct);
        var commands = backend.PlanApply(current, desired);
        return commands.Count == 0 ? current : await FirewallBackends.ApplyAndVerifyAsync(backend, runner, commands, desired, ct);
    }

    public async Task<FirewallState> ResetAsync(CancellationToken ct = default)
    {
        await FirewallBackends.ResetAndVerifyAsync(backend, runner, await backend.ReadAsync(ct), ct);
        return await backend.ReadAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Server side of the agent protocol (`dsp agent`): one request line in, one response line out.</summary>
public static class Agent
{
    public const int MaxPops = 256;
    public const int MaxIpsPerPop = 256;

    public static async Task RunAsync(TextReader input, TextWriter output, IFirewallService service, CancellationToken ct = default)
    {
        await WriteAsync(output, await Safe(async () => Ok(service, await service.ReadAsync(ct))));
        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
                continue;
            await WriteAsync(output, await HandleAsync(line, service, ct));
        }
    }

    public static async Task<AgentResponse> HandleAsync(string line, IFirewallService service, CancellationToken ct = default)
    {
        AgentRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(line, AgentJson.Default.AgentRequest);
        }
        catch (JsonException e)
        {
            return new AgentResponse(false, Error: $"bad request: {e.Message}");
        }
        return req?.Op switch
        {
            "read" => await Safe(async () => Ok(service, await service.ReadAsync(ct))),
            "reset" => await Safe(async () => Ok(service, await service.ResetAsync(ct))),
            "apply" => Validate(req.Blocked) is { } desired
                ? await Safe(async () => Ok(service, await service.ApplyAsync(desired, ct)))
                : new AgentResponse(false, Error: "apply rejected: blocked must map valid PoP codes to literal IPv4 addresses"),
            _ => new AgentResponse(false, Error: $"unknown op '{req?.Op}'"),
        };
    }

    /// <summary>Trust boundary: the same rules as the SDR config parser, plus size limits.</summary>
    public static DesiredState? Validate(Dictionary<string, List<string>>? blocked)
    {
        if (blocked is null || blocked.Count > MaxPops)
            return null;
        var result = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var (pop, ips) in blocked)
        {
            if (!SdrConfig.IsValidPopCode(pop) || ips.Count == 0 || ips.Count > MaxIpsPerPop)
                return null;
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var ip in ips)
            {
                if (SdrConfig.NormalizeIpv4(ip) is not { } valid)
                    return null;
                set.Add(valid);
            }
            result[pop] = set;
        }
        return new DesiredState(result);
    }

    public static AgentState ToWire(FirewallState s) =>
        new(s.Present, s.Blocked.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()), [.. s.Warnings]);

    public static FirewallState FromWire(AgentState s) => new(
        new SortedDictionary<string, IReadOnlySet<string>>(
            s.Blocked.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)new SortedSet<string>(kv.Value, StringComparer.Ordinal)),
            StringComparer.Ordinal),
        s.Warnings,
        [],
        s.Present);

    private static AgentResponse Ok(IFirewallService service, FirewallState state) => new(true, service.Backend, ToWire(state));

    private static async Task<AgentResponse> Safe(Func<Task<AgentResponse>> action)
    {
        try
        {
            return await action();
        }
        catch (FirewallException e)
        {
            Log.Error("agent", e);
            return new AgentResponse(false, Error: e.Message);
        }
    }

    private static async Task WriteAsync(TextWriter output, AgentResponse response)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(response, AgentJson.Default.AgentResponse));
        await output.FlushAsync();
    }
}

/// <summary>
/// Client side: starts `pkexec &lt;dsp&gt; agent` once (one authentication prompt per session) and sends
/// requests over its stdin/stdout. The first response is an initial read, so a cancelled password
/// prompt surfaces on the first call.
/// </summary>
public sealed class AgentFirewallService(string dspPath, string? backend = null, string launcher = "pkexec") : IFirewallService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public string? Backend { get; private set; }

    public async Task<FirewallState> ReadAsync(CancellationToken ct = default) =>
        await EnsureStartedAsync(ct) ?? await SendAsync(new AgentRequest("read"), ct);

    public async Task<FirewallState> ApplyAsync(DesiredState desired, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);
        return await SendAsync(new AgentRequest("apply", desired.Blocked.ToDictionary(kv => kv.Key, kv => kv.Value.ToList())), ct);
    }

    public async Task<FirewallState> ResetAsync(CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);
        return await SendAsync(new AgentRequest("reset"), ct);
    }

    /// <summary>Starts the agent if it isn't running and returns its initial state; null if it was already running.</summary>
    private async Task<FirewallState?> EnsureStartedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: false })
                return null;
            var psi = new ProcessStartInfo(launcher)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(dspPath);
            psi.ArgumentList.Add("agent");
            if (backend is not null)
            {
                psi.ArgumentList.Add("--backend");
                psi.ArgumentList.Add(backend);
            }
            Log.Info($"starting firewall agent: {launcher} {dspPath} agent");
            try
            {
                _process = Process.Start(psi) ?? throw new FirewallException("Could not start the firewall helper.");
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new FirewallException($"Could not run {launcher} ({e.Message}). Install polkit, or run `sudo ./dsp ...` from a terminal instead.");
            }
            return Unwrap(await ReadResponseAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FirewallState> SendAsync(AgentRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_process is not { HasExited: false })
                throw new FirewallException("The firewall helper is not running.");
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, AgentJson.Default.AgentRequest));
            await _process.StandardInput.FlushAsync(ct);
            return Unwrap(await ReadResponseAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AgentResponse> ReadResponseAsync(CancellationToken ct)
    {
        var line = await _process!.StandardOutput.ReadLineAsync(ct);
        if (line is null)
        {
            await _process.WaitForExitAsync(ct);
            var err = (await _process.StandardError.ReadToEndAsync(ct)).Trim();
            var code = _process.ExitCode;
            _process.Dispose();
            _process = null;
            throw new FirewallException(code is 126 or 127
                ? "Authentication was cancelled or refused, so the firewall can't be read or changed."
                : $"The firewall helper stopped (exit {code}). {err}");
        }
        return JsonSerializer.Deserialize(line, AgentJson.Default.AgentResponse) ?? throw new FirewallException("Empty response from the firewall helper.");
    }

    private FirewallState Unwrap(AgentResponse r)
    {
        if (!r.Ok || r.State is null)
            throw new FirewallException(r.Error ?? "The firewall helper reported an error.");
        Backend = r.Backend;
        return Agent.FromWire(r.State);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false } p)
        {
            p.StandardInput.Close(); // the agent exits on EOF
            try
            {
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Log.Warn("firewall agent did not exit within 5 s");
            }
        }
        _process?.Dispose();
        _gate.Dispose();
    }
}
