using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;

namespace FSOps.Core.Economy;

/// <summary>
/// Everything tunable about contract flying: how big the board is, how far jobs reach, what they
/// pay, and what walking away costs. Lives in economy-config.json like every other balance figure -
/// contract flying is retuned by editing that file, never by changing code.
///
/// <para>Shared across playstyles. A contract is somebody else's aeroplane and somebody else's
/// costs; how realistically the player chose to have their OWN airline billed says nothing about
/// what a third party would pay them to fly a job.</para>
/// </summary>
public sealed class ContractConfig
{
    /// <summary>
    /// How many jobs a full board tries to carry. A target rather than a guarantee: generation
    /// rejects anything it cannot make genuinely flyable, so a player with two aircraft available
    /// gets a shorter board - and is told that is why, rather than being left to wonder.
    /// </summary>
    public int BoardSize { get; init; } = 8;

    /// <summary>
    /// How long a board lasts before it refreshes, in hours. The bucket this defines is also what
    /// generation is seeded from, so "deterministic" and "refreshes on a schedule" are the same
    /// mechanism rather than two that have to be kept in step.
    /// </summary>
    public int BoardRefreshHours { get; init; } = 24;

    /// <summary>
    /// How long the player has to finish an accepted contract, in days. <b>Weeks, not days</b>, and
    /// deliberately so: a multi-leg ocean crossing is meant to be flyable across several sessions,
    /// left half-finished and picked up again. Short enough that abandoned jobs do not sit for ever;
    /// long enough that the deadline is never the reason a crossing failed.
    /// </summary>
    public int DeadlineDays { get; init; } = 28;

    /// <summary>
    /// The fraction of the outstanding legs' value charged when an accepted contract is abandoned
    /// part-way.
    ///
    /// <para><b>1.0 is the user's own stated figure, and it is theirs rather than a tuning choice.</b>
    /// Their words: <i>"if someone does 3 legs when there are 2 legs remaining they would get charged
    /// for the remaining 2 legs."</i> The fraction they were describing IS the unflown share - two
    /// fifths of the fee - so scaling it down again would be a second fraction nobody asked for.
    /// This was briefly built at 0.5 and corrected.</para>
    ///
    /// <para><b>Check the outcome before assuming it is punitive.</b> Earn three legs, pay for two,
    /// and abandoning most of the way through lands near break-even: not a trap, just "you gained
    /// nothing for the evening", which is a fair consequence for leaving somebody's aeroplane
    /// stranded. Stopping at leg one costs real money, and it should - that is where the aircraft
    /// ends up in the worst possible place.</para>
    ///
    /// <para><b>The safety valve is elsewhere and is not this number:</b> nothing at all is charged
    /// if no leg was ever flown (see
    /// <see cref="FSOps.Core.Contracts.ContractPayCalculator.CalculateAbandonCharge"/>), because the
    /// justification the charge rests on - somebody has to recover the aircraft - is simply false
    /// when the aircraft never left. So handing an untouched job back is free at any fraction, and
    /// accepting a contract is never a decision the player cannot undo.</para>
    /// </summary>
    public decimal AbandonChargeFraction { get; init; } = 1.0m;

    // ---- Fee shape - see ContractPayCalculator.CalculateFee ----

    /// <summary>What any job is worth before distance, legs or load are counted. Keeps a short hop from paying nothing.</summary>
    public decimal BaseFee { get; init; } = 900m;

    public decimal FeePerNm { get; init; } = 3.10m;

    /// <summary>
    /// Paid per sector, on top of distance. Five 300 nm legs really are more work than one 1,500 nm
    /// leg - five departures, five approaches, five sessions - and this is what says so. It is also
    /// what stops a light single's ocean crossing being the worst-paid job on the board.
    /// </summary>
    public decimal FeePerLeg { get; init; } = 850m;

    public decimal FeePerPayloadKg { get; init; } = 0.55m;

    public decimal FeePerPassenger { get; init; } = 90m;

    /// <summary>No job pays less than this, whatever the arithmetic says.</summary>
    public decimal MinimumFee { get; init; } = 750m;

    // ---- Multipliers ----

    /// <summary>
    /// How the fee scales with the size of aircraft the operator is handing over. A widebody ferry is
    /// a serious undertaking with a serious asset attached; a Cessna 152 job is not.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> CategoryMultipliers { get; init; } =
        new Dictionary<string, decimal>
        {
            [nameof(ContractAircraftCategory.LightSingle)] = 0.55m,
            [nameof(ContractAircraftCategory.LightTwin)] = 0.75m,
            [nameof(ContractAircraftCategory.UtilityTurboprop)] = 1.00m,
            [nameof(ContractAircraftCategory.BusinessJet)] = 1.35m,
            [nameof(ContractAircraftCategory.RegionalAirliner)] = 1.55m,
            [nameof(ContractAircraftCategory.Narrowbody)] = 2.10m,
            [nameof(ContractAircraftCategory.Widebody)] = 3.20m,
        };

    /// <summary>
    /// A modest nudge per kind, not a hierarchy. Ferry pays a little over the odds because the player
    /// is trusted with the whole aeroplane and gets nothing back at the far end; charter pays a
    /// little over cargo because people are less forgiving than boxes.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> KindMultipliers { get; init; } =
        new Dictionary<string, decimal>
        {
            [nameof(ContractKind.Ferry)] = 1.10m,
            [nameof(ContractKind.Cargo)] = 1.00m,
            [nameof(ContractKind.Charter)] = 1.05m,
        };

    // ---- Scale ----

    /// <summary>
    /// The spread of job sizes a board aims for. <b>This is the setting that decides whether the board
    /// is worth browsing</b>: the user asked for jobs that "could be massive or just small domestic
    /// flights", so a board that is all one size is the same failure as a generator that always offers
    /// four legs. Weights are relative; a band whose distance nothing available can reach simply
    /// produces nothing and the board fills from the others.
    /// </summary>
    public IReadOnlyList<ContractScaleBand> ScaleBands { get; init; } = new[]
    {
        new ContractScaleBand("Local", 60, 260, 3.0),
        new ContractScaleBand("Regional", 260, 850, 3.0),
        new ContractScaleBand("Long", 850, 2400, 2.0),
        new ContractScaleBand("Epic", 2400, 6500, 1.0),
    };

    /// <summary>
    /// How close to the band's chosen target distance a destination has to be to count, as a fraction
    /// of the target. Loose enough that real geography can satisfy it, tight enough that asking for a
    /// 300 nm job does not return a 900 nm one.
    /// </summary>
    public double DestinationDistanceTolerance { get; init; } = 0.35;

    public decimal CategoryMultiplier(ContractAircraftCategory category) =>
        CategoryMultipliers.TryGetValue(category.ToString(), out var value) ? value : 1.0m;

    public decimal KindMultiplier(ContractKind kind) =>
        KindMultipliers.TryGetValue(kind.ToString(), out var value) ? value : 1.0m;
}

/// <summary>
/// One size of job the board may offer. <paramref name="Weight"/> is relative to the other bands -
/// larger means more common.
/// </summary>
public sealed record ContractScaleBand(string Name, double MinDistanceNm, double MaxDistanceNm, double Weight);
