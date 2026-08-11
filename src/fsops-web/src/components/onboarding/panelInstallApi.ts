import { get, post } from '@/lib/api'

/**
 * Thin typed wrappers around the panel-install API (see PanelEndpoints.cs /
 * PanelPackageInstaller.cs). Kept here rather than in a shared lib file because the onboarding
 * "MSFS panel" step is currently the only frontend consumer - the Settings-page equivalent
 * (editing/reinstalling/uninstalling the panel later) is a separate, not-yet-built piece of UI
 * owned elsewhere; see the onboarding module's own notes on scope.
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
}

export interface PanelOperationResult {
  success: boolean
  reason: string | null
  installed: boolean
  installedPath: string | null
  installedVersion: string | null
  expectedVersion: string
  spbPresent: boolean
  toolbarWillAppearInSim: boolean
  filesWritten: number
  message: string
}

export function detectCommunityFolders(): Promise<PanelCandidate[]> {
  return get<PanelCandidate[]>('/panel/detect')
}

export function validateCommunityFolder(path: string): Promise<PanelPathValidation> {
  return post<PanelPathValidation>('/panel/validate', { path })
}

export function installPanel(path: string): Promise<PanelOperationResult> {
  return post<PanelOperationResult>('/panel/install', { path })
}
