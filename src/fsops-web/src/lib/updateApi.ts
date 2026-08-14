import { get, post, put } from '@/lib/api'

/**
 * The updater's API surface.
 *
 * Every call here goes to FSOps' own server on localhost. The SPA never talks to GitHub - it
 * cannot, because the UI has no business making third-party requests of its own. The one
 * outbound call the update check needs is made
 * server-side, once, and only when the feature is switched on.
 */

export type UpdateDownloadState = 'none' | 'downloading' | 'ready' | 'failed'

/**
 * Which releases the updater may offer.
 *
 * - `stable` - released builds only. The default, including on a brand-new install where nothing
 *   has been stored: the server resolves an absent setting, an absent settings row and an
 *   unreadable database all to this.
 * - `development` - released builds and pre-releases, whichever is newest.
 *
 * The channel decides which build is offered and nothing else. A pre-release's installer is
 * verified against its published SHA-256 exactly as strictly as a stable one.
 */
export type UpdateChannel = 'stable' | 'development'

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
  /** Which releases may be offered. See {@link UpdateChannel}. */
  channel: UpdateChannel
  /**
   * This build is NEWER than anything the selected channel has - what happens the moment somebody
   * on a development build switches back to stable. Not an error and not an update: there is
   * nothing to offer, and offering the older release would be a downgrade dressed as an upgrade.
   * Shown as its own message rather than as "you are on the latest version", which would be false.
   */
  aheadOfChannel: boolean
  /** The newest version the channel holds, whether or not it is an update. */
  channelNewestVersion: string | null
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

/**
 * Chooses which releases may be offered. The server re-checks against the new channel before it
 * answers, so the status this resolves to is already about the channel just chosen rather than the
 * one just left - there is no intermediate render showing the old channel's release.
 */
export function setUpdateChannel(channel: UpdateChannel): Promise<UpdateStatus> {
  return put<UpdateStatus>('/update/channel', { channel })
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
