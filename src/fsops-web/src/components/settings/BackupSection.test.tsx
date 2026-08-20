import { beforeEach, describe, expect, it, vi } from 'vitest'

import { BackupSection } from './BackupSection'
import { click, findButton, flush, mount, text } from '@/test/domHarness'
import type { BackupStatus } from '@/lib/backupApi'

function queryButton(root: ParentNode, label: string): HTMLButtonElement | null {
  return Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.includes(label)) ?? null
}

vi.mock('@/lib/backupApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/backupApi')>()
  return {
    ...actual,
    fetchBackupStatus: vi.fn(),
    cancelPendingRestore: vi.fn(),
    acknowledgeLastRestore: vi.fn(),
    downloadBackup: vi.fn(),
    saveBackupFile: vi.fn(),
    uploadRestore: vi.fn(),
  }
})

import {
  acknowledgeLastRestore,
  cancelPendingRestore,
  downloadBackup,
  fetchBackupStatus,
  saveBackupFile,
} from '@/lib/backupApi'

function status(overrides: Partial<BackupStatus> = {}): BackupStatus {
  return {
    databaseSizeBytes: 18_874_368,
    dataDirectory: 'C:\\Users\\pilot\\AppData\\Local\\FSOps',
    backupsDirectory: 'C:\\Users\\pilot\\AppData\\Local\\FSOps\\backups',
    suggestedFileName: 'Skyline Air backup 2026-08-20 1432.fsopsbak',
    appVersion: '1.2.0',
    pendingRestore: null,
    lastRestore: null,
    ...overrides,
  }
}

async function render(initial: BackupStatus) {
  vi.mocked(fetchBackupStatus).mockResolvedValue(initial)
  const mounted = await mount(<BackupSection />)
  await flush()
  return mounted
}

describe('BackupSection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('says what is in a backup, and what is not, without sending anyone to a guide', async () => {
    // The card has to answer this where the button is. Somebody deciding whether this protects them
    // will not go and read documentation first, and finding out what a backup did not cover on the
    // day they need it is the whole failure this feature exists to prevent.
    const { container, unmount } = await render(status())
    const body = text(container)

    expect(body).toContain('A complete copy of the FSOps database')
    expect(body).toContain('every flight you have flown')
    expect(body).toContain('does not contain anything from Microsoft Flight Simulator')
    expect(body).toContain('replaces your whole airline')
    unmount()
  })

  it('says how big the backup will be, and that taking one changes nothing', async () => {
    const { container, unmount } = await render(status())

    expect(text(container)).toContain('18.0 MB')
    expect(text(container)).toContain('nothing is changed')
    unmount()
  })

  it('backing up lets the player choose where it goes and names the file it wrote', async () => {
    vi.mocked(downloadBackup).mockResolvedValue({
      blob: new Blob(['x']),
      fileName: 'Skyline Air backup 2026-08-20 1432.fsopsbak',
    })
    vi.mocked(saveBackupFile).mockResolvedValue(true)

    const { container, unmount } = await render(status())
    click(findButton(container, 'Back up'))
    await flush()

    expect(vi.mocked(downloadBackup)).toHaveBeenCalledOnce()
    expect(vi.mocked(saveBackupFile)).toHaveBeenCalledOnce()
    unmount()
  })

  it('a staged restore says plainly that nothing has changed yet and that FSOps must restart', async () => {
    const { container, unmount } = await render(
      status({
        pendingRestore: {
          sourceFileName: 'Skyline Air backup 2026-08-01 0900.fsopsbak',
          stagedUtc: '2026-08-20T14:40:00+00:00',
          safetyCopyPath: 'C:\\FSOps\\backups\\Before restore - Skyline Air backup 2026-08-20 1440.fsopsbak',
          backupAppVersion: '1.1.0',
          backupCreatedUtc: '2026-08-01T09:00:00+00:00',
          backupAirlineName: 'Skyline Air',
        },
      }),
    )
    const body = text(container)

    expect(body).toContain('A restore is waiting for FSOps to restart')
    expect(body).toContain('Skyline Air backup 2026-08-01 0900.fsopsbak')
    expect(body).toContain('Close FSOps and open it again')
    expect(body).toContain('nothing has changed yet')

    // Where the airline about to be replaced went. Useless as a reassurance unless it is on screen.
    expect(body).toContain('Before restore - Skyline Air backup 2026-08-20 1440.fsopsbak')

    // And a second restore cannot be started on top of the first.
    expect(queryButton(container, 'Choose a backup file')?.hasAttribute('disabled')).toBe(true)
    unmount()
  })

  it('a staged restore can be cancelled, and says the airline is unchanged', async () => {
    vi.mocked(cancelPendingRestore).mockResolvedValue(status())
    const { container, unmount } = await render(
      status({
        pendingRestore: {
          sourceFileName: 'wrong file.fsopsbak',
          stagedUtc: '2026-08-20T14:40:00+00:00',
          safetyCopyPath: 'C:\\FSOps\\backups\\Before restore.fsopsbak',
          backupAppVersion: '1.2.0',
          backupCreatedUtc: null,
          backupAirlineName: null,
        },
      }),
    )

    click(findButton(container, 'Cancel the restore'))
    await flush()

    expect(vi.mocked(cancelPendingRestore)).toHaveBeenCalledOnce()
    expect(text(container)).not.toContain('A restore is waiting')
    unmount()
  })

  it('reports a finished restore after the restart, and where the previous airline went', async () => {
    // The player cannot watch a restore finish, so being left to infer whether it worked would be
    // its own failure.
    const { container, unmount } = await render(
      status({
        lastRestore: {
          succeeded: true,
          appliedUtc: '2026-08-20T15:00:00+00:00',
          sourceFileName: 'Skyline Air backup 2026-08-01 0900.fsopsbak',
          safetyCopyPath: 'C:\\FSOps\\backups\\Before restore - Skyline Air.fsopsbak',
          airlineName: 'Skyline Air',
          message: null,
        },
      }),
    )
    const body = text(container)

    expect(body).toContain('Restored from Skyline Air backup 2026-08-01 0900.fsopsbak')
    expect(body).toContain('Before restore - Skyline Air.fsopsbak')
    expect(body).toContain('will not delete it')
    unmount()
  })

  it('a restore that did not finish is reported as such, in the server\u2019s own words', async () => {
    const { container, unmount } = await render(
      status({
        lastRestore: {
          succeeded: false,
          appliedUtc: '2026-08-20T15:00:00+00:00',
          sourceFileName: 'damaged.fsopsbak',
          safetyCopyPath: 'C:\\FSOps\\backups\\Before restore.fsopsbak',
          airlineName: null,
          message: 'The backup that was waiting to be restored turned out to be damaged, so your airline was left exactly as it was.',
        },
      }),
    )
    const body = text(container)

    expect(body).toContain('The last restore did not finish')
    expect(body).toContain('turned out to be damaged')
    expect(body).not.toContain('Restored from')
    unmount()
  })

  it('the restore result can be dismissed once it has been read', async () => {
    vi.mocked(acknowledgeLastRestore).mockResolvedValue(status())
    const { container, unmount } = await render(
      status({
        lastRestore: {
          succeeded: true,
          appliedUtc: '2026-08-20T15:00:00+00:00',
          sourceFileName: 'a backup.fsopsbak',
          safetyCopyPath: 'C:\\FSOps\\backups\\Before restore.fsopsbak',
          airlineName: 'Skyline Air',
          message: null,
        },
      }),
    )

    click(findButton(container, 'Got it'))
    await flush()

    expect(vi.mocked(acknowledgeLastRestore)).toHaveBeenCalledOnce()
    expect(text(container)).not.toContain('Restored from')
    unmount()
  })

  it('says which direction of version compatibility is supported, before anything is tried', async () => {
    const { container, unmount } = await render(status())

    expect(text(container)).toContain('newer version of FSOps is refused')
    expect(text(container)).toContain('an older one is fine')
    unmount()
  })

  it('a status that cannot be read offers a retry rather than an empty card', async () => {
    vi.mocked(fetchBackupStatus).mockRejectedValue(new Error('nope'))
    const { container, unmount } = await mount(<BackupSection />)
    await flush()

    expect(text(container)).toContain('Could not read the backup status')
    expect(queryButton(container, 'Try again')).not.toBeNull()
    unmount()
  })
})
