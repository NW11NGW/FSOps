import { get, post, put } from '@/lib/api'

/**
 * The updater's API surface.
 *
 * Every call here goes to FSOps' own server on localhost. The SPA never talks to GitHub - it
 * cannot, because the UI also runs inside an MSFS in-game panel with no reliable internet and no
 * business making third-party requests. The one outbound call the update check needs is made
 * server-side, once, and only when the feature is switched on.
 */

export type UpdateDownloadState = 'none' | 'downloading' | 'ready' | 'failed'

export interface UpdateStatus {
  /** The kill switch. When false the server makes no outbound request of any kind. */
  enabled: boolean
  /** A background check is running right now. Nothing waits on it. */
  checking: boolean
  currentVersion: string
  /** The newest stable release, or null when this build is already current. */
  latestVersion: string | null
  updateAvailable: boolean
  /** The user has already said no to exactly this version. */
  dismissed: boolean
  lastCheckedUtc: string | null
  /**
   * The last attempt could not reach GitHub. Deliberately never surfaced as an error - it is
   * shown, if at all, as a quiet "couldn't check" line, because having no internet is not a fault.
   */
  lastCheckFailed: boolean
  releaseUrl: string | null
  releaseNotes: string | null
  releasePublishedUtc: string | null
  /** True only when the release ships BOTH an installer and its .sha256. Without a checksum there
   *  is nothing to verify a download against, so the in-app download is not offered at all. */
  downloadAvailable: boolean
  downloadState: UpdateDownloadState
  /** Set only for a file whose SHA-256 matched the release's published checksum. */
  downloadFileName: string | null
  downloadSha256: string | null
  downloadedBytes: number
  downloadMessage: string | null
}

/** Reads the cached status. Never waits on the network; may start a background check. */
export function fetchUpdateStatus(): Promise<UpdateStatus> {
  return get<UpdateStatus>('/update/status')
}

/** An explicit "check now". Still cannot fail - an unreachable GitHub comes back as no update. */
export function checkForUpdateNow(): Promise<UpdateStatus> {
  return post<UpdateStatus>('/update/check')
}

export function setUpdateChecksEnabled(enabled: boolean): Promise<UpdateStatus> {
  return put<UpdateStatus>('/update/preferences', { enabled })
}

export function dismissUpdate(): Promise<UpdateStatus> {
  return post<UpdateStatus>('/update/dismiss')
}

/** Starts the download. Returns immediately - poll the status for the outcome. */
export function startUpdateDownload(): Promise<UpdateStatus> {
  return post<UpdateStatus>('/update/download')
}

/** Opens the folder holding the verified installer. FSOps never runs the installer itself. */
export function revealUpdateDownload(): Promise<{ opened: boolean; folder: string }> {
  return post<{ opened: boolean; folder: string }>('/update/reveal')
}

/** Bytes as a short human string for the download line ("42.1 MB"). */
export function formatDownloadedBytes(bytes: number): string {
  if (bytes <= 0) return '0 MB'
  const megabytes = bytes / (1024 * 1024)
  return megabytes < 1 ? `${Math.round(bytes / 1024)} KB` : `${megabytes.toFixed(1)} MB`
}

/** A SHA-256 shortened for display, keeping both ends so it can still be eyeballed against the
 *  release page. The full value stays in the DOM's title attribute. */
export function shortenHash(hash: string): string {
  return hash.length <= 20 ? hash : `${hash.slice(0, 10)}…${hash.slice(-10)}`
}
