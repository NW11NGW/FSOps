namespace FSOps.Core.Entities;

/// <summary>
/// One job offered by another operator: the player flies it personally, in an aircraft the operator
/// supplies, and is paid a fee. <b>They bear no operating costs</b> - fuel, landing, handling and
/// maintenance all belong to the other business. That is the whole point of the arrangement and it
/// is enforced structurally rather than by a rule: the contract completion path never reaches the
/// code that would post those lines (see FlightLifecycleService.FinalizeFlightAsync).
///
/// <para><b>This is not a Route and must never become one.</b> A Route is the player's own airline
/// selling seats between two airports it serves, and it carries a fare, a demand model and a flight
/// number. A contract is somebody else's aeroplane going somewhere for somebody else's reasons. They
/// are stored separately for the same reason the fleet catalogue and the contract aircraft list are
/// separate: merging them would quietly feed jobs flown for other operators into the demand, seat
/// and reputation models that exist to describe the player's own airline.</para>
///
/// <para><b>The stops are named up front.</b> A ferry lists its whole chain - Bristol, Wick,
/// Reykjavik, Narsarsuaq, Goose Bay, New York - and every one of those legs was checked against this
/// aircraft's range when the contract was generated. A job on the board is always flyable by the
/// aircraft it names, and the player always knows exactly what they accepted before they accept it.
/// </para>
/// </summary>
public class Contract
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    public ContractKind Kind { get; set; }

    public ContractStatus Status { get; set; } = ContractStatus.Offered;

    /// <summary>
    /// The board this offer belongs to - see <c>ContractBoardKey</c>. Contracts are generated
    /// deterministically per (world seed, airline, time bucket), and this records which bucket
    /// produced this row so the board can be regenerated idempotently: a second read of the same
    /// bucket returns the rows already stored rather than a second set of near-identical offers.
    /// It also identifies which offers a later bucket has moved past, so they expire rather than
    /// accumulating for ever.
    /// </summary>
    public long BoardBucket { get; set; }

    /// <summary>
    /// A stable index within the board bucket, 0-based. With <see cref="BoardBucket"/> this is what
    /// makes an offer identifiable across regenerations of the same board.
    /// </summary>
    public int BoardSlot { get; set; }

    /// <summary>The other business, as a name to show. Generated, and purely flavour - nothing keys off it.</summary>
    public string OperatorName { get; set; } = string.Empty;

    /// <summary>
    /// The ICAO type designator of the aircraft the contract supplies - a key into
    /// <see cref="FSOps.Core.SimAircraft.ContractAircraftCatalogue"/>, never into the airline's own
    /// fleet or into AircraftTypes. Stored as the designator rather than as a foreign key precisely
    /// because there is nothing in the database to point at: the aeroplane belongs to somebody else.
    /// </summary>
    public string AircraftTypeDesignator { get; set; } = string.Empty;

    /// <summary>What the job is carrying, as words: "2,400 kg of machine parts", "4 passengers", or empty for a ferry.</summary>
    public string LoadDescription { get; set; } = string.Empty;

    /// <summary>Freight, kilograms. Zero for a ferry (the aeroplane is the cargo) and for a charter.</summary>
    public int PayloadKg { get; set; }

    /// <summary>Passengers. Zero for a ferry and for cargo.</summary>
    public int PaxCount { get; set; }

    /// <summary>
    /// The whole job's fee, in the stored base unit, for flying every leg. Never paid as a lump:
    /// each leg carries its own share (see <see cref="ContractLeg.FeeShare"/>), those shares sum
    /// exactly to this, and each is posted as its leg is actually flown.
    /// </summary>
    public decimal Fee { get; set; }

    public double TotalDistanceNm { get; set; }

    public int TotalPlannedBlockMinutes { get; set; }

    /// <summary>When this offer appeared on the board.</summary>
    public DateTimeOffset OfferedUtc { get; set; }

    /// <summary>
    /// When the job must be finished by. <b>Fixed when the contract is generated and visible before
    /// the player accepts</b> - it never ambushes them, and the world stays predictable. Generous by
    /// design: weeks of real time, so a half-finished ocean crossing can be left and picked up.
    /// </summary>
    public DateTimeOffset DeadlineUtc { get; set; }

    public DateTimeOffset? AcceptedUtc { get; set; }

    /// <summary>When this contract reached a terminal status - completed, abandoned or expired.</summary>
    public DateTimeOffset? ClosedUtc { get; set; }

    /// <summary>
    /// Why an abandoned contract ended, in words the player can read: they gave up, or the deadline
    /// passed. Null for every other status. A contract that silently stopped being available is the
    /// worst possible outcome, so the reason is always recorded rather than inferred from dates.
    /// </summary>
    public string? ClosedReason { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }

    public List<ContractLeg> Legs { get; set; } = new();
}

/// <summary>
/// One sector of a contract, flown in order. A ferry has several; cargo and charter normally have
/// one, but nothing forbids more - leg count falls out of distance against the named aircraft's
/// range rather than being decided by the kind.
/// </summary>
public class ContractLeg
{
    public Guid Id { get; set; }

    public Guid ContractId { get; set; }

    /// <summary>1-based position in the chain. Legs are flown in this order and no other.</summary>
    public int Sequence { get; set; }

    public string DepartureIcao { get; set; } = string.Empty;

    public string ArrivalIcao { get; set; } = string.Empty;

    public double DistanceNm { get; set; }

    /// <summary>
    /// What this leg was expected to take, from the contract aircraft's cruise speed. This is the
    /// weight in <see cref="FeeShare"/> and in the abandon charge, and it is stamped at generation
    /// so neither can move under the player after they accepted.
    /// </summary>
    public int PlannedBlockMinutes { get; set; }

    /// <summary>
    /// This leg's slice of <see cref="Contract.Fee"/>, weighted by
    /// <see cref="PlannedBlockMinutes"/>. Stamped at generation, so what a leg is worth is a fact
    /// about the contract rather than something recomputed (and therefore able to differ) at the
    /// moment it is paid. Every leg's share sums exactly to the fee - see
    /// <see cref="FSOps.Core.Contracts.ContractPayCalculator.AllocateFeeShares"/>.
    /// </summary>
    public decimal FeeShare { get; set; }

    /// <summary>
    /// The flight that flew this leg, once one has. Null while the leg is outstanding. This is the
    /// only link between a contract and a Flight row, and it is the thing that makes "pay per leg
    /// actually flown" answerable from stored fact rather than from a running tally.
    /// </summary>
    public Guid? FlightId { get; set; }

    public DateTimeOffset? FlownUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
