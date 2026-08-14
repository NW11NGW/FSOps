namespace FSOps.Core.Entities;

/// <summary>
/// One day's recorded <see cref="Airline.ReputationScore"/>. Insert-only, exactly like
/// <see cref="FlightEvent"/> and <see cref="LedgerTransaction"/> - a row is written once for a given
/// airline/day and never updated or deleted, so the series can never be rewritten after the fact.
/// <para>
/// <b>Why this table exists at all.</b> <see cref="Airline.ReputationScore"/> is a single mutable
/// number with no history behind it. It is a pure function of the flight sequence
/// (<see cref="Economy.ReputationCalculator"/>), so replaying it looked possible - but a replay
/// cannot be trusted for two reasons: a manually-completed sector takes a different, harsher advance
/// (<see cref="Economy.ReputationCalculator.AdvanceForUnverifiedManualCompletion"/>) than a
/// telemetry-completed one, and nothing on the <see cref="Flight"/> row distinguishes the two; and
/// every advance depends on the airline's current <see cref="AirlinePlaystyle"/>, so history flown
/// under a different playstyle would be replayed with today's constants. Rather than draw a
/// plausible-looking invented line, FSOps records the real number from the day this shipped.
/// </para>
/// <para>
/// <b>Missing days are expected and honest.</b> A snapshot is taken by
/// <c>EconomyClockService</c> on whatever days the app is actually opened; days the app never ran
/// have no row and must render as a gap, never as an interpolated or carried-forward value. Callers
/// should never fabricate a point for a date that has no row.
/// </para>
/// </summary>
public class ReputationSnapshot
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    /// <summary>
    /// The UTC calendar day this score was observed on, as <c>yyyy-MM-dd</c>. Deliberately a string
    /// rather than a date type: SQLite has no native date, every date in this schema is stored as
    /// text anyway, and this exact form sorts lexicographically in the same order it sorts
    /// chronologically - so a range filter or an ORDER BY behaves identically in SQL and in memory,
    /// with none of the provider-translation traps this project has already been bitten by around
    /// <see cref="DateTimeOffset"/>. It is also the exact shape the stats API already returns for a
    /// day bucket, so nothing has to reformat it on the way out.
    /// </summary>
    public string DateUtc { get; set; } = string.Empty;

    /// <summary>The airline's <see cref="Airline.ReputationScore"/> at the moment this row was written.</summary>
    public double Score { get; set; }

    /// <summary>The precise instant the observation was taken - <see cref="DateUtc"/> is the bucket,
    /// this is the timestamp. Kept so a row can always be traced back to when it was actually made.</summary>
    public DateTimeOffset RecordedUtc { get; set; }
}
