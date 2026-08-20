/**
 * Which aircraft the player can actually load in MSFS. Nothing on this screen changes how the
 * airline works today - it exists so contract flying, where a job arrives with an aircraft
 * attached, can only ever offer something that is really in their simulator.
 */

export type SimEdition = 'Standard' | 'Deluxe' | 'PremiumDeluxe'

/** Which edition ships an aircraft, or 'AddOn' for anything the player installed themselves. */
export type SimAircraftShipsWith = SimEdition | 'AddOn'

export type SimAircraftCategory =
  | 'LightSingle'
  | 'LightTwin'
  | 'UtilityTurboprop'
  | 'BusinessJet'
  | 'RegionalAirliner'
  | 'Narrowbody'
  | 'Widebody'

/**
 * Why FSOps believes the player can or cannot load an aircraft. Shown next to every row, because
 * "found in your Community folder" and "your edition includes this" are very different claims and
 * the player is the only one who can tell which is right.
 */
export type SimAircraftEvidence =
  | 'NotIncluded'
  | 'Edition'
  | 'InstalledOnDisk'
  | 'CommunityFolder'
  | 'TickedOn'
  | 'TickedOff'

/**
 * How a scan ended. Everything except 'Scanned' means "could not look" — and never "you own
 * nothing". MSFS 2024 streams most of its base content, so a scan can prove an aircraft is there
 * and can never prove one is missing.
 */
export type SimAircraftScanOutcome = 'NoFolder' | 'FolderMissing' | 'NotAPackagesFolder' | 'Scanned'

export interface SimAircraftPackage {
  packageFolder: string
  packageTitle: string
  /** What the package's own configuration declared, when FSOps could not match it to anything. */
  rawDesignator: string | null
  /** The catalogue entry this resolved to, or null when FSOps did not recognise the package. */
  typeDesignator: string | null
}

export interface SimAircraftScan {
  outcome: SimAircraftScanOutcome
  communityFolderPath: string | null
  scannedUtc: string
  packagesInspected: number
  aircraftPackages: SimAircraftPackage[]
  /** Base-content aircraft found on disk, by ICAO type designator. */
  basePackageTypeDesignators: string[]
}

export interface SimAircraftEntry {
  typeDesignator: string
  name: string
  manufacturer: string
  category: SimAircraftCategory
  seats: number
  payloadKg: number
  rangeNm: number
  cruiseTasKts: number
  shipsWith: SimAircraftShipsWith
  available: boolean
  evidence: SimAircraftEvidence
}

export interface SimAircraftState {
  edition: SimEdition
  /** What the player set by hand, or null when FSOps is finding the folder itself. */
  configuredCommunityFolderPath: string | null
  /** The folder FSOps will actually read — configured or found. Null means it could not find one. */
  effectiveCommunityFolderPath: string | null
  lastScan: SimAircraftScan | null
  aircraft: SimAircraftEntry[]
}
