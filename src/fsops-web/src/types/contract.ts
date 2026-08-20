/**
 * Contract flying - jobs other operators offer, flown personally by the player in the operator's own
 * aircraft for a flat fee.
 *
 * Every shape here mirrors what `ContractEndpoints` actually returns. Two things about that are
 * deliberate and worth not undoing:
 *
 * - **The server does the ordering, the arithmetic and the refusing.** A contract carries its whole
 *   chain of stops, the per-leg fee shares, what has been earned so far, what is outstanding, and
 *   which leg is next. None of that is re-derived in the client, so the board can never disagree
 *   with the ledger about which sector comes next or what it pays.
 * - **The sentences are the API's, not the UI's.** `limitation.message`, `abandonReason`,
 *   `closedReason` and every `error` are written to be read by a player and are rendered verbatim.
 *   Rewording them here is how two screens come to say different things about one number.
 */

/** Which kind of job. Ferry is the multi-leg expedition; the other two exist so the board is not all
 *  one shape. See `ContractKind` in Enums.cs. */
export type ContractKind = 'Ferry' | 'Cargo' | 'Charter'

/**
 * Where a contract is in its life (`ContractStatus` in Enums.cs).
 *
 * `Expired` and `Abandoned` are deliberately distinct and must not be conflated in display: an offer
 * nobody took costs nothing, whereas a job walked out of half-way carries a charge.
 */
export type ContractStatus = 'Offered' | 'Accepted' | 'Completed' | 'Abandoned' | 'Expired'

/** The aeroplane the job names, from the contract aircraft catalogue. Fields are nullable because a
 *  catalogue entry can be retired without invalidating contracts already written against it - render
 *  a null as "not known", never as zero. */
export interface ContractAircraftInfo {
  typeDesignator: string
  name: string | null
  manufacturer: string | null
  /** `ContractAircraftCategory.ToString()` - display only, nothing branches on it. */
  category: string | null
  rangeNm: number | null
  cruiseTasKts: number | null
  seats: number | null
}

/** One sector of a contract. `feeShare` is stamped when the contract is generated and never
 *  recomputed, which is why the earned/outstanding split can always agree with the ledger. */
export interface ContractLeg {
  id: string
  /** 1-based position in the chain. Legs are flown in this order and the server enforces it. */
  sequence: number
  departureIcao: string
  arrivalIcao: string
  distanceNm: number
  plannedBlockMinutes: number
  /** What this leg is WORTH - stamped when the contract was generated. */
  feeShare: number
  /**
   * What this leg actually PAID, from the posted ledger rows.
   *
   * Equal to `feeShare` for a leg flown in the simulator; **zero** for one completed with estimates
   * or invalidated by slew or a position jump, both of which count as flown and pay nothing. `null`
   * when the leg has not been flown at all — a third state, not a payment of zero.
   */
  feePaid: number | null
  flown: boolean
  flightId: string | null
  flownUtc: string | null
}

/** The next sector to fly. Null when every leg has been flown. Supplied by the server precisely so a
 *  screen never has to work out the ordering rule for itself. */
export interface ContractNextLeg {
  id: string
  sequence: number
  departureIcao: string
  arrivalIcao: string
}

/** One contract, as the board and the job view render it (`ContractEndpoints.ToDto`). */
export interface Contract {
  id: string
  kind: ContractKind
  status: ContractStatus
  operatorName: string

  aircraft: ContractAircraftInfo

  /** A human sentence describing the load - "2,400 kg of machine parts", "6 passengers", or for a
   *  ferry "Positioning flight - Cessna 172, empty". Rendered as written. */
  loadDescription: string
  payloadKg: number
  paxCount: number

  /** The whole job's fee for flying every leg. What is actually banked follows the legs flown, not
   *  this figure. Does NOT include the completion bonus. */
  fee: number
  /**
   * A single lump paid **only when every leg has been flown**, on top of the per-leg shares. Zero for
   * a single-leg job, and it grows with the length of the chain.
   *
   * Shown before accepting on purpose: a bonus nobody knows about cannot influence the decision it
   * exists to influence. **Forfeited by handing the job back** — and it is never part of the abandon
   * charge, so walking away loses it rather than being billed for it.
   */
  completionBonus: number
  /** `fee + completionBonus` — what the job is worth if it is seen through, which is the figure a
   *  player actually compares between offers. */
  totalIfCompleted: number
  totalDistanceNm: number
  totalPlannedBlockMinutes: number

  legCount: number
  flownLegCount: number
  /**
   * What has actually been **banked**, summed from the posted ledger rows — not from the stamped
   * shares of legs that happen to be marked flown.
   *
   * Those are different numbers: a leg completed with estimates counts as flown and pays nothing, as
   * does one invalidated by slew or a position jump. Compare against `legs[].feeShare` to see what a
   * flown leg was worth, and `legs[].feePaid` to see what it paid.
   */
  earnedSoFar: number
  /** What the REMAINING legs are worth — a fact about the future, so still from the stamped shares. */
  outstandingFee: number

  /**
   * What handing this job back would cost right now, and the sentence explaining it - both straight
   * from `ContractPayCalculator.CalculateAbandonCharge` via the same call that posts the charge.
   *
   * This is on the DTO rather than worked out here for a specific reason: the charge is the
   * outstanding fee scaled by `ContractConfig.AbandonChargeFraction`, which is server-side economy
   * config the client cannot see. A client that multiplied `outstandingFee` itself would be right
   * only until that number was tuned, and would then quietly describe something other than what
   * happens. Show `abandonCharge`; never recompute it.
   *
   * Zero is a real answer with a real reason ("no leg was flown, so ... costs nothing"), not a
   * missing one.
   */
  abandonCharge: number
  abandonReason: string

  offeredUtc: string
  /** Visible before accepting and never recalculated afterwards, so it cannot move under the player. */
  deadlineUtc: string
  acceptedUtc: string | null
  closedUtc: string | null
  closedReason: string | null

  nextLeg: ContractNextLeg | null
  /** Present whenever the server was asked for the full chain, which the board and job view both are. */
  legs: ContractLeg[] | null
}

/**
 * Why a board is thinner than it should be, in terms the player can act on.
 *
 * `message` is null when the board came out full, and is deliberately never an empty string -
 * "nothing to say" and "something to say, and it is blank" must not look alike. When it IS set it
 * has to be shown: a thin board with no explanation is indistinguishable from a broken feature, and
 * both real causes (too few aircraft ticked, an airline that barely touches anywhere) are one click
 * from being fixed.
 */
export interface ContractBoardLimitation {
  availableAircraftCount: number
  originCount: number
  requested: number
  generated: number
  message: string | null
}

/** GET /contracts/board. */
export interface ContractBoard {
  bucket: number
  refreshesUtc: string
  offered: Contract[]
  /** Jobs the player has taken and not yet finished. These survive every board refresh. */
  accepted: Contract[]
  limitation: ContractBoardLimitation
}

/** POST /contracts/{id}/abandon - the charge actually raised, and the server's own words for it. */
export interface ContractAbandonResult {
  contract: Contract
  charge: number
  unflownLegCount: number
  unflownBlockMinutes: number
  reason: string
}

/** POST /contracts/{id}/start-leg - the leg that just became a real, tracked flight. */
export interface ContractStartLegResult {
  flightId: string
  contractId: string
  contractLegId: string
  legSequence: number
  legCount: number
  departureIcao: string
  arrivalIcao: string
  distanceNm: number
  plannedBlockMinutes: number
  feeShare: number
  aircraftTypeDesignator: string
  aircraftName: string | null
  /** Informational only and never penalised, exactly as for an airline sector. Null means the sim
   *  reported no aircraft to compare against - a third state, not a failed comparison. */
  typeMismatch: boolean | null
}

/**
 * The contract marker carried by a logbook row and a flight's report card. Null for an ordinary
 * airline sector, which is how a consumer tells the two apart without inferring it from a missing
 * `routeId`.
 */
export interface FlightContractInfo {
  contractId: string
  kind: ContractKind
  operatorName: string
  legSequence: number
  legCount: number
  feeShare: number
  /** Report card only - the logbook's row does not carry these. */
  status?: ContractStatus
  aircraftTypeDesignator?: string
  aircraftName?: string | null
  departureIcao?: string
  arrivalIcao?: string
}
