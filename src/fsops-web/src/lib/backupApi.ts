import { ApiError, get, post } from '@/lib/api'

/**
 * Saving the airline to a file, and putting one back.
 *
 * Two of these calls deliberately do not go through `@/lib/api`: a backup is a file rather than
 * JSON in both directions, and forcing it through a helper that serialises bodies and parses
 * responses would mean loading the whole database into a string. They use `fetch` directly and
 * normalise their failures into the same {@link ApiError} everything else throws, so callers do not
 * have to care which kind of request it was.
 */

const API_BASE = '/api/v1'

export interface PendingRestore {
  /** The file the player picked, named back to them so they can confirm it is the right one. */
  sourceFileName: string
  stagedUtc: string
  /** Where the airline this restore will replace was saved, automatically, before staging. */
  safetyCopyPath: string
  backupAppVersion: string | null
  backupCreatedUtc: string | null
  backupAirlineName: string | null
}

export interface LastRestore {
  succeeded: boolean
  appliedUtc: string
  sourceFileName: string
  safetyCopyPath: string
  airlineName: string | null
  /** Set only when something went wrong. Written for the player. */
  message: string | null
}

export interface BackupStatus {
  databaseSizeBytes: number
  dataDirectory: string
  backupsDirectory: string
  /** The airline's name and today's date - what the save dialog is pre-filled with. */
  suggestedFileName: string
  appVersion: string
  /** A restore waiting for the next start, or null. */
  pendingRestore: PendingRestore | null
  /** What happened last time one was applied, or null. */
  lastRestore: LastRestore | null
}

export function fetchBackupStatus(): Promise<BackupStatus> {
  return get<BackupStatus>('/backup/status')
}

export function cancelPendingRestore(): Promise<BackupStatus> {
  return post<BackupStatus>('/backup/restore/cancel')
}

export function acknowledgeLastRestore(): Promise<BackupStatus> {
  return post<BackupStatus>('/backup/restore/acknowledge')
}

export interface DownloadedBackup {
  blob: Blob
  /** The name the server chose. Preferred over anything the client could invent, since the server
   *  is the one that knows the airline's name at the moment the copy was taken. */
  fileName: string
}

/**
 * Asks the server for a fresh backup and returns it in memory, along with the name it should be
 * saved under. Deliberately does not decide where it goes - that is the player's choice, made in
 * {@link saveBackupFile}.
 */
export async function downloadBackup(fallbackName: string): Promise<DownloadedBackup> {
  let response: Response
  try {
    response = await fetch(`${API_BASE}/backup/file`, { method: 'GET' })
  } catch {
    throw new ApiError(0, 'Could not reach FSOps to take a backup. Try again in a moment.')
  }

  if (!response.ok) {
    let message = 'FSOps could not create the backup.'
    try {
      const body: unknown = await response.json()
      if (body && typeof body === 'object' && typeof (body as { error?: unknown }).error === 'string') {
        message = (body as { error: string }).error
      }
    } catch {
      // Not JSON. The generic message above is what the player gets.
    }
    throw new ApiError(response.status, message)
  }

  return {
    blob: await response.blob(),
    fileName: fileNameFromDisposition(response.headers.get('content-disposition')) ?? fallbackName,
  }
}

/**
 * Sends a file the player chose to the server to be checked and staged. The raw bytes, not a form:
 * there is exactly one file, and a multipart body would add a size ceiling that a long-running
 * airline could quietly exceed.
 */
export async function uploadRestore(file: File): Promise<BackupStatus> {
  let response: Response
  try {
    response = await fetch(`${API_BASE}/backup/restore?fileName=${encodeURIComponent(file.name)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/octet-stream' },
      body: file,
    })
  } catch {
    throw new ApiError(0, 'Could not reach FSOps to read that file. Try again in a moment.')
  }

  const body: unknown = await response.json().catch(() => undefined)

  if (!response.ok) {
    const message =
      body && typeof body === 'object' && typeof (body as { error?: unknown }).error === 'string'
        ? (body as { error: string }).error
        : 'That file could not be restored.'
    throw new ApiError(response.status, message)
  }

  return body as BackupStatus
}

/**
 * Puts a downloaded backup somewhere the player chose.
 *
 * Uses the browser's save picker when there is one, which is a real "where do you want this"
 * dialog with the suggested name already filled in. Falls back to an ordinary download when there
 * is not - the file still arrives, it just lands wherever downloads normally go. Returns false when
 * the player closed the dialog without saving, so the caller can stay quiet instead of claiming a
 * backup that does not exist.
 */
export async function saveBackupFile(backup: DownloadedBackup): Promise<boolean> {
  const picker = (window as unknown as { showSaveFilePicker?: ShowSaveFilePicker }).showSaveFilePicker

  if (typeof picker === 'function') {
    try {
      const handle = await picker({
        suggestedName: backup.fileName,
        types: [{ description: 'FSOps backup', accept: { 'application/octet-stream': ['.fsopsbak'] } }],
      })
      const writable = await handle.createWritable()
      await writable.write(backup.blob)
      await writable.close()
      return true
    } catch (err) {
      // A closed dialog is a decision, not a failure. Anything else falls through to the plain
      // download rather than leaving the player with no way to save at all.
      if (err instanceof DOMException && err.name === 'AbortError') return false
    }
  }

  const url = URL.createObjectURL(backup.blob)
  try {
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = backup.fileName
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
  } finally {
    // Revoked on a later tick so the download has actually started reading it first.
    window.setTimeout(() => URL.revokeObjectURL(url), 30_000)
  }

  return true
}

interface FileSystemWritable {
  write: (data: Blob) => Promise<void>
  close: () => Promise<void>
}

interface FileSystemHandle {
  createWritable: () => Promise<FileSystemWritable>
}

type ShowSaveFilePicker = (options: {
  suggestedName?: string
  types?: { description: string; accept: Record<string, string[]> }[]
}) => Promise<FileSystemHandle>

/** Pulls the file name out of a Content-Disposition header, preferring the RFC 5987 form so an
 *  airline with an accented name keeps it. */
function fileNameFromDisposition(header: string | null): string | null {
  if (!header) return null

  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(header)
  if (encoded?.[1]) {
    try {
      return decodeURIComponent(encoded[1].trim())
    } catch {
      // A malformed header is not worth failing a backup over - fall through to the plain form.
    }
  }

  const plain = /filename="?([^";]+)"?/i.exec(header)
  return plain?.[1]?.trim() ?? null
}

/** Bytes as a short human string, so "about how big is this" can be answered before pressing a
 *  button. Matches the updater's own formatting. */
export function formatBackupSize(bytes: number): string {
  if (bytes <= 0) return 'empty'
  const megabytes = bytes / (1024 * 1024)
  return megabytes < 1 ? `${Math.round(bytes / 1024)} KB` : `${megabytes.toFixed(1)} MB`
}
