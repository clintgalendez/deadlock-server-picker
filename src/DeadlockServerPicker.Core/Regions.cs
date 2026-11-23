using System.Text.Json;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Core;

public enum Region { NA, EU, Asia, SA, Oceania, MiddleEast, Africa }

/// <summary>Region of a PoP. Guessed = not in regions.json, inferred from typical_pings or geo.</summary>
public sealed record RegionInfo(Region Region, bool Guessed);

public static class Regions
{
    private static readonly IReadOnlyDictionary<string, Region> Map = Load();

    public static IReadOnlyList<Region> All { get; } = Enum.GetValues<Region>();

    public static bool IsMapped(string popCode) => Map.ContainsKey(popCode);

    public static RegionInfo Of(Pop pop, SdrConfig config)
    {
        if (Map.TryGetValue(pop.Code, out var r))
            return new RegionInfo(r, false);

        // Geo is unreliable in the live data (swapped lat/lon, wrong signs), so prefer the
        // nearest explicitly mapped PoP by Valve's own typical_pings.
        var nearest = config.TypicalPings
            .Select(t => t.A == pop.Code ? (Other: t.B, t.Ms) : t.B == pop.Code ? (Other: t.A, t.Ms) : (Other: "", Ms: int.MaxValue))
            .Where(x => x.Ms <= 40 && Map.ContainsKey(x.Other))
            .OrderBy(x => x.Ms).ThenBy(x => x.Other, StringComparer.Ordinal)
            .Select(x => x.Other)
            .FirstOrDefault();
        if (nearest is not null)
            return new RegionInfo(Map[nearest], true);

        return new RegionInfo(pop.Geo is [var lon, var lat] ? FromGeo(lon, lat) : Region.EU, true);
    }

    /// <summary>Coarse bounding boxes on [lon, lat]. Last resort only.</summary>
    public static Region FromGeo(double lon, double lat)
    {
        if (Math.Abs(lat) > 90) (lon, lat) = (lat, lon);
        return (lon, lat) switch
        {
            ( >= -170 and <= -30, < 13) => Region.SA,
            ( >= -170 and <= -50, _) => Region.NA,
            ( >= 34 and <= 63, >= 12 and <= 42) => Region.MiddleEast,
            ( >= -25 and <= 45, >= 35) => Region.EU,
            ( >= -20 and <= 55, _) => Region.Africa,
            ( >= 110, <= -10) => Region.Oceania,
            _ => Region.Asia,
        };
    }

    /// <summary>Accepts na, eu, asia, sa, oce/oceania, me/middleeast, af/africa (any case).</summary>
    public static bool TryParse(string s, out Region region)
    {
        Region? r = s.ToLowerInvariant() switch
        {
            "na" or "northamerica" => Region.NA,
            "eu" or "europe" => Region.EU,
            "asia" => Region.Asia,
            "sa" or "southamerica" => Region.SA,
            "oce" or "oceania" => Region.Oceania,
            "me" or "middleeast" => Region.MiddleEast,
            "af" or "africa" => Region.Africa,
            _ => null,
        };
        region = r ?? default;
        return r is not null;
    }

    public static string DisplayName(Region r) => r switch
    {
        Region.NA => "North America",
        Region.EU => "Europe",
        Region.Asia => "Asia",
        Region.SA => "South America",
        Region.Oceania => "Oceania",
        Region.MiddleEast => "Middle East",
        Region.Africa => "Africa",
        _ => r.ToString(),
    };

    /// <summary>Deadlock's in-game <c>citadel_region_override</c> value for a region, if it has one.</summary>
    public static int? CitadelRegionOverride(Region r) => r switch
    {
        Region.NA => 0,
        Region.EU => 1,
        Region.Asia => 2,
        Region.SA => 3,
        Region.Oceania => 5,
        _ => null,
    };

    private static Dictionary<string, Region> Load()
    {
        using var stream = typeof(Regions).Assembly.GetManifestResourceStream("regions.json")
            ?? throw new InvalidOperationException("regions.json resource missing");
        using var doc = JsonDocument.Parse(stream);
        var map = new Dictionary<string, Region>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.GetProperty("pops").EnumerateObject())
            map[p.Name] = Enum.Parse<Region>(p.Value.GetString()!);
        return map;
    }
}
