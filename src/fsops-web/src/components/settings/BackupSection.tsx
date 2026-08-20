import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertTriangle, CheckCircle2, Clock, Download, Loader2, RotateCcw, Save, XCircle } from 'lucide-react'
import { toast } from 'sonner'

import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { ApiError } from '@/lib/api'
import {
  acknowledgeLastRestore,
  cancelPendingRestore,
  downloadBackup,
  fetchBackupStatus,
  formatBackupSize,
  saveBackupFile,
  uploadRestore,
  type BackupStatus,
} from '@/lib/backupApi'

/**
 * Saving the airline to a file, and putting one back.
 *
 * <p>Four things about this card are deliberate rather than incidental.</p>
 * <ul>
 *   <li><b>It says what is in a backup, here, not in a guide.</b> Somebody deciding whether this
 *   protects them will not go and read documentation first, and a backup whose contents are a
 *   surprise is worse than none: they will find out what it did not cover on the day they need it.</li>
 *   <li><b>Restoring takes two visible steps, because it genuinely does.</b> FSOps holds the
 *   database open the whole time it is running, so a restore cannot be applied to a live file — it
 *   is checked and staged now, and swapped in when FSOps next starts. Pretending otherwise would
 *   mean a restore that half-applied, which is worse than one that waits.</li>
 *   <li><b>A restore saves what it is about to replace first, automatically, and says where.</b>
 *   Picking the wrong file is exactly as unrecoverable as losing the database, and telling somebody
 *   where the old copy went only helps if it was actually made.</li>
 *   <li><b>The outcome survives the restart.</b> The player cannot watch a restore finish, so the
 *   result of the last one is reported back here rather than left to be inferred from whether the
 *   airline looks right.</li>
 * </ul>
 */
export function BackupSection() {
  const [status, setStatus] = useState<BackupStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const [failed, setFailed] = useState(false)
  const [backingUp, setBackingUp] = useState(false)
  const [restoring, setRestoring] = useState(false)
  const [confirm, setConfirm] = useState<File | null>(null)

  const fileInput = useRef<HTMLInputElement>(null)
  const cancelled = useRef(false)

  const load = useCallback(() => {
    fetchBackupStatus()
      .then((result) => {
        if (cancelled.current) return
        setStatus(result)
        setFailed(false)
      })
      .catch(() => {
        if (cancelled.current) return
        setFailed(true)
      })
      .finally(() => {
        if (!cancelled.current) setLoading(false)
      })
  }, [])

  useEffect(() => {
    cancelled.current = false
    load()
    return () => {
      cancelled.current = true
    }
  }, [load])

  async function handleBackUp() {
    setBackingUp(true)
    try {
      const backup = await downloadBackup(status?.suggestedFileName ?? 'FSOps backup.fsopsbak')
      const saved = await saveBackupFile(backup)
      if (saved) {
        toast.success(`Backed up to ${backup.fileName}.`)
      }
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not create the backup. Try again in a moment.')
    } finally {
      setBackingUp(false)
    }
  }

  function handleFileChosen(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    // Cleared straight away so choosing the same file twice in a row still fires a change event.
    event.target.value = ''
    if (file) setConfirm(file)
  }

  async function handleRestore(file: File) {
    setRestoring(true)
    try {
      const next = await uploadRestore(file)
      setStatus(next)
      setConfirm(null)
      toast.success('Backup checked and ready. Restart FSOps to finish restoring it.')
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'That file could not be restored.')
    } finally {
      setRestoring(false)
    }
  }

  async function handleCancelPending() {
    try {
      setStatus(await cancelPendingRestore())
      toast.success('Restore cancelled. Your airline is unchanged.')
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not cancel the restore.')
    }
  }

  async function handleDismissResult() {
    try {
      setStatus(await acknowledgeLastRestore())
    } catch {
      // Dismissing a notice is not worth a message of its own; it will still be there next time.
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <Save className="size-4 text-accent" />
          Backup and restore
        </CardTitle>
        <CardDescription>
          Everything FSOps knows about your airline lives in one file on this computer. Saving a copy of it somewhere
          else is the only thing that protects you if that file is lost or damaged.
        </CardDescription>
      </CardHeader>

      <CardContent className="space-y-5">
        {loading && (
          <div className="space-y-2">
            <Skeleton className="h-5 w-48" />
            <Skeleton className="h-4 w-72" />
            <Skeleton className="h-4 w-64" />
          </div>
        )}

        {!loading && failed && (
          <div className="flex flex-col gap-3 rounded-md border border-danger/30 bg-danger/5 p-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex items-start gap-2 text-sm text-danger">
              <XCircle className="mt-0.5 size-4 shrink-0" />
              <span>Could not read the backup status.</span>
            </div>
            <Button type="button" variant="outline" size="sm" onClick={load} className="shrink-0">
              Try again
            </Button>
          </div>
        )}

        {!loading && !failed && status && (
          <>
            {status.lastRestore && (
              <div
                className={
                  status.lastRestore.succeeded
                    ? 'space-y-2 rounded-md border border-success/40 bg-success/5 p-3'
                    : 'space-y-2 rounded-md border border-danger/40 bg-danger/5 p-3'
                }
              >
                <p className="flex items-center gap-2 text-sm font-medium">
                  {status.lastRestore.succeeded ? (
                    <CheckCircle2 className="size-4 text-success" aria-hidden />
                  ) : (
                    <AlertTriangle className="size-4 text-danger" aria-hidden />
                  )}
                  {status.lastRestore.succeeded
                    ? `Restored from ${status.lastRestore.sourceFileName}`
                    : 'The last restore did not finish'}
                </p>
                <p className="text-xs text-muted-foreground">
                  {status.lastRestore.succeeded ? (
                    <>
                      Applied {formatMoment(status.lastRestore.appliedUtc)}. The airline that was here before was saved
                      to <span className="break-all font-mono text-foreground">{status.lastRestore.safetyCopyPath}</span>
                      , and FSOps will not delete it.
                    </>
                  ) : (
                    status.lastRestore.message
                  )}
                </p>
                <Button type="button" variant="outline" size="sm" onClick={() => void handleDismissResult()}>
                  Got it
                </Button>
              </div>
            )}

            {status.pendingRestore && (
              <div className="space-y-2 rounded-md border border-warning/40 bg-warning/5 p-3">
                <p className="flex items-center gap-2 text-sm font-medium">
                  <Clock className="size-4 text-warning" aria-hidden />
                  A restore is waiting for FSOps to restart
                </p>
                <p className="text-xs text-muted-foreground">
                  <span className="font-medium text-foreground">{status.pendingRestore.sourceFileName}</span> has been
                  checked and is ready. FSOps holds your airline&rsquo;s file open while it is running, so the swap
                  happens as it starts. <span className="font-medium text-foreground">Close FSOps and open it
                  again</span> to finish &mdash; nothing has changed yet.
                </p>
                <p className="text-xs text-muted-foreground">
                  Your current airline has already been saved to{' '}
                  <span className="break-all font-mono text-foreground">{status.pendingRestore.safetyCopyPath}</span>.
                </p>
                <Button type="button" variant="outline" size="sm" onClick={() => void handleCancelPending()}>
                  <RotateCcw className="mr-2 size-4" />
                  Cancel the restore
                </Button>
              </div>
            )}

            <div className="rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
              <p className="font-medium text-foreground">What a backup contains</p>
              <p className="mt-1">
                A complete copy of the FSOps database: your airline, fleet, routes, pilots and their schedules, every
                flight you have flown, your finances and loans, your settings, and the world airport data.
              </p>
              <p className="mt-1">
                It does not contain anything from Microsoft Flight Simulator itself &mdash; no aircraft, liveries or
                flight plans &mdash; and no FSOps log files or downloaded installers. Restoring one replaces your whole
                airline with the one in the file; it does not merge them.
              </p>
            </div>

            <div className="flex flex-col gap-3 border-t border-border pt-4 sm:flex-row sm:items-center sm:justify-between">
              <div>
                <p className="text-sm font-medium">Back up now</p>
                <p className="text-xs text-muted-foreground">
                  You choose where it goes. Safe to do at any time, including mid-flight &mdash; nothing is paused and
                  nothing is changed. About {formatBackupSize(status.databaseSizeBytes)} before compression.
                </p>
              </div>
              <Button type="button" className="shrink-0" disabled={backingUp} onClick={() => void handleBackUp()}>
                {backingUp ? <Loader2 className="mr-2 size-4 animate-spin" /> : <Download className="mr-2 size-4" />}
                {backingUp ? 'Backing up…' : 'Back up'}
              </Button>
            </div>

            <div className="flex flex-col gap-3 border-t border-border pt-4 sm:flex-row sm:items-center sm:justify-between">
              <div>
                <p className="text-sm font-medium">Restore from a backup</p>
                <p className="text-xs text-muted-foreground">
                  FSOps checks the file before anything happens, and saves your current airline first. A backup made by
                  a newer version of FSOps is refused rather than attempted; an older one is fine.
                </p>
              </div>
              <input
                ref={fileInput}
                type="file"
                accept=".fsopsbak"
                className="sr-only"
                aria-label="Backup file to restore"
                onChange={handleFileChosen}
              />
              <Button
                type="button"
                variant="outline"
                className="shrink-0"
                disabled={restoring || status.pendingRestore !== null}
                onClick={() => fileInput.current?.click()}
              >
                Choose a backup file
              </Button>
            </div>
          </>
        )}
      </CardContent>

      <Dialog open={confirm !== null} onOpenChange={(open) => !open && setConfirm(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <AlertTriangle className="size-5 text-warning" />
              Restore from this backup?
            </DialogTitle>
            <DialogDescription>
              <span className="font-medium text-foreground">{confirm?.name}</span> will replace your entire airline
              &mdash; fleet, routes, pilots, flights and finances &mdash; with whatever is in it. Your current airline is
              saved to FSOps&rsquo; own backups folder first, automatically, and you will be told exactly where.
              Restoring only finishes when you next start FSOps.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setConfirm(null)} disabled={restoring}>
              Cancel
            </Button>
            <Button type="button" onClick={() => confirm && void handleRestore(confirm)} disabled={restoring}>
              {restoring ? 'Checking the file…' : 'Check and prepare the restore'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}

function formatMoment(iso: string): string {
  const parsed = new Date(iso)
  if (Number.isNaN(parsed.getTime())) return 'recently'
  return parsed.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}
