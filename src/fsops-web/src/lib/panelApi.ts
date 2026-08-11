import { del, get, post } from '@/lib/api'

/**
 * Typed wrappers around the in-game panel API (see PanelEndpoints.cs / PanelPackageInstaller.cs).
 *
 * Lives in lib/ rather than beside the onboarding wizard because the panel is managed from two
 * places now: onboarding installs it once, and Settings shows its real state and can reinstall,
 * move or remove it afterwards. One module means those two can never drift into disagreeing about
 * the shape of a status response.
 */

export interface PanelCandidate {
  path: string
  source: string
  exists: boolean
}

export interface PanelPathValidation {
  valid: boolean
  reason: string | null
  resolvedPath: string | null
  /** Whether the folder is actually there. Reported separately from `valid`: a well-formed path to
   *  a folder that doesn't exist is worth accepting but not worth promising an install for. */
  exists: boolean
}

export interface PanelOperationResult {
  success: boolean
  reason: string | null
  installed: boolean
  installedPath: string | null
  installedVersion: string | null
  expectedVersion: string
  /** Whether the compiled toolbar component is on disk. Reported honestly - when it is false the
   *  files are installed but no FSOps button appears in the sim, and the UI says exactly that. */
  spbPresent: boolean
  toolbarWillAppearInSim: boolean
  filesWritten: number
  message: string
  /** Port baked into the installed panel's config, or null when nothing is installed. If FSOps has
   *  since moved port the installed panel keeps calling the old one and silently shows nothing in
   *  the sim - comparing these two is the only thing that catches it. */
  installedPort: string | null
  expectedPort: string | null
}

export interface PanelMoveResult {
  success: boolean
  reason: string | null
  message: string
  install: PanelOperationResult
  oldCopyRemoved: boolean
  oldCopyMessage: string
}

export function detectCommunityFolders(): Promise<PanelCandidate[]> {
  return get<PanelCandidate[]>('/panel/detect')
}

export function validateCommunityFolder(path: string): Promise<PanelPathValidation> {
  return post<PanelPathValidation>('/panel/validate', { path })
}

/**
 * Reads what is actually on disk. With no `path` it reports on the folder saved in settings; with
 * one it reports on that folder instead, which is how Settings can ask "is there still a copy in
 * the folder I'm about to move away from?" before the player commits to a change.
 *
 * Always resolves, even for a path the server refuses - an unusable folder comes back as
 * `success: false` with a `reason` worth showing, not as a thrown error.
 */
export function getPanelStatus(path?: string | null): Promise<PanelOperationResult> {
  return get<PanelOperationResult>('/panel/status', path ? { path } : undefined)
}

export function installPanel(path: string): Promise<PanelOperationResult> {
  return post<PanelOperationResult>('/panel/install', { path })
}

/** Installs into `toPath` and only then clears `fromPath`, server-side, so a failure can never
 *  leave the player with no panel at all. */
export function movePanel(fromPath: string | null, toPath: string): Promise<PanelMoveResult> {
  return post<PanelMoveResult>('/panel/move', { fromPath, toPath })
}

export function uninstallPanel(path: string): Promise<PanelOperationResult> {
  return del<PanelOperationResult>('/panel/uninstall', { path })
}
