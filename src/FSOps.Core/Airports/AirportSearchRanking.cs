using FSOps.Core.Entities;

namespace FSOps.Core.Airports;

public enum AirportMatchKind
{
    ExactIcao = 0,
    IcaoPrefix = 1,
    ExactIata = 2,
    IataPrefix = 3,
    NameOrMunicipalityContains = 4,
    NoMatch = 5,
}

/// <summary>
/// Pure ordering logic for airport search. Kept separate from the endpoint/EF query so it
/// can run entirely in memory over a small candidate set and be unit tested without a
/// database: classify each candidate against the query, then sort by match quality first
/// and airport importance second.
/// </summary>
public static class AirportSearchRanking
{
    public static AirportMatchKind Classify(Airport airport, string query)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return AirportMatchKind.NoMatch;
        }

        if (string.Equals(airport.Icao, q, StringComparison.OrdinalIgnoreCase))
        {
            return AirportMatchKind.ExactIcao;
        }

        if (airport.Icao.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            return AirportMatchKind.IcaoPrefix;
        }

        if (!string.IsNullOrEmpty(airport.Iata))
        {
            if (string.Equals(airport.Iata, q, StringComparison.OrdinalIgnoreCase))
            {
                return AirportMatchKind.ExactIata;
            }

            if (airport.Iata.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            {
                return AirportMatchKind.IataPrefix;
            }
        }

        if (airport.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            airport.Municipality.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return AirportMatchKind.NameOrMunicipalityContains;
        }

        return AirportMatchKind.NoMatch;
    }

    /// <summary>Lower is more important. Large+scheduled beats large, beats medium, etc.</summary>
    public static int ImportanceWeight(Airport airport)
    {
        return airport.SizeCategory switch
        {
            AirportSizeCategory.Large => airport.HasScheduledService ? 0 : 1,
            AirportSizeCategory.Medium => airport.HasScheduledService ? 2 : 3,
            AirportSizeCategory.Small => airport.HasScheduledService ? 4 : 5,
            AirportSizeCategory.Seaplane => 6,
            AirportSizeCategory.Heliport => 7,
            AirportSizeCategory.Closed => 8,
            _ => 9,
        };
    }

    /// <summary>Filters to actual matches and orders best-first. Caller applies the limit.</summary>
    public static IEnumerable<Airport> Order(IEnumerable<Airport> candidates, string query)
    {
        return candidates
            .Select(a => (Airport: a, Kind: Classify(a, query)))
            .Where(x => x.Kind != AirportMatchKind.NoMatch)
            .OrderBy(x => x.Kind)
            .ThenBy(x => ImportanceWeight(x.Airport))
            .ThenBy(x => x.Airport.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Airport);
    }
}
