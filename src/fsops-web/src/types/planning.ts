/**
 * The three decision surfaces served by `/api/v1/planning/*` and `/api/v1/routes/{id}/pricing`.
 *
 * Every money figure here is in the app's single base unit and must be rendered through
 * `fmt.money` - never printed raw, and never converted anywhere but at the point of display.
 *
 * Every one of these figures comes from the same `SectorProjector` the ledger posts from (see its
 * class doc on the server): a number shown here before the player commits is the number the ledger
 * will hold afterwards.
 */

/** One fare and everything the economy engine says it produces on this sector. */
export interface FarePricePoint {
  fare: number
  paxBooked: number
  seats: number
  loadFactorPercent: number
  revenue: number
  cost: number
  profit: number
}

/** One sampled point on the fare curve - the same shape plus where it sits relative to the
 *  reference fare, which is what makes the curve comparable between a short hop and a long haul. */
export interface FareCurvePoint {
  fare: number
  multipleOfReferenceFare: number
  paxBooked: number
  loadFactorPercent: number
  revenue: number
  cost: number
  profit: number
}

/**
 * Which aircraft the figures assume, and why - stated rather than hidden, because a sector is
 * genuinely worth different money to different airframes (seats feed bookings, weight feeds the
 * airport fees, block time feeds crew, maintenance and fuel).
 */
export interface PricingAssumedAircraft {
  aircraftTypeId: string
  typeName: string
  icaoType: string
  seats: number
  basis: string
  canOperate: boolean
}

export interface PricingAircraftOption {
  aircraftTypeId: string
  typeName: string
  icaoType: string
  seats: number
  ownedCount: number
  canOperate: boolean
  /** 'Range' or 'Runway' when this type physically can't operate the sector; null when it can. */
  blockedBy: string | null
}

/** GET /routes/{id}/pricing when the route's airports can't be resolved from world data. */
export interface RoutePricingUnavailable {
  routeId: string
  departureIcao: string
  arrivalIcao: string
  flightNumber: string | null
  distanceNm: number
  currentFare: number
  priceable: false
  reason: string
}

export interface RoutePricingAvailable {
  routeId: string
  departureIcao: string
  departureName: string | null
  arrivalIcao: string
  arrivalName: string | null
  flightNumber: string | null
  distanceNm: number
  priceable: true
  currentFare: number
  /** What the app would charge if the player expressed no opinion. */
  referenceFare: number
  /** The closed-form revenue peak from the demand model itself. */
  revenueMaximizingFare: number
  /** The highest-profit fare among the sampled curve points - "best of the fares sampled", never
   *  claimed as an exact optimum. */
  bestSampledProfitFare: number
  marketDemandPax: number
  fuelPricePerKg: number
  pricedAtUtc: string
  /** The band the save actually enforces, so the input can bound itself rather than letting the
   *  player discover the limit by being refused. */
  fareBand: { minimum: number; maximum: number }
  assumedAircraft: PricingAssumedAircraft
  aircraftOptions: PricingAircraftOption[]
  atFare: FarePricePoint
  atCurrentFare: FarePricePoint
  atReferenceFare: FarePricePoint
  curve: FareCurvePoint[]
  /** The facts behind the one sentence the player can disagree with - see PricingVerdict. */
  verdict: PricingVerdict
}

export type RoutePricingResponse = RoutePricingAvailable | RoutePricingUnavailable

/**
 * The verdict as facts, not as a finished sentence. Money is stored in one base unit and formatted
 * only at the point of display, so the server cannot compose a sentence containing a fare or a
 * profit without guessing at the reader's currency - it returns the facts and the UI writes the
 * sentence with `fmt.money`. See PlanningEndpoints.PricingVerdict.
 */
export interface PricingVerdict {
  kind: 'NobodyBooks' | 'AlreadyBest' | 'CouldEarnMore'
  paxBooked: number
  loadFactorPercent: number
  profit: number
  /** Only set when `kind` is 'CouldEarnMore'. */
  betterFare: number | null
  betterFarePaxBooked: number | null
  extraProfit: number | null
  pricedRelativeToSuggestion: 'above' | 'below' | 'exactly at'
  /** True when the airline owns more than one type, so the figures depend on which was assumed. */
  aircraftDependent: boolean
}

export interface RouteOpportunity {
  departureIcao: string
  departureName: string | null
  arrivalIcao: string
  arrivalName: string | null
  arrivalMunicipality: string | null
  arrivalCountry: string | null
  distanceNm: number
  blockMinutes: number
  suggestedFare: number
  marketDemandPax: number
  expectedPassengers: number
  seats: number
  loadFactorPercent: number
  revenuePerSector: number
  costPerSector: number
  profitPerSector: number
  aircraftTypeName: string
  reason: string
}

/** A pair worth flying that nothing in the fleet can - stated rather than hidden, in the same
 *  spirit as route creation's own refusals. */
export interface BlockedOpportunity {
  departureIcao: string
  arrivalIcao: string
  arrivalName: string | null
  arrivalCountry: string | null
  distanceNm: number
  marketDemandPax: number
  reason: string
}

export interface OpportunitiesResponse {
  bases: string[]
  fleetTypeCount: number
  opportunities: RouteOpportunity[]
  blocked: BlockedOpportunity[]
}

export interface FleetUtilisationRow {
  fleetAircraftId: string
  registration: string
  typeName: string
  seats: number
  locationIcao: string
  status: string
  reservedForPlayer: boolean
  scheduledSectorsPerWeek: number
}

export interface UnflyableRoute {
  routeId: string
  departureIcao: string
  arrivalIcao: string
  distanceNm: number
  reason: string | null
}

export interface SeatCappedRoute {
  routeId: string
  departureIcao: string
  arrivalIcao: string
  marketDemandPax: number
  seats: number
  typeName: string
  turnedAwayPerSector: number
}

export interface FleetSuggestion {
  aircraftTypeId: string
  typeName: string
  icaoType: string
  seats: number
  rangeNm: number
  alreadyOwned: boolean
  purchasePrice: number
  /** Null when the economy config has no lease rate for this type - it can still be bought. */
  monthlyLease: number | null
  leaseDeposit: number | null
  monthlyInsurance: number
  affordableToBuyNow: boolean
  affordableToLeaseNow: boolean
  unlocksRouteCount: number
  unlocksOpportunityCount: number
  extraSeatsOnBusyRoutes: number
  bestSector: string | null
  bestSectorProfit: number
  reason: string
}

export interface FleetAdviceResponse {
  cashBalance: number
  fleetSize: number
  idleAircraftCount: number
  /** The headline is willing to say "don't buy anything" - a planner that always finds a reason to
   *  spend money is not advice. */
  headline: string
  utilisation: FleetUtilisationRow[]
  unflyableRoutes: UnflyableRoute[]
  seatCappedRoutes: SeatCappedRoute[]
  suggestions: FleetSuggestion[]
}
