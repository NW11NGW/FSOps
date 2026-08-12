namespace FSOps.Core.Entities;

/// <summary>Singleton row holding world-simulation state shared across the whole app.</summary>
public class EconomyState
{
    public Guid Id { get; set; }

    public DateTimeOffset LastProcessedUtc { get; set; }

    /// <summary>
    /// Watermark for <see cref="FSOps.Server.Services.VirtualFlightResolverService"/> - "every
    /// scheduled virtual-pilot occurrence whose arrival time is at or before this has been
    /// resolved (flown, skipped, or cancelled)". Separate from <see cref="LastProcessedUtc"/>
    /// (the monthly billing watermark) because the two cycles advance independently and at
    /// different granularities - see that service's class doc for why they share this one
    /// EconomyState row rather than each inventing their own.
    /// <para>
    /// Nullable rather than defaulted to a scaffolded value: a brand-new/pre-E2 database has no
    /// opinion on where virtual-flight resolution should start, and a wrong non-null default here
    /// is exactly the class of bug an EF-scaffolded default has produced here twice before - the
    /// sim-rate column defaulted to 0.0 would have made every historical flight read as a frozen
    /// clock. Left null until
    /// VirtualFlightResolverService's first pass explicitly bootstraps it to "now" - the same
    /// never-back-charge principle EconomyClockService already uses when this whole row is first
    /// created.
    /// </para>
    /// </summary>
    public DateTimeOffset? LastScheduleResolvedUtc { get; set; }

    public decimal FuelPricePerKg { get; set; }

    public int WorldSeed { get; set; }

    /// <summary>
    /// Cursor for the "while you were away" catch-up summary. A returning player can otherwise open
    /// the app to a materially different balance with no idea why: months of lease payments,
    /// salaries and insurance arriving at once looks exactly like a bug unless it is explained.
    /// Independent of <see cref="LastProcessedUtc"/> and
    /// <see cref="LastScheduleResolvedUtc"/>, which are billing/scheduling watermarks the resolver
    /// services own and advance continuously. This one instead marks "the player has been shown
    /// everything up to here" and only ever moves when the summary is explicitly acknowledged (see
    /// MaintenanceEndpoints' away-summary endpoints) - so polling the summary mid-session is always
    /// safe and never consumes it. Nullable and left null until the first acknowledgement, same
    /// "no opinion until something explicitly sets it" convention as
    /// <see cref="LastScheduleResolvedUtc"/> - a brand-new airline has nothing to catch up on.
    /// </summary>
    public DateTimeOffset? AwaySummaryLastViewedUtc { get; set; }
}
