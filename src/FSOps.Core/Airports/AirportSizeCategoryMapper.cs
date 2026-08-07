using FSOps.Core.Entities;

namespace FSOps.Core.Airports;

/// <summary>Maps OurAirports' free-text "type" column onto our SizeCategory enum.</summary>
public static class AirportSizeCategoryMapper
{
    public static AirportSizeCategory Map(string? ourAirportsType)
    {
        return ourAirportsType?.Trim().ToLowerInvariant() switch
        {
            "large_airport" => AirportSizeCategory.Large,
            "medium_airport" => AirportSizeCategory.Medium,
            "small_airport" => AirportSizeCategory.Small,
            "heliport" => AirportSizeCategory.Heliport,
            "seaplane_base" => AirportSizeCategory.Seaplane,
            "closed" => AirportSizeCategory.Closed,
            // Balloonports and anything unrecognised aren't relevant to airline routing -
            // file them under Small rather than adding a category nobody uses.
            _ => AirportSizeCategory.Small,
        };
    }
}
