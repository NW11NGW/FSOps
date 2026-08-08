/**
 * Client-side fallback only - used when GET /fleet/registration-suggestion can't be reached, so a
 * flaky connection never blocks the buy/lease dialog outright. The real, country-correct suggestion
 * always comes from the server (AircraftRegistrationGenerator in FSOps.Core.Airlines); this never
 * tries to replicate that logic, it just produces SOME plausible placeholder the player can edit or
 * re-randomise once the connection recovers. The server re-validates format and uniqueness on
 * submit regardless, so this fallback being generic is not a correctness risk.
 */
export const AircraftRegistrationGenerator = {
  fallback(): string {
    const letters = Array.from({ length: 4 }, () => String.fromCharCode(65 + Math.floor(Math.random() * 26))).join('')
    return `FS-${letters}`
  },
}
