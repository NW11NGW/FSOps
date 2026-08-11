import { useEffect, useRef, useState } from 'react'
import { AlertTriangle, CheckCircle2, FolderOpen, Loader2, MonitorPlay, XCircle } from 'lucide-react'

import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

import {
  detectCommunityFolders,
  validateCommunityFolder,
  type PanelCandidate,
  type PanelPathValidation,
} from '../panelInstallApi'
import type { WizardData } from '../wizardData'

interface CommunityFolderStepProps {
  data: WizardData
  onChange: (patch: Partial<WizardData>) => void
}

type DetectStatus = 'loading' | 'ready' | 'error'

/**
 * "MSFS panel" onboarding step - docs/PLAN.md "The Community folder is captured at onboarding and
 * reused to install the panel". This step ONLY captures and validates the path; the actual install
 * (and saving it to UserSettings via PUT /settings) happens once in OnboardingWizard's submit
 * handler, after the airline itself is created - so a install hiccup here never blocks founding an
 * airline, and the path is never asked for twice.
 *
 * Genuinely skippable: leaving the path empty (the default) is a fully valid state - see
 * STEP_VALIDATORS.communityFolder in wizardData.ts - and is offered again later in Settings.
 */
export function CommunityFolderStep({ data, onChange }: CommunityFolderStepProps) {
  const [status, setStatus] = useState<DetectStatus>('loading')
  const [candidates, setCandidates] = useState<PanelCandidate[]>([])
  const [validation, setValidation] = useState<PanelPathValidation | null>(null)
  const [validating, setValidating] = useState(false)
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  useEffect(() => {
    let cancelled = false
    detectCommunityFolders()
      .then((result) => {
        if (cancelled) return
        setCandidates(result)
        setStatus('ready')
      })
      .catch(() => {
        if (cancelled) return
        setStatus('error')
      })
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    const path = data.communityFolderPath?.trim()
    if (debounceRef.current) clearTimeout(debounceRef.current)

    if (!path) {
      setValidation(null)
      setValidating(false)
      return
    }

    setValidating(true)
    debounceRef.current = setTimeout(() => {
      validateCommunityFolder(path)
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data.communityFolderPath])

  const foundCandidates = candidates.filter((c) => c.exists)
  const otherCandidates = candidates.filter((c) => !c.exists)

  return (
    <div>
      <h2 className="text-2xl font-semibold tracking-tight">Connect your MSFS panel.</h2>
      <p className="mt-2 text-sm text-muted-foreground">
        FSOps can add a small in-game toolbar panel to Microsoft Flight Simulator 2024 showing your live flight, ETA
        and airline cash without alt-tabbing out. It needs your MSFS <strong>Community</strong> folder — completely
        optional, and you can set it up any time later in Settings.
      </p>

      <div className="mt-8">
        {status === 'loading' && (
          <div className="flex items-center gap-2 rounded-lg border border-border bg-surface px-4 py-3 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin" />
            Looking for your MSFS install…
          </div>
        )}

        {status === 'error' && (
          <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 px-4 py-3 text-sm text-warning">
            <AlertTriangle className="mt-0.5 size-4 shrink-0" />
            <span>Couldn&rsquo;t scan for MSFS automatically. You can still paste the folder path below, or skip this and add it later.</span>
          </div>
        )}

        {status === 'ready' && foundCandidates.length > 0 && (
          <div className="space-y-2">
            <Label>We found this</Label>
            {foundCandidates.map((candidate) => (
              <CandidateCard
                key={candidate.path}
                candidate={candidate}
                selected={data.communityFolderPath === candidate.path}
                onSelect={() => onChange({ communityFolderPath: candidate.path })}
              />
            ))}
          </div>
        )}

        {status === 'ready' && foundCandidates.length === 0 && otherCandidates.length === 0 && (
          <div className="flex items-start gap-2 rounded-md border border-border bg-surface px-4 py-3 text-sm text-muted-foreground">
            <MonitorPlay className="mt-0.5 size-4 shrink-0" />
            <span>No MSFS 2024 install was found on this PC. That&rsquo;s fine — enter the path yourself below, or skip this for now.</span>
          </div>
        )}

        <div className="mt-6 space-y-1.5">
          <Label htmlFor="community-folder-path">
            {foundCandidates.length > 0 ? 'Or enter a different path' : 'Community folder path'}
          </Label>
          <div className="flex flex-col gap-2 sm:flex-row">
            <Input
              id="community-folder-path"
              value={data.communityFolderPath ?? ''}
              placeholder="C:\Users\you\AppData\Roaming\Microsoft Flight Simulator 2024\Packages\Community"
              onChange={(event) => onChange({ communityFolderPath: event.target.value || null })}
              className="font-mono text-xs"
            />
            {data.communityFolderPath && (
              <Button
                type="button"
                variant="ghost"
                className="shrink-0"
                onClick={() => onChange({ communityFolderPath: null })}
              >
                Skip this for now
              </Button>
            )}
          </div>

          <ValidationHint path={data.communityFolderPath} validating={validating} validation={validation} />
        </div>
      </div>
    </div>
  )
}

function CandidateCard({
  candidate,
  selected,
  onSelect,
}: {
  candidate: PanelCandidate
  selected: boolean
  onSelect: () => void
}) {
  return (
    <button
      type="button"
      aria-pressed={selected}
      onClick={onSelect}
      className={cn(
        'flex w-full items-start gap-3 rounded-lg border p-4 text-left transition-colors',
        selected ? 'border-accent bg-accent/10' : 'border-border bg-surface hover:border-accent/40',
      )}
    >
      <FolderOpen className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
      <div className="min-w-0">
        <p className="truncate font-mono text-xs">{candidate.path}</p>
        <p className="mt-1 text-xs text-muted-foreground">{candidate.source}</p>
      </div>
      {selected && <CheckCircle2 className="ml-auto size-4 shrink-0 text-accent" />}
    </button>
  )
}

function ValidationHint({
  path,
  validating,
  validation,
}: {
  path: string | null
  validating: boolean
  validation: PanelPathValidation | null
}) {
  if (!path) {
    return <p className="text-xs text-muted-foreground">Leave this blank to skip — you can add it later in Settings.</p>
  }
  if (validating) {
    return (
      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Loader2 className="size-3 animate-spin" /> Checking…
      </p>
    )
  }
  if (!validation) {
    return null
  }
  if (validation.valid && !validation.exists) {
    // Don't promise an install that will be refused: the installer won't create a Community folder
    // that isn't there, because a folder FSOps invented isn't one MSFS reads.
    return (
      <p className="flex items-start gap-1.5 text-xs text-warning">
        <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
        <span>That path looks right, but there&rsquo;s no folder there yet — double-check it, or set this up later in Settings.</span>
      </p>
    )
  }
  if (validation.valid) {
    return (
      <p className="flex items-center gap-1.5 text-xs text-success">
        <CheckCircle2 className="size-3.5" /> Looks like a Community folder. The panel will be installed here when you finish.
      </p>
    )
  }
  return (
    <p className="flex items-start gap-1.5 text-xs text-danger">
      <XCircle className="mt-0.5 size-3.5 shrink-0" /> <span>{validation.reason}</span>
    </p>
  )
}
