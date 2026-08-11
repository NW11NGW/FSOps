/**
 * The panel API used to live here, back when the onboarding wizard was its only caller. Settings
 * now manages the panel too (status, reinstall, move, remove), so the real module moved to
 * `@/lib/panelApi` where both can share one definition of a status response.
 *
 * Re-exported rather than deleted so the wizard's existing imports keep pointing somewhere
 * sensible; prefer importing from `@/lib/panelApi` directly in anything new.
 */
export {
  detectCommunityFolders,
  getPanelStatus,
  installPanel,
  movePanel,
  uninstallPanel,
  validateCommunityFolder,
  type PanelCandidate,
  type PanelMoveResult,
  type PanelOperationResult,
  type PanelPathValidation,
} from '@/lib/panelApi'
