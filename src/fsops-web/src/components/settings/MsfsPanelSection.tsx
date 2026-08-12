import { useCallback, useEffect, useRef, useState } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  FolderOpen,
  Loader2,
  MonitorPlay,
  RefreshCw,
  RotateCcw,
  Trash2,
  XCircle,
} from 'lucide-react'
import { toast } from 'sonner'

import { Badge, type BadgeProps } from '@/components/ui/badge'
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
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { useSettings } from '@/hooks/useSettings'
import { ApiError } from '@/lib/api'
import {
  detectCommunityFolders,
  getPanelStatus,
  installPanel,
  movePanel,
  uninstallPanel,
  validateCommunityFolder,
  type PanelCandidate,
  type PanelOperationResult,
  type PanelPathValidation,
} from '@/lib/panelApi'
import { cn } from '@/lib/utils'

type LoadState = 'loading' | 'ready' | 'error'
type BusyAction = 'install' | 'repair' | 'uninstall' | 'save' | null

/** How a folder change should treat the copy already sitting in the old folder. */
type FolderChangeMode = 'plain' | 'move' | 'removeOld'

/**
 * The one place the MSFS in-game panel is managed after onboarding: the Community folder itself,
 * what is actually on disk in it, and the install / repair / remove actions.
 *
 * <p>Folder and panel deliberately live in a single card rather than two. They used to be split —
 * a "Simulator" card that saved the path and an install that only ever happened once, during
 * onboarding — and the result was that changing the folder here stored a new path while the panel
 * stayed behind in the old one, with nothing in the app able to notice. Anything that can silently
 * disagree with the folder it belongs to eventually does.</p>
 *
 * <p>Status is read from disk on load rather than remembered, because the interesting failures all
 * happen outside FSOps: the player deletes the folder, reinstalls the sim, upgrades FSOps past the
 * installed package version, or removes the package by hand.</p>
 */
export function MsfsPanelSection() {
  const { settings, updateSettings } = useSettings()
  const savedPath = settings.communityFolderPath

  const [path, setPath] = useState(savedPath ?? '')
  const [edited, setEdited] = useState(false)
  const [browseOpen, setBrowseOpen] = useState(false)
  const [busy, setBusy] = useState<BusyAction>(null)

  const [statusState, setStatusState] = useState<LoadState>('loading')
  const [status, setStatus] = useState<PanelOperationResult | null>(null)
  const [statusError, setStatusError] = useState<string | null>(null)
  const [reloadToken, setReloadToken] = useState(0)

  const [candidates, setCandidates] = useState<PanelCandidate[]>([])
  const [detectState, setDetectState] = useState<LoadState>('loading')

  const [validation, setValidation] = useState<PanelPathValidation | null>(null)
  const [validating, setValidating] = useState(false)
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const [pendingPath, setPendingPath] = useState<string | null | undefined>(undefined)
  const [confirmUninstall, setConfirmUninstall] = useState(false)
  const [dialogError, setDialogError] = useState<string | null>(null)

  // The folder-change dialog fades out rather than vanishing, so it is still on screen for a beat
  // after pendingPath is cleared. Rendering it from the last value it was opened with keeps its
  // wording from flipping to a different question on the way out.
  const lastPendingPath = useRef<string | null>(null)
  const lastOldPath = useRef<string | null>(null)
  if (pendingPath !== undefined) {
    lastPendingPath.current = pendingPath
    lastOldPath.current = savedPath
  }

  const reload = useCallback(() => setReloadToken((t) => t + 1), [])

  // Settings arrive asynchronously, so the field can't just be seeded once at mount - it has to
  // pick up the saved path when it lands. Anything the player has typed wins over that, always.
  useEffect(() => {
    if (!edited) setPath(savedPath ?? '')
  }, [savedPath, edited])

  useEffect(() => {
    let cancelled = false
    setStatusState('loading')
    setStatusError(null)

    getPanelStatus()
      .then((result) => {
        if (cancelled) return
        setStatus(result)
        setStatusState('ready')
      })
      .catch((err: unknown) => {
        if (cancelled) return
        setStatusError(err instanceof ApiError ? err.message : 'Could not read the panel status.')
        setStatusState('error')
      })

    return () => {
      cancelled = true
    }
  }, [savedPath, reloadToken])

  // Only worth scanning the machine when there's no folder configured - that's the one state where
  // a list of candidates is the answer rather than clutter.
  useEffect(() => {
    if (savedPath) {
      setDetectState('ready')
      return
    }

    let cancelled = false
    setDetectState('loading')
    detectCommunityFolders()
      .then((result) => {
        if (cancelled) return
        setCandidates(result)
        setDetectState('ready')
      })
      .catch(() => {
        if (cancelled) return
        setDetectState('error')
      })

    return () => {
      cancelled = true
    }
  }, [savedPath])

  const trimmed = path.trim()
  const unchanged = trimmed === (savedPath ?? '')

  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current)

    if (!trimmed || unchanged) {
      setValidation(null)
      setValidating(false)
      return
    }

    setValidating(true)
    debounceRef.current = setTimeout(() => {
      validateCommunityFolder(trimmed)
        .then(setValidation)
        .catch(() =>
          setValidation({
            valid: false,
            reason: 'Could not check this path — check your connection.',
            resolvedPath: null,
            exists: false,
          }),
        )
        .finally(() => setValidating(false))
    }, 350)

    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current)
    }
  }, [trimmed, unchanged])

  function describeError(err: unknown, fallback: string): string {
    return err instanceof ApiError ? err.message : fallback
  }

  async function runInstall(kind: 'install' | 'repair') {
    if (!savedPath) return
    setBusy(kind)
    try {
      const result = await installPanel(savedPath)
      toast.success(result.message)
      reload()
    } catch (err) {
      toast.error(describeError(err, kind === 'repair' ? 'Could not repair the panel.' : 'Could not install the panel.'))
    } finally {
      setBusy(null)
    }
  }

  async function runUninstall() {
    if (!savedPath) return
    setBusy('uninstall')
    try {
      const result = await uninstallPanel(savedPath)
      toast.success(result.message)
      setConfirmUninstall(false)
      reload()
    } catch (err) {
      toast.error(describeError(err, 'Could not remove the panel.'))
    } finally {
      setBusy(null)
    }
  }

  /** Save pressed. Anything installed in the old folder gets a decision first, never a silent orphan. */
  function requestFolderChange() {
    const next = trimmed || null
    if (next === (savedPath ?? null)) return
    setDialogError(null)

    if (status?.installed && savedPath) {
      setPendingPath(next)
      return
    }

    void commitFolderChange(next, 'plain')
  }

  async function commitFolderChange(next: string | null, mode: FolderChangeMode) {
    setBusy('save')
    setDialogError(null)
    try {
      if (mode === 'move' && next) {
        const result = await movePanel(savedPath, next)
        await updateSettings({ communityFolderPath: next })
        toast.success(`${result.install.message} ${result.oldCopyMessage}`)
      } else if (mode === 'removeOld') {
        if (savedPath) await uninstallPanel(savedPath)
        await updateSettings({ communityFolderPath: next })
        toast.success(
          next
            ? 'Panel removed from the old folder, and your Community folder was updated.'
            : 'Panel removed and your Community folder was cleared.',
        )
      } else {
        await updateSettings({ communityFolderPath: next })
        toast.success(next ? 'Community folder updated.' : 'Community folder cleared.')
      }

      setEdited(false)
      setPendingPath(undefined)
      reload()
    } catch (err) {
      const message = describeError(err, 'Could not change the Community folder.')
      setDialogError(message)
      toast.error(message)
    } finally {
      setBusy(null)
    }
  }

  // "Something of ours is in that folder" - true for a healthy install and for a damaged one alike,
  // and the thing every destructive/repair control below should key off. `status.installed` alone
  // now means "complete", so using it here would hide Repair and Remove behind the very damage they
  // exist to undo, and offer a fresh Install instead.
  const packagePresent = statusState === 'ready' && status !== null && isPackagePresent(status)
  const health = describeHealth(savedPath, statusState === 'ready' ? status : null, statusState)

  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <CardTitle className="flex items-center gap-2 text-base">
              <MonitorPlay className="size-4 text-accent" />
              MSFS in-game panel
            </CardTitle>
            <CardDescription className="mt-1.5">
              FSOps installs a small toolbar panel into your MSFS 2024 Community folder so you can see your live
              flight, ETA and airline cash without alt-tabbing. Entirely optional — removing it never affects your
              airline.
            </CardDescription>
          </div>
          {statusState === 'loading' ? (
            <Skeleton className="h-6 w-24 rounded-full" />
          ) : (
            <Badge variant={health.variant}>{health.label}</Badge>
          )}
        </div>
      </CardHeader>

      <CardContent className="space-y-6">
        <StatusPanel
          state={statusState}
          status={status}
          savedPath={savedPath}
          error={statusError}
          onRetry={reload}
        />

        <div className="space-y-1.5">
          <Label htmlFor="community-folder">Community folder</Label>
          <div className="flex flex-col gap-2 sm:flex-row">
            <Input
              id="community-folder"
              value={path}
              placeholder="C:\Users\you\AppData\Local\Packages\...\LocalCache\Packages\Community"
              onChange={(event) => {
                setEdited(true)
                setPath(event.target.value)
              }}
              aria-invalid={validation ? !validation.valid : undefined}
              className="font-mono text-xs"
            />
            <Button type="button" variant="outline" onClick={() => setBrowseOpen(true)} className="shrink-0">
              <FolderOpen /> Browse…
            </Button>
          </div>

          <PathHint trimmed={trimmed} unchanged={unchanged} validating={validating} validation={validation} />

          {!savedPath && (
            <DetectedFolders
              state={detectState}
              candidates={candidates}
              selected={trimmed}
              onSelect={(candidate) => {
                setEdited(true)
                setPath(candidate)
              }}
            />
          )}
        </div>

        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:flex-wrap sm:items-center sm:justify-end">
          {packagePresent && (
            <Button
              type="button"
              variant="ghost"
              className="text-danger hover:bg-danger/10 sm:mr-auto"
              onClick={() => setConfirmUninstall(true)}
              disabled={busy !== null}
            >
              <Trash2 /> Remove panel
            </Button>
          )}

          {packagePresent && (
            <Button type="button" variant="outline" onClick={() => void runInstall('repair')} disabled={busy !== null}>
              {busy === 'repair' ? <Loader2 className="animate-spin" /> : <RotateCcw />}
              {busy === 'repair' ? 'Repairing…' : 'Reinstall / repair'}
            </Button>
          )}

          {!packagePresent && savedPath && statusState === 'ready' && status?.success && (
            <Button type="button" variant="outline" onClick={() => void runInstall('install')} disabled={busy !== null}>
              {busy === 'install' ? <Loader2 className="animate-spin" /> : <MonitorPlay />}
              {busy === 'install' ? 'Installing…' : 'Install panel'}
            </Button>
          )}

          <Button
            type="button"
            onClick={requestFolderChange}
            disabled={busy !== null || unchanged || (validation !== null && !validation.valid)}
          >
            {busy === 'save' ? 'Saving…' : 'Save folder'}
          </Button>
        </div>
      </CardContent>

      <FolderChangeDialog
        open={pendingPath !== undefined}
        nextPath={lastPendingPath.current}
        oldPath={lastOldPath.current}
        busy={busy === 'save'}
        error={dialogError}
        onCancel={() => {
          setPendingPath(undefined)
          setDialogError(null)
        }}
        onChoose={(mode) => void commitFolderChange(pendingPath ?? null, mode)}
      />

      <Dialog open={confirmUninstall} onOpenChange={(open) => !busy && setConfirmUninstall(open)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <AlertTriangle className="size-5 text-danger" />
              Remove the panel from MSFS?
            </DialogTitle>
            <DialogDescription>
              This deletes the <code className="rounded bg-muted px-1 py-0.5 text-xs">fsops-panel</code> folder from
              your Community folder and nothing else — your other add-ons, and everything about your airline, are
              untouched. Close MSFS first if it&rsquo;s running. You can install it again from here at any time.
            </DialogDescription>
          </DialogHeader>
          {status?.installedPath && (
            <p className="break-all font-mono text-xs text-muted-foreground">{status.installedPath}</p>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setConfirmUninstall(false)} disabled={busy !== null}>
              Cancel
            </Button>
            <Button type="button" variant="destructive" onClick={() => void runUninstall()} disabled={busy !== null}>
              {busy === 'uninstall' ? 'Removing…' : 'Remove the panel'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={browseOpen} onOpenChange={setBrowseOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Browse for your Community folder</DialogTitle>
            <DialogDescription>
              A native folder picker isn&rsquo;t available yet. For now, open File Explorer, navigate to your MSFS
              Community folder, copy its address from the address bar, and paste it into the field above.
            </DialogDescription>
          </DialogHeader>
          <p className="text-sm text-muted-foreground">
            It&rsquo;s usually under something like{' '}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              …\Packages\Microsoft.FlightSimulator_...\LocalCache\Packages\Community
            </code>{' '}
            (Microsoft Store) or <code className="rounded bg-muted px-1 py-0.5 text-xs">…\Community</code> (Steam or
            other installs).
          </p>
          <DialogFooter>
            <Button type="button" onClick={() => setBrowseOpen(false)}>
              Got it
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}

// -------------------------------------------------------------------------------------------
// Status
// -------------------------------------------------------------------------------------------

function describeHealth(
  savedPath: string | null,
  status: PanelOperationResult | null,
  state: LoadState,
): { variant: BadgeProps['variant']; label: string } {
  if (state === 'error') return { variant: 'danger', label: 'Unknown' }
  if (!savedPath) return { variant: 'muted', label: 'Not set up' }
  if (!status) return { variant: 'muted', label: 'Unknown' }
  if (!status.success) return { variant: 'danger', label: 'Needs attention' }
  // Incomplete is checked before "not installed", because a package that is present but has lost
  // files is emphatically not the same as an empty folder - offering Install would be technically
  // curable but tells the player nothing about what actually went wrong.
  if (isIncomplete(status)) return { variant: 'warning', label: 'Needs repair' }
  if (!status.installed) return { variant: 'muted', label: 'Not installed' }
  if (hasPortDrift(status)) return { variant: 'warning', label: 'Needs repair' }
  if (status.installedVersion !== status.expectedVersion) return { variant: 'warning', label: 'Update available' }
  return { variant: 'success', label: 'Installed' }
}

/**
 * True when the installed panel is calling a port FSOps is no longer listening on. Nothing else
 * surfaces this: the files are all present and correct, so the panel looks perfectly installed
 * while showing an empty box in the sim. A null installedPort means the config couldn't be read,
 * which is not the same as a mismatch and must not be reported as one.
 */
function hasPortDrift(status: PanelOperationResult): boolean {
  return (
    // Deliberately "a package is there", not status.installed. Since `installed` came to mean
    // "complete", keying off it here let one fault mask another: a damaged install stopped
    // reporting its port drift and claimed the port "matches this copy of FSOps" instead, which
    // was worse than silence because it was a confident wrong answer about the wrong port.
    isPackagePresent(status) &&
    status.installedPort !== null &&
    status.expectedPort !== null &&
    status.installedPort !== status.expectedPort
  )
}

/**
 * True when an FSOps package is in the folder but has lost files it should have. This is the state
 * that used to be invisible: the manifest alone decided "installed", so deleting the panel's own
 * FSOpsPanel.js left a green "Installed - up to date" badge over a panel that renders nothing in
 * the sim, and nothing anywhere suggested pressing repair.
 */
function isIncomplete(status: PanelOperationResult): boolean {
  return status.success && status.missingFiles.length > 0
}

/** Something of ours is in that folder - healthy or damaged. The right question for anything that
 *  describes or repairs an existing install, as opposed to offering a fresh one. */
function isPackagePresent(status: PanelOperationResult): boolean {
  return status.installed || isIncomplete(status)
}

function StatusPanel({
  state,
  status,
  savedPath,
  error,
  onRetry,
}: {
  state: LoadState
  status: PanelOperationResult | null
  savedPath: string | null
  error: string | null
  onRetry: () => void
}) {
  if (state === 'loading') {
    return (
      <div className="space-y-2 rounded-lg border border-border bg-surface p-4">
        <Skeleton className="h-4 w-2/3" />
        <Skeleton className="h-4 w-1/2" />
        <Skeleton className="h-4 w-3/5" />
      </div>
    )
  }

  if (state === 'error') {
    return (
      <div className="flex flex-col gap-3 rounded-lg border border-danger/30 bg-danger/5 p-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-start gap-2 text-sm text-danger">
          <XCircle className="mt-0.5 size-4 shrink-0" />
          <span>{error ?? 'Could not read the panel status.'}</span>
        </div>
        <Button type="button" variant="outline" size="sm" onClick={onRetry} className="shrink-0">
          <RefreshCw /> Try again
        </Button>
      </div>
    )
  }

  if (!savedPath) {
    return (
      <div className="flex items-start gap-2 rounded-lg border border-border bg-surface p-4 text-sm text-muted-foreground">
        <MonitorPlay className="mt-0.5 size-4 shrink-0" />
        <span>
          No Community folder is set, so the panel isn&rsquo;t installed anywhere. Choose a folder below and save it,
          then install the panel — or leave this alone if you don&rsquo;t want an in-game panel at all.
        </span>
      </div>
    )
  }

  if (status && !status.success) {
    return (
      <div className="flex flex-col gap-3 rounded-lg border border-danger/30 bg-danger/5 p-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-start gap-2 text-sm text-danger">
          <AlertTriangle className="mt-0.5 size-4 shrink-0" />
          <span>{status.reason ?? status.message}</span>
        </div>
        <Button type="button" variant="outline" size="sm" onClick={onRetry} className="shrink-0">
          <RefreshCw /> Re-check
        </Button>
      </div>
    )
  }

  if (!status) return null

  const upToDate = status.installedVersion === status.expectedVersion
  const incomplete = isIncomplete(status)
  // The .spb being absent means two opposite things depending on why. If it is in the missing list
  // then this build shipped one and the player's copy lost it (repairable now); if it is simply not
  // there at all, the build genuinely has none and no amount of repairing will conjure it.
  const spbMissingFromCopy = status.missingFiles.some((f) => f.toLowerCase().endsWith('.spb'))
  // A damaged package is still a package: its version, port and toolbar rows are all still worth
  // showing, and hiding them would make a repairable install look like an empty folder.
  const present = isPackagePresent(status)

  return (
    <div className="rounded-lg border border-border bg-surface px-4 py-1">
      <StatusRow label="Panel files">
        {incomplete ? (
          <span className="flex items-start gap-1.5 text-warning">
            <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
            <span>
              {status.missingFiles.length === 1
                ? '1 file is missing'
                : `${status.missingFiles.length} files are missing`}{' '}
              — reinstall to put {status.missingFiles.length === 1 ? 'it' : 'them'} back.
              <span className="mt-1 block break-all font-mono text-xs text-muted-foreground">
                {status.missingFiles.join(', ')}
              </span>
            </span>
          </span>
        ) : status.installed ? (
          <span className="flex items-center gap-1.5 text-success">
            <CheckCircle2 className="size-3.5 shrink-0" /> Installed
          </span>
        ) : (
          <span className="text-muted-foreground">Not installed in this folder</span>
        )}
      </StatusRow>

      <StatusRow label="Location">
        <span className="break-all font-mono text-xs text-muted-foreground">
          {status.installedPath ?? savedPath}
        </span>
      </StatusRow>

      {present && (
        <StatusRow label="Version">
          {upToDate ? (
            <span className="text-muted-foreground">
              {status.installedVersion} — up to date
            </span>
          ) : (
            <span className="flex items-center gap-1.5 text-warning">
              <AlertTriangle className="size-3.5 shrink-0" />
              {status.installedVersion ?? 'unreadable'} installed, FSOps expects {status.expectedVersion} — reinstall to
              update
            </span>
          )}
        </StatusRow>
      )}

      {present && (
        <StatusRow label="Connects to">
          {hasPortDrift(status) ? (
            <span className="flex items-start gap-1.5 text-warning">
              <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
              <span>
                Port {status.installedPort}, but FSOps is on {status.expectedPort} — the panel will show nothing in
                the sim until you reinstall it.
              </span>
            </span>
          ) : (
            <span className="text-muted-foreground">
              Port {status.installedPort ?? status.expectedPort} — matches this copy of FSOps
            </span>
          )}
        </StatusRow>
      )}

      {present && (
        <StatusRow label="Toolbar button">
          {status.toolbarWillAppearInSim ? (
            <span className="flex items-center gap-1.5 text-success">
              <CheckCircle2 className="size-3.5 shrink-0" /> Appears in the MSFS toolbar
            </span>
          ) : spbMissingFromCopy ? (
            // Their copy lost it. Telling them to wait for a future update here would send them
            // away for something that will never arrive - the fix is one button away, right now.
            <span className="flex items-start gap-1.5 text-warning">
              <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
              <span>
                Not yet — the compiled panel component is missing from your copy, so no FSOps button appears in the
                sim. Reinstall to put it back.
              </span>
            </span>
          ) : (
            <span className="flex items-start gap-1.5 text-warning">
              <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
              <span>
                Not yet — this build of FSOps doesn&rsquo;t include the compiled panel component MSFS needs, so no
                FSOps button appears in the sim. The files are in place and a future update will finish it.
              </span>
            </span>
          )}
        </StatusRow>
      )}

      {!present && (
        <StatusRow label="What to do">
          <span className="text-muted-foreground">{status.message}</span>
        </StatusRow>
      )}
    </div>
  )
}

function StatusRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5 border-b border-border py-2.5 last:border-b-0 sm:flex-row sm:items-baseline sm:justify-between sm:gap-6">
      <span className="shrink-0 text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</span>
      <span className="min-w-0 text-sm sm:text-right">{children}</span>
    </div>
  )
}

// -------------------------------------------------------------------------------------------
// Folder input helpers
// -------------------------------------------------------------------------------------------

function PathHint({
  trimmed,
  unchanged,
  validating,
  validation,
}: {
  trimmed: string
  unchanged: boolean
  validating: boolean
  validation: PanelPathValidation | null
}) {
  if (!trimmed) {
    return (
      <p className="text-xs text-muted-foreground">
        Leave this blank if you don&rsquo;t want the in-game panel. A correct path ends in a folder named
        &ldquo;Community&rdquo;.
      </p>
    )
  }
  if (unchanged) {
    return <p className="text-xs text-muted-foreground">This is the folder FSOps is currently using.</p>
  }
  if (validating) {
    return (
      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Loader2 className="size-3 animate-spin" /> Checking…
      </p>
    )
  }
  if (!validation) return null
  if (validation.valid && !validation.exists) {
    return (
      <p className="flex items-start gap-1.5 text-xs text-warning">
        <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
        <span>
          The path looks right, but there&rsquo;s no folder there. You can still save it — the panel can&rsquo;t be
          installed until the folder exists.
        </span>
      </p>
    )
  }
  if (validation.valid) {
    return (
      <p className="flex items-center gap-1.5 text-xs text-success">
        <CheckCircle2 className="size-3.5 shrink-0" /> Looks like a Community folder.
      </p>
    )
  }
  return (
    <p className="flex items-start gap-1.5 text-xs text-danger">
      <XCircle className="mt-0.5 size-3.5 shrink-0" /> <span>{validation.reason}</span>
    </p>
  )
}

function DetectedFolders({
  state,
  candidates,
  selected,
  onSelect,
}: {
  state: LoadState
  candidates: PanelCandidate[]
  selected: string
  onSelect: (path: string) => void
}) {
  if (state === 'loading') {
    return (
      <p className="flex items-center gap-1.5 pt-2 text-xs text-muted-foreground">
        <Loader2 className="size-3 animate-spin" /> Looking for your MSFS install…
      </p>
    )
  }

  if (state === 'error') {
    return (
      <p className="pt-2 text-xs text-muted-foreground">
        Couldn&rsquo;t scan for MSFS automatically — paste the folder path above instead.
      </p>
    )
  }

  const found = candidates.filter((c) => c.exists)
  if (found.length === 0) {
    return (
      <p className="pt-2 text-xs text-muted-foreground">
        No MSFS 2024 install was found on this PC. Paste the folder path above if you know it.
      </p>
    )
  }

  return (
    <div className="space-y-2 pt-3">
      <p className="text-xs font-medium text-muted-foreground">Found on this PC</p>
      {found.map((candidate) => (
        <button
          key={candidate.path}
          type="button"
          aria-pressed={selected === candidate.path}
          onClick={() => onSelect(candidate.path)}
          className={cn(
            'flex w-full items-start gap-3 rounded-lg border p-3 text-left transition-colors',
            selected === candidate.path ? 'border-accent bg-accent/10' : 'border-border bg-surface hover:border-accent/40',
          )}
        >
          <FolderOpen className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
          <div className="min-w-0">
            <p className="break-all font-mono text-xs">{candidate.path}</p>
            <p className="mt-1 text-xs text-muted-foreground">{candidate.source}</p>
          </div>
          {selected === candidate.path && <CheckCircle2 className="ml-auto size-4 shrink-0 text-accent" />}
        </button>
      ))}
    </div>
  )
}

// -------------------------------------------------------------------------------------------
// Folder change
// -------------------------------------------------------------------------------------------

/**
 * Asked whenever the folder changes while a panel is installed in the old one. There is no correct
 * default here that the app can pick on the player's behalf: they may be pointing FSOps at a second
 * sim install (where leaving the first copy is right) or correcting a mistake (where it is litter).
 * So both answers are offered plainly, and neither is hidden behind the other.
 */
function FolderChangeDialog({
  open,
  nextPath,
  oldPath,
  busy,
  error,
  onCancel,
  onChoose,
}: {
  open: boolean
  nextPath: string | null
  oldPath: string | null
  busy: boolean
  error: string | null
  onCancel: () => void
  onChoose: (mode: FolderChangeMode) => void
}) {
  const clearing = nextPath === null

  return (
    <Dialog open={open} onOpenChange={(next) => !next && !busy && onCancel()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{clearing ? 'Clear your Community folder?' : 'Move the panel to the new folder?'}</DialogTitle>
          <DialogDescription>
            {clearing
              ? 'The FSOps panel is installed in your current Community folder. Clearing the setting on its own leaves those files behind in the sim.'
              : 'The FSOps panel is installed in your current Community folder. FSOps can install it in the new folder and clean up the old copy, or just change the setting and leave the old copy alone.'}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-2 text-xs">
          <div className="rounded-md border border-border bg-surface p-3">
            <p className="font-medium text-muted-foreground">Current folder</p>
            <p className="mt-1 break-all font-mono">{oldPath}</p>
          </div>
          {!clearing && (
            <div className="rounded-md border border-border bg-surface p-3">
              <p className="font-medium text-muted-foreground">New folder</p>
              <p className="mt-1 break-all font-mono">{nextPath}</p>
            </div>
          )}
        </div>

        {error && (
          <p className="flex items-start gap-1.5 text-xs text-danger">
            <XCircle className="mt-0.5 size-3.5 shrink-0" /> <span>{error}</span>
          </p>
        )}

        <DialogFooter className="sm:justify-between">
          <Button type="button" variant="ghost" onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
          <div className="flex flex-col gap-2 sm:flex-row">
            <Button type="button" variant="outline" onClick={() => onChoose('plain')} disabled={busy}>
              {clearing ? 'Just clear the setting' : 'Just change the folder'}
            </Button>
            <Button
              type="button"
              onClick={() => onChoose(clearing ? 'removeOld' : 'move')}
              disabled={busy}
            >
              {busy ? 'Working…' : clearing ? 'Remove the panel and clear' : 'Move the panel'}
            </Button>
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
