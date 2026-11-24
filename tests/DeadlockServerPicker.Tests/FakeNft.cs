using DeadlockServerPicker.Core.Firewall;

namespace DeadlockServerPicker.Tests;

/// <summary>
/// Minimal stateful stand-in for the nft binary: remembers the last table applied with `nft -f -`
/// and lists it back, so CLI tests exercise apply + read-back verification end to end.
/// </summary>
internal sealed class FakeNft : ICommandRunner
{
    private const string TableHeader = "table inet " + NftablesBackend.Table + " {\n";

    public string? Table { get; set; }

    public List<Command> Ran { get; } = [];

    public IEnumerable<Command> Writes => Ran.Where(c => c.Args is ["-f", "-"]);

    public Task<CommandResult> RunAsync(Command command, CancellationToken ct = default)
    {
        Ran.Add(command);
        var result = string.Join(' ', command.Args) switch
        {
            "--version" => Ok("nftables v1.1.6"),
            "list tables" => Ok(Table is null ? "" : $"table inet {NftablesBackend.Table}\n"),
            "list ruleset" => Ok(Table ?? ""),
            var a when a == $"list table inet {NftablesBackend.Table}" => Table is null ? new CommandResult(1, "", "Error: No such file or directory") : Ok(Table),
            "-f -" => Apply(command.Stdin!),
            var a => new CommandResult(1, "", $"FakeNft: unexpected `nft {a}`"),
        };
        return Task.FromResult(result);
    }

    private CommandResult Apply(string script)
    {
        var i = script.IndexOf(TableHeader, StringComparison.Ordinal);
        Table = i >= 0 ? script[i..] : null;
        return Ok("");
    }

    private static CommandResult Ok(string stdout) => new(0, stdout, "");
}
