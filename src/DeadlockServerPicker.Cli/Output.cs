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

public sealed record BlockedPopDto(string Code, IReadOnlyList<string> Ips, bool? MatchesConfig);

public sealed record StatusDto(string Backend, IReadOnlyList<BlockedPopDto> Blocked, IReadOnlyList<string> Warnings, ConfigDto? Config);

public sealed record VersionDto(string Version, string ConfigDir, string? LogFile);

public sealed record ErrorDto(string Error, int ExitCode);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ListDto))]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(VersionDto))]
[JsonSerializable(typeof(ErrorDto))]
internal sealed partial class CliJson : JsonSerializerContext;
