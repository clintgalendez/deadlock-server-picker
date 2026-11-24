using System.Text.Json.Serialization;

namespace DeadlockServerPicker.Cli;

public sealed record NearbyDto(string Code, int Ms);

public sealed record PopDto(
    string Code,
    string Description,
    string Region,
    bool RegionGuessed,
    bool Blockable,
    int Relays,
    IReadOnlyList<string> Ips,
    IReadOnlyList<NearbyDto>? ReachedVia,
    long? PingMs = null,
    bool? Blocked = null);

public sealed record ConfigDto(int AppId, long Revision, DateTimeOffset FetchedAt, bool Stale, string? StaleReason);

public sealed record ListDto(ConfigDto Config, IReadOnlyList<PopDto> Pops);

public sealed record BlockedPopDto(string Code, IReadOnlyList<string> Ips, bool? MatchesConfig, IReadOnlyList<string>? MissingIps = null, IReadOnlyList<string>? ExtraIps = null);

public sealed record StatusDto(string Backend, IReadOnlyList<BlockedPopDto> Blocked, IReadOnlyList<string> Warnings, ConfigDto? Config);

public sealed record VersionDto(string Version, string ConfigDir, string? LogFile);

public sealed record ErrorDto(string Error, int ExitCode);

public sealed record PopChangeDto(string Pop, string Kind, IReadOnlyList<string> AddIps, IReadOnlyList<string> RemoveIps);

public sealed record ChangeDto(
    string Backend,
    bool DryRun,
    bool Applied,
    IReadOnlyList<PopChangeDto> Changes,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Blocked,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings,
    string? Tip);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ListDto))]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(VersionDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(ChangeDto))]
internal sealed partial class CliJson : JsonSerializerContext;
