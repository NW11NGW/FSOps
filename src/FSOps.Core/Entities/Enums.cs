namespace FSOps.Core.Entities;

public enum AirportSizeCategory
{
    Small,
    Medium,
    Large,
    Heliport,
    Seaplane,
    Closed,
}

public enum AirlineStrategyProfile
{
    International,
    Domestic,
    LowCost,
    Premium,
    Balanced,
}

/// <summary>
/// Chosen once, at airline creation, and permanent for the airline's life: switching mid-game
/// would either bankrupt a healthy airline overnight or trivialise everything already earned, and
/// either way its history would have been earned under rules that no longer apply. A playstyle is a named set of overrides in
/// economy-config.json (starter lease, insurance, lease deposit, starting capital) - never a code
/// path, so nothing in the economy engine itself branches on this value. It is resolved once, at
/// the point a config is needed for a specific airline, via EconomyConfigCatalog.
/// </summary>
public enum AirlinePlaystyle
{
    Casual,
    TrueLife,
}

public enum AircraftOwnership
{
    Owned,
    Leased,
}

public enum FleetAircraftStatus
{
    Active,
    InMaintenance,
    InFlight,
}

public enum PilotStatus
{
    Available,
    Flying,
    Inactive,
}

/// <summary>
/// Stored as text (see FlightConfiguration), so member ORDER carries no meaning and adding one
/// needs no migration.
/// <para>
/// There is deliberately no <c>Planned</c> member. One was declared until 2026-08-14 and never
/// written by anything: a player's flight is created the moment they start it
/// (<c>FlightEndpoints</c>, straight to <see cref="InProgress"/>), and a virtual pilot's occurrence
/// is resolved in a single pass that writes its final status immediately. A flight that exists but
/// has not begun is not a state this app has - the schedule template IS the plan, and it lives in
/// PilotScheduleEntry, not in a Flight row. Re-adding it would mean a real flight lifecycle change,
/// not a one-line enum edit.
/// </para>
/// </summary>
public enum FlightStatus
{
    InProgress,
    Completed,
    Interrupted,
    Abandoned,

    /// <summary>
    /// A virtual pilot's scheduled flight that could not fly (aircraft in maintenance, still away,
    /// or otherwise unavailable) and the airline's Playstyle is Casual: the airline forgives a
    /// schedule the player has not perfected yet. Recorded and visible in history so a
    /// silent skip can never hide a scheduling bug, but no ledger lines are posted at all: no lost
    /// revenue (nothing was ever booked) and no penalty. See VirtualFlightResolverService.
    /// </summary>
    Skipped,

    /// <summary>
    /// A virtual pilot's scheduled flight that could not fly and the airline's Playstyle is
    /// True-life, where a schedule that cannot fly costs real money and is clearly reported. Unlike
    /// <see cref="Skipped"/>, this posts a single <see cref="LedgerCategory.CancellationFee"/>
    /// ledger line (see EconomyConfig.UnflyableSchedule) so a badly-planned schedule genuinely
    /// bites. Whether an unflyable occurrence resolves to Skipped or Cancelled is entirely
    /// config-driven (EconomyConfig.UnflyableSchedule.CancellationFee is zero for Casual, positive
    /// for True-life) - there is no `if (playstyle)` branch anywhere in the resolution logic.
    /// </summary>
    Cancelled,

    /// <summary>
    /// A virtual pilot's scheduled occurrence that could not fly because its aircraft is grounded
    /// for a maintenance check, on a schedule with <see cref="PilotSchedule.AutoSuspendOnMaintenance"/>
    /// set, so the schedule suspends while the aircraft is unavailable and resumes automatically
    /// when it returns. Recorded and visible in history exactly like <see cref="Skipped"/> (no
    /// ledger lines at all - never a <see cref="LedgerCategory.CancellationFee"/>, because the
    /// schedule isn't at fault), but distinct from Skipped so the reason ("suspended for
    /// maintenance" vs. "the schedule genuinely can't be flown as built") is never conflated in the
    /// UI or history. Resumption is automatic: the very next occurrence after the aircraft comes
    /// out of maintenance resolves normally, with no separate "un-suspend" step anywhere.
    /// </summary>
    Suspended,
}

public enum FlightEventType
{
    PhaseChange,
    Touchdown,
    PositionSnapshot,
    Mismatch,
    Note,
}

/// <summary>
/// What kind of job another operator is offering. Stored as text (see ContractConfiguration), so
/// member order carries no meaning and adding one needs no migration.
/// <para>
/// The three exist because <b>variety of flying is the whole point of contracts</b>, not the money -
/// so they have to feel genuinely different rather than being one job type wearing three labels.
/// Nothing in the generator branches on this value to decide whether a job MAY be multi-leg; leg
/// count falls out of distance against the named aircraft's range (see
/// <see cref="FSOps.Core.Contracts.ContractLegChainBuilder"/>). What the kind actually changes is
/// which scale bands are plausible and what the load is - which is why a ferry usually ends up long
/// and a charter usually does not, without either being forced.
/// </para>
/// </summary>
public enum ContractKind
{
    /// <summary>
    /// Move somebody else's aircraft from A to a distant B. The most distinctive thing in the app:
    /// several sectors over several sessions, the aircraft sitting where it was left in between, and
    /// the whole chain of stops named up front. There is no payload - the aeroplane IS the cargo.
    /// </summary>
    Ferry,

    /// <summary>Freight between two points, where the load is what decides which airframe can take it.</summary>
    Cargo,

    /// <summary>A one-off passenger job, often somewhere the player's own network does not reach.</summary>
    Charter,
}

/// <summary>
/// Where a contract is in its life. Stored as text (see ContractConfiguration), so member order
/// carries no meaning and adding one needs no migration.
/// <para>
/// <see cref="Offered"/> is first deliberately, so <c>default(ContractStatus)</c> is the harmless
/// answer - a row that somehow lost its status reads as "on the board", never as one the player is
/// on the hook for.
/// </para>
/// </summary>
public enum ContractStatus
{
    /// <summary>On the board, not yet accepted. Disappears when the board refreshes past it.</summary>
    Offered,

    /// <summary>The player took it. Legs are flown in order; the deadline is now running.</summary>
    Accepted,

    /// <summary>Every leg flown. Terminal.</summary>
    Completed,

    /// <summary>
    /// The player gave up part-way, or the deadline passed with legs outstanding. Terminal. They
    /// keep what the flown legs earned and are charged for the rest - see
    /// <see cref="FSOps.Core.Contracts.ContractPayCalculator"/>.
    /// </summary>
    Abandoned,

    /// <summary>
    /// Offered, never accepted, and the board has moved on. Terminal, and deliberately distinct from
    /// <see cref="Abandoned"/>: an offer nobody took is not a job somebody walked out of, and it
    /// costs nothing. Conflating the two would put a charge on ignoring the board.
    /// </summary>
    Expired,
}

/// <summary>
/// Stored as text (see LedgerTransactionConfiguration), so member ORDER carries no meaning and
/// adding one needs no migration.
/// <para>
/// There is deliberately no <c>Other</c> member. One was declared until 2026-08-14 and never written
/// by anything: across every commit ever made it appeared only in this declaration, the TypeScript
/// union that mirrors it, and two display-label maps - no code path ever assigned it, so no database
/// can be holding one (the same evidence that settled removing <c>GsxServices</c>, and the same
/// reason the check matters: a value left behind in a real database would fail to parse on read).
/// It was worse than merely unused. <c>FinanceEndpoints.CostsAsync</c> summed it into neither the
/// fixed nor the variable total, so a single row posted under it would have made the Finances page
/// disagree with the ledger - on the one screen whose whole job is being accurate. It could not
/// honestly be fixed by adding it to a total either: a catch-all has no answer to "fixed or
/// variable", which is the only question that page asks. A category that cannot be classified does
/// not belong on a page that classifies, so it is gone rather than papered over. Anything genuinely
/// new gets its own member and its own decision, exactly as
/// <see cref="AircraftRepositioning"/> did - and as <see cref="ContractFee"/> has now.
/// </para>
/// </summary>
public enum LedgerCategory
{
    TicketRevenue,
    Fuel,
    LandingFees,
    Handling,
    Maintenance,
    Salary,

    /// <summary>
    /// Per-sector crew cost, distinct from <see cref="Salary"/>, which is the monthly pilot wage.
    /// These were both posted as Salary until the Finances page needed to separate variable cost
    /// from fixed - a sector's crew cost varies with flying, a monthly wage does not, and telling
    /// them apart from the ledger category alone was impossible. Stored as text (see
    /// LedgerTransactionConfiguration), so adding this value needs no migration.
    /// </summary>
    CrewCost,

    /// <summary>
    /// Parking charges at the arrival airport. Split out of <see cref="Handling"/> for the same
    /// reason as <see cref="CrewCost"/> - see that member.
    /// </summary>
    ParkingFees,

    /// <summary>
    /// Per-passenger charges levied by the arrival airport. Split out of <see cref="Handling"/>.
    /// </summary>
    PassengerCharges,

    /// <summary>
    /// Turnaround and gate fees at the arrival airport. Split out of <see cref="Handling"/>.
    /// </summary>
    TurnaroundFees,
    LeasePayment,
    LoanPayment,
    AircraftPurchase,
    Insurance,
    StartingCapital,
    LoanProceeds,

    /// <summary>
    /// Posted for a virtual pilot's flight that couldn't fly under a True-life airline - see
    /// <see cref="FlightStatus.Cancelled"/>. Never
    /// posted for a Casual airline (see <see cref="FlightStatus.Skipped"/>) and never posted
    /// alongside a <see cref="TicketRevenue"/> line for the same flight - a cancelled sector never
    /// flew, so it never earns revenue.
    /// </summary>
    CancellationFee,

    /// <summary>
    /// The modest revenue uplift for a sector FSOps corroborated as genuinely flown online on
    /// VATSIM - see <see cref="FSOps.Server.Services.FlightEconomicsPoster.PostVatsimOnlineBonus"/>
    /// and <see cref="EconomyConfig.VatsimOnlineBonus"/>. Posted as its own line (rather than folded
    /// into <see cref="TicketRevenue"/>) so the report card and ledger can show plainly that it came
    /// from flying online, not from the fare itself. Stored as text, so adding this value needs no
    /// migration.
    /// </summary>
    VatsimOnlineBonus,

    /// <summary>
    /// The flat charge for moving an idle aircraft to another airport the airline serves without
    /// flying it there - see <see cref="FSOps.Core.Economy.AircraftRepositioningConfig"/> and
    /// FleetRepositionEndpoints. Its own category rather than
    /// <see cref="Handling"/>: the Finances page groups by category, and
    /// filing a positioning fee under handling would misattribute it to a flight that never
    /// happened - "what did I spend moving aircraft around" is a question the ledger should be able
    /// to answer on its own. Stored as text (see LedgerTransactionConfiguration), so adding this
    /// value needs no migration.
    /// </summary>
    AircraftRepositioning,

    /// <summary>
    /// Money moving because of contract flying - a job flown personally for another operator, in an
    /// aircraft that operator supplies. Positive for a leg actually flown; negative for the single
    /// charge raised when an accepted contract is abandoned with legs outstanding. See
    /// <see cref="FSOps.Core.Contracts.ContractPayCalculator"/> and ContractEconomicsPoster.
    /// <para>
    /// <b>Its own category, and the ONLY category a contract sector ever posts.</b> The whole
    /// arrangement is that the player bears no operating costs - fuel, landing, handling,
    /// maintenance and crew all belong to the other business - so a contract flight writes exactly
    /// one line and nothing else. That is not a rule the poster remembers to follow: the contract
    /// completion path never reaches the code that would post the others (see
    /// FlightLifecycleService.FinalizeFlightAsync). Filing the fee under
    /// <see cref="TicketRevenue"/> instead would have been wrong twice over - it would claim the
    /// player's own airline sold those seats, and it would make "what has contract flying earned me"
    /// unanswerable from the ledger, which is the one question this feature most obviously invites.
    /// </para>
    /// <para>
    /// Both signs live in one category on purpose. An abandon charge is not a different kind of money
    /// from the fee it is deducted from - it is the same job going the other way - and splitting them
    /// would make the honest total ("what did contracts do to my balance") require knowing to add two
    /// categories together. Stored as text (see LedgerTransactionConfiguration), so adding this value
    /// needs no migration.
    /// </para>
    /// </summary>
    ContractFee,
}

public enum MaintenanceEventType
{
    ACheck,
    CCheck,
    Unscheduled,
}

public enum DistanceUnit
{
    Nm,
    Km,
}

public enum AltitudeUnit
{
    Feet,
    Metres,
}

public enum WeightUnit
{
    Kg,
    Lb,
}

public enum TimeDisplay
{
    Utc,
    Local,
}

/// <summary>
/// Which releases the in-app updater is allowed to offer. Stored as text on UserSettings (see
/// UserSettingsConfiguration), so member order carries no meaning for the database - but
/// <see cref="Stable"/> is first deliberately, so <c>default(UpdateChannel)</c> is the safe answer
/// rather than the adventurous one. Anything that fails to resolve a channel - no settings row, an
/// unreadable database, a value the app does not recognise - lands on Stable for that same reason.
/// <para>
/// This decides <b>which</b> build is offered and nothing else. It has no bearing on whether a
/// download is verified: a pre-release's installer is checked against its published SHA-256 exactly
/// as strictly as a stable one, by the same code, with no branch on this value anywhere near it.
/// </para>
/// </summary>
public enum UpdateChannel
{
    /// <summary>
    /// Released builds only. The updater reads GitHub's <c>/releases/latest</c>, which excludes
    /// drafts and pre-releases, so nothing that has not been published as a full release can reach
    /// someone on this channel.
    /// </summary>
    Stable,

    /// <summary>
    /// Released builds <i>and</i> pre-releases, whichever is newest. This is how a build from the
    /// development branch reaches somebody who asked for it, and the pre-release flag is what stops
    /// it reaching anybody who did not.
    /// </summary>
    Development,
}

/// <summary>Starter aircraft choice offered during airline creation - not persisted directly,
/// each family maps to one specific AircraftType variant (see AirlineEndpoints).</summary>
public enum StarterAircraftFamily
{
    A320,
    B737,
}
