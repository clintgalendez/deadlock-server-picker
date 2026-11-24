using System.Text.Json;
using DeadlockServerPicker.Core;
using DeadlockServerPicker.Core.Firewall;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Cli;

/// <summary>block / unblock / preset / reset: plan against the real firewall state, then dry-run or apply + verify.</summary>
public static partial class Cli
{
    private sealed record Plan(DesiredState Desired, List<string> Notes, string? Tip = null);

    private static async Task<int> ChangeAsync(CliArgs args, CliEnv env, CancellationToken ct)
    {
        var backend = await FirewallBackends.CreateAsync(env.Runner, args.Backend, ct);
        if (!args.DryRun && !env.IsElevated())
            return Fail(args, env, Elevation.Hint + " Add --dry-run to preview the commands without admin rights.", ExitCodes.NeedsElevation);

        var loaded = await LoadConfigAsync(args, env, ct);
        var cfg = loaded.Config;
        var (current, readNote) = await ReadForPlanAsync(backend, env, ct);
        var plan = await BuildPlanAsync(args, env, cfg, current, ct);
        if (readNote is not null)
            plan.Notes.Insert(0, readNote);

        var changes = Planner.Diff(current, plan.Desired);
        var commands = backend.PlanApply(current, plan.Desired);
        var warnings = new List<string>();
        if (Planner.OverBlockingWarning(cfg, plan.Desired) is { } overBlocking)
            warnings.Add(overBlocking);
        if (loaded.Stale)
            warnings.Add("The relay list is from the cache and may be out of date; blocks use the cached relay IPs.");

        if (!args.DryRun && commands.Count > 0)
        {
            Log.Info($"applying {changes.Count} change(s) with {backend.Name}");
            var after = await FirewallBackends.ApplyAndVerifyAsync(backend, env.Runner, commands, plan.Desired, ct);
            warnings.AddRange(after.Warnings);
        }
        return Report(args, env, backend.Name, changes, commands, plan.Desired.Blocked.Keys, plan.Notes, warnings, plan.Tip);
    }

    /// <summary>
    /// Reads the firewall. On Linux without root a --dry-run cannot read it, so it plans against an
    /// empty firewall and says so.
    /// </summary>
    private static async Task<(FirewallState State, string? Note)> ReadForPlanAsync(IFirewallBackend backend, CliEnv env, CancellationToken ct)
    {
        if (backend.Name != "windows" && !env.IsElevated())
            return (FirewallState.Empty, "Not running as root, so the current firewall state could not be read; this dry run assumes nothing is blocked yet.");
        return (await backend.ReadAsync(ct), null);
    }

    private static async Task<Plan> BuildPlanAsync(CliArgs args, CliEnv env, SdrConfig cfg, FirewallState current, CancellationToken ct)
    {
        switch (args.Command)
        {
            case "block":
                {
                    if (args.Positional.Count == 0)
                        throw new UsageException("Name PoPs or regions to block, e.g. `dsp block asia sa` or `dsp block hkg sgp`.");
                    var sel = Planner.Resolve(cfg, args.Positional);
                    return new Plan(Planner.Block(cfg, current, sel.Pops), [.. sel.Notes]);
                }
            case "unblock":
                {
                    if (args.All)
                        return new Plan(DesiredState.Empty, []);
                    if (args.Positional.Count == 0)
                        throw new UsageException("Name PoPs or regions to unblock, or use `dsp unblock --all`.");
                    var sel = Planner.Resolve(cfg, args.Positional);
                    return new Plan(Planner.Unblock(cfg, current, sel.Pops), []);
                }
            case "preset":
                return await PresetAsync(args, env, cfg, ct);
            case "resync":
                return new Plan(Planner.Resync(cfg, current), [.. Planner.StaleBlocks(cfg, current).Select(s => s.Describe())]);
            default:
                throw new UsageException($"Unknown command '{args.Command}'.");
        }
    }

    private static async Task<Plan> PresetAsync(CliArgs args, CliEnv env, SdrConfig cfg, CancellationToken ct)
    {
        var name = args.Positional.FirstOrDefault();
        var rest = args.Positional.Skip(1).ToList();
        switch (name)
        {
            case "only-region":
                {
                    if (rest.Count == 0)
                        throw new UsageException("Name the region(s) to keep, e.g. `dsp preset only-region eu`.");
                    var keep = new HashSet<Region>();
                    foreach (var r in rest)
                        keep.Add(Regions.TryParse(r, out var region) ? region : throw new UsageException($"'{r}' is not a region. Use na, eu, asia, sa, oce, me or af."));
                    string? tip = null;
                    if (args.AppId == AppInfo.DeadlockAppId && keep.Count == 1 && Regions.CitadelRegionOverride(keep.Single()) is { } value)
                        tip = $"Also set Deadlock's own region preference: open the console (F7) and run `citadel_region_override {value}` ({Regions.DisplayName(keep.Single())}).";
                    return new Plan(Planner.OnlyRegions(cfg, keep), [], tip);
                }
            case "within":
                {
                    if (rest.Count != 1 || !int.TryParse(rest[0], out var ms) || ms <= 0)
                        throw new UsageException("Give a latency limit in ms, e.g. `dsp preset within 80`.");
                    if (!args.Json)
                        env.Err.WriteLine($"pinging {cfg.Pops.Values.Count(p => p.Blockable)} PoPs...");
                    var pings = await Latency.MeasureAsync(cfg.Pops.Values, env.Pinger, ct: ct);
                    var (desired, timedOut) = Planner.Within(cfg, pings, ms);
                    var notes = new List<string>();
                    if (timedOut.Count > 0)
                        notes.Add($"{timedOut.Count} PoP{(timedOut.Count == 1 ? "" : "s")} did not answer pings and {(timedOut.Count == 1 ? "was" : "were")} left open ({string.Join(", ", timedOut)}); some relays ignore ping. Block them with `dsp block` if you want.");
                    return new Plan(desired, notes);
                }
            case "unblock-all":
                return new Plan(DesiredState.Empty, []);
            default:
                throw new UsageException("Presets: `only-region <region>...`, `within <ms>`, `unblock-all`.");
        }
    }

    private static async Task<int> ResetAsync(CliArgs args, CliEnv env, CancellationToken ct)
    {
        var backend = await FirewallBackends.CreateAsync(env.Runner, args.Backend, ct);
        if (!args.DryRun && !env.IsElevated())
            return Fail(args, env, Elevation.Hint + " Add --dry-run to preview the commands without admin rights.", ExitCodes.NeedsElevation);

        var notes = new List<string>();
        FirewallState current;
        if (backend.Name != "windows" && !env.IsElevated())
        {
            // Can't read without root: show what a reset would run if anything were there.
            current = FirewallState.Empty with { Present = true };
            notes.Add("Not running as root, so the firewall could not be read; showing the commands a reset runs when rules exist.");
        }
        else
        {
            current = await backend.ReadAsync(ct);
        }

        var commands = backend.PlanReset(current);
        var changes = current.Blocked.Select(kv => new PopChange(kv.Key, ChangeKind.Remove, kv.Value, new SortedSet<string>())).ToList();
        if (!args.DryRun && commands.Count > 0)
            await FirewallBackends.ResetAndVerifyAsync(backend, env.Runner, current, ct);
        return Report(args, env, backend.Name, changes, commands, [], notes, [], null);
    }

    private static int Report(
        CliArgs args,
        CliEnv env,
        string backend,
        IReadOnlyList<PopChange> changes,
        IReadOnlyList<Command> commands,
        IEnumerable<string> blocked,
        IReadOnlyList<string> notes,
        IReadOnlyList<string> warnings,
        string? tip)
    {
        var applied = !args.DryRun && commands.Count > 0;
        if (args.Json)
        {
            var dto = new ChangeDto(
                backend,
                args.DryRun,
                applied,
                changes.Select(c => new PopChangeDto(c.Pop, c.Kind.ToString().ToLowerInvariant(), c.After.Except(c.Before).ToList(), c.Before.Except(c.After).ToList())).ToList(),
                commands.Select(c => c.Display()).ToList(),
                blocked.ToList(),
                notes,
                warnings,
                tip);
            env.Out.WriteLine(JsonSerializer.Serialize(dto, CliJson.Default.ChangeDto));
            return ExitCodes.Ok;
        }

        foreach (var n in notes)
            env.Out.WriteLine($"note: {n}");
        if (changes.Count == 0 && commands.Count == 0)
        {
            env.Out.WriteLine("Nothing to change: the firewall already matches.");
        }
        else
        {
            env.Out.WriteLine($"Plan ({backend}):");
            foreach (var c in changes)
                env.Out.WriteLine("  " + Describe(c));
            if (changes.Count == 0)
                env.Out.WriteLine("  repair our firewall entries");
        }
        foreach (var w in warnings)
            env.Out.WriteLine($"warning: {w}");

        if (args.DryRun)
        {
            if (commands.Count > 0)
            {
                env.Out.WriteLine();
                env.Out.WriteLine("Dry run, nothing was changed. These commands would run:");
                foreach (var c in commands)
                    env.Out.WriteLine(c.Display());
            }
        }
        else if (applied)
        {
            var list = blocked.ToList();
            env.Out.WriteLine(list.Count == 0
                ? "Done. Verified in the firewall: nothing is blocked."
                : $"Done. Verified in the firewall: {list.Count} PoP{(list.Count == 1 ? "" : "s")} blocked ({string.Join(", ", list)}).");
        }
        if (tip is not null)
            env.Out.WriteLine($"tip: {tip}");
        return ExitCodes.Ok;
    }

    private static string Describe(PopChange c) => c.Kind switch
    {
        ChangeKind.Add => $"+ block   {c.Pop,-6} ({c.After.Count} IP{(c.After.Count == 1 ? "" : "s")})",
        ChangeKind.Remove => $"- unblock {c.Pop,-6}",
        _ => $"~ update  {c.Pop,-6} (relay IPs changed: +{c.After.Except(c.Before).Count()} -{c.Before.Except(c.After).Count()})",
    };
}
