import { useEffect, useMemo, useState } from 'react'
import { AlertTriangle, Check, FolderSearch, Info, Plane, RefreshCw, XCircle } from 'lucide-react'
import { toast } from 'sonner'

import { ToggleGroup } from '@/components/shared/ToggleGroup'
import { Badge, type BadgeProps } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { useSimAircraft } from '@/hooks/useSimAircraft'
import { ApiError } from '@/lib/api'
import { cn } from '@/lib/utils'
import type {
  SimAircraftCategory,
  SimAircraftEntry,
  SimAircraftEvidence,
  SimAircraftScan,
  SimEdition,
} from '@/types/simAircraft'

const EDITIONS: { value: SimEdition; label: string }[] = [
  { value: 'Standard', label: 'Standard' },
  { value: 'Deluxe', label: 'Deluxe' },
  { value: 'PremiumDeluxe', label: 'Premium Deluxe' },
]

const CATEGORY_ORDER: SimAircraftCategory[] = [
  'LightSingle',
  'LightTwin',
  'UtilityTurboprop',
  'BusinessJet',
  'RegionalAirliner',
  'Narrowbody',
  'Widebody',
]

const CATEGORY_LABELS: Record<SimAircraftCategory, string> = {
  LightSingle: 'Light singles',
  LightTwin: 'Light twins',
  UtilityTurboprop: 'Turboprops',
  BusinessJet: 'Business jets',
  RegionalAirliner: 'Regional airliners',
  Narrowbody: 'Narrowbodies',
  Widebody: 'Widebodies',
}

/**
 * Which aircraft the player can actually load in MSFS.
 *
 * <p>Three things here are deliberate rather than incidental:</p>
 * <ul>
 *   <li><b>Standard is the default, and the default is the small answer.</b> Somebody who never
 *   opens this screen gets the smallest aircraft set. Guessing low costs them a tick box; guessing
 *   high would offer them a job in an aircraft they cannot load.</li>
 *   <li><b>A scan is shown as evidence, not as a verdict.</b> MSFS 2024 streams most of its base
 *   content, so a scan can prove an aircraft is there and can never prove one is missing. The copy
 *   says so where the button is, because somebody whose scan found three things needs to know that
 *   is not the whole list.</li>
 *   <li><b>Every row can be overruled.</b> The player is the only party here who actually knows
 *   what is in their simulator, so their tick beats the scan and the edition both.</li>
 * </ul>
 */
export function SimAircraftSection() {
  const { status, state, busy, refetch, scan, setEdition, setCommunityFolder, setAvailable } = useSimAircraft()
  const [folderDraft, setFolderDraft] = useState('')

  const configured = state?.configuredCommunityFolderPath ?? ''
  useEffect(() => setFolderDraft(configured), [configured])

  const grouped = useMemo(() => {
    const byCategory = new Map<SimAircraftCategory, SimAircraftEntry[]>()
    for (const entry of state?.aircraft ?? []) {
      const bucket = byCategory.get(entry.category)
      if (bucket) bucket.push(entry)
      else byCategory.set(entry.category, [entry])
    }
    return CATEGORY_ORDER.map((category) => ({ category, entries: byCategory.get(category) ?? [] })).filter(
      (group) => group.entries.length > 0,
    )
  }, [state?.aircraft])

  const availableCount = state?.aircraft.filter((a) => a.available).length ?? 0

  async function guard(work: () => Promise<void>, failure: string) {
    try {
      await work()
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : failure)
    }
  }

  if (status === 'loading' && !state) {
    return (
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <Plane className="size-4 text-accent" />
            Aircraft in your simulator
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <Skeleton className="h-5 w-48" />
          <Skeleton className="h-4 w-72" />
          <Skeleton className="h-4 w-64" />
        </CardContent>
      </Card>
    )
  }

  if (status === 'error' || !state) {
    return (
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <Plane className="size-4 text-accent" />
            Aircraft in your simulator
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div className="flex flex-col gap-3 rounded-lg border border-danger/30 bg-danger/5 p-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex items-start gap-2 text-sm text-danger">
              <XCircle className="mt-0.5 size-4 shrink-0" />
              <span>Could not read which aircraft you have.</span>
            </div>
            <Button type="button" variant="outline" size="sm" onClick={refetch} className="shrink-0">
              <RefreshCw /> Try again
            </Button>
          </div>
        </CardContent>
      </Card>
    )
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <CardTitle className="flex items-center gap-2 text-base">
              <Plane className="size-4 text-accent" />
              Aircraft in your simulator
            </CardTitle>
            <CardDescription className="mt-1.5">
              FSOps needs to know which aircraft you can actually load, so it never offers you a job in something you
              do not have. Tell it which edition of MSFS 2024 you bought, let it look in your Community folder, and
              correct anything it gets wrong — your answer always wins.
            </CardDescription>
          </div>
          <Badge variant={availableCount > 0 ? 'success' : 'warning'}>
            {availableCount} aircraft available
          </Badge>
        </div>
      </CardHeader>

      <CardContent className="space-y-6">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border pb-5">
          <div>
            <p className="text-sm font-medium">Your MSFS 2024 edition</p>
            <p className="text-xs text-muted-foreground">
              The editions build on each other — Deluxe adds aircraft to Standard, Premium Deluxe adds more again.
              If you are not sure, leave it on Standard and tick anything extra below.
            </p>
          </div>
          <ToggleGroup
            ariaLabel="MSFS 2024 edition"
            value={state.edition}
            onChange={(value) => void guard(() => setEdition(value), 'Could not save your edition. Try again.')}
            options={EDITIONS}
          />
        </div>

        <div className="space-y-3 border-b border-border pb-5">
          <div>
            <p className="text-sm font-medium">Your Community folder</p>
            <p className="text-xs text-muted-foreground">
              Where your add-on aircraft live. FSOps finds this by asking the simulator where it keeps its packages,
              so you only need to fill it in if you have moved things around. FSOps only ever reads this folder.
            </p>
          </div>

          <Label htmlFor="community-folder">Folder path</Label>
          <Input
            id="community-folder"
            value={folderDraft}
            placeholder={state.effectiveCommunityFolderPath ?? 'FSOps could not find your Community folder'}
            onChange={(event) => setFolderDraft(event.target.value)}
            className="font-mono text-xs"
          />

          {!state.configuredCommunityFolderPath && state.effectiveCommunityFolderPath && (
            <p className="text-xs text-muted-foreground">
              Found automatically:{' '}
              <span className="break-all font-mono text-foreground">{state.effectiveCommunityFolderPath}</span>
            </p>
          )}

          <div className="flex flex-wrap items-center gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={busy || folderDraft.trim() === configured}
              onClick={() =>
                void guard(async () => {
                  await setCommunityFolder(folderDraft.trim() || null)
                  toast.success(folderDraft.trim() ? 'Community folder saved.' : 'Back to finding it automatically.')
                }, 'Could not save that folder. Try again.')
              }
            >
              Save folder
            </Button>
            {state.configuredCommunityFolderPath && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled={busy}
                onClick={() =>
                  void guard(async () => {
                    await setCommunityFolder(null)
                    toast.success('Back to finding it automatically.')
                  }, 'Could not clear that folder. Try again.')
                }
              >
                Find it for me
              </Button>
            )}
            <Button
              type="button"
              size="sm"
              disabled={busy}
              onClick={() => void guard(() => scan(), 'Could not scan. Try again in a moment.')}
            >
              <FolderSearch className={cn('mr-2 size-4', busy && 'animate-pulse')} />
              {busy ? 'Scanning…' : 'Scan for aircraft'}
            </Button>
          </div>

          <ScanReport scan={state.lastScan} />
        </div>

        <div className="space-y-4">
          <div>
            <p className="text-sm font-medium">What you can fly</p>
            <p className="text-xs text-muted-foreground">
              Tick anything you have that FSOps missed, and untick anything you do not. A scan can prove an aircraft
              is installed; it can never prove one is not, because MSFS streams most of its aircraft and only keeps
              on disk what you have flown.
            </p>
          </div>

          {grouped.map((group) => (
            <div key={group.category} className="space-y-1.5">
              <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                {CATEGORY_LABELS[group.category]}
              </p>
              <div className="overflow-hidden rounded-lg border border-border bg-surface">
                {group.entries.map((entry) => (
                  <AircraftRow
                    key={entry.typeDesignator}
                    entry={entry}
                    busy={busy}
                    onToggle={(next) =>
                      void guard(
                        () => setAvailable(entry.typeDesignator, next),
                        `Could not change the ${entry.name}. Try again.`,
                      )
                    }
                  />
                ))}
              </div>
            </div>
          ))}
        </div>
      </CardContent>
    </Card>
  )
}

function AircraftRow({
  entry,
  busy,
  onToggle,
}: {
  entry: SimAircraftEntry
  busy: boolean
  onToggle: (available: boolean | null) => void
}) {
  const ticked = entry.evidence === 'TickedOn' || entry.evidence === 'TickedOff'
  const evidence = describeEvidence(entry.evidence)

  return (
    <div className="flex flex-wrap items-center gap-3 border-b border-border px-3 py-2.5 last:border-b-0">
      <button
        type="button"
        role="checkbox"
        aria-checked={entry.available}
        aria-label={entry.name}
        disabled={busy}
        // Clicking a row FSOps already agrees with clears the tick rather than adding a redundant
        // one, so an override only ever exists where the player and FSOps actually disagree.
        onClick={() => onToggle(ticked ? null : !entry.available)}
        className={cn(
          'flex size-5 shrink-0 items-center justify-center rounded border transition-colors',
          entry.available
            ? 'border-accent bg-accent text-accent-foreground'
            : 'border-border bg-muted text-transparent hover:border-accent/60',
          busy && 'cursor-not-allowed opacity-60',
        )}
      >
        <Check className="size-3.5" aria-hidden />
      </button>

      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-medium">{entry.name}</p>
        <p className="truncate text-xs text-muted-foreground">
          <span className="font-mono">{entry.typeDesignator}</span>
          {entry.seats > 0 ? ` · ${entry.seats} ${entry.seats === 1 ? 'seat' : 'seats'}` : ' · freight only'}
          {` · ${entry.rangeNm.toLocaleString()} nm`}
        </p>
      </div>

      <Badge variant={evidence.variant}>{evidence.label}</Badge>
    </div>
  )
}

function ScanReport({ scan }: { scan: SimAircraftScan | null }) {
  if (!scan) {
    return (
      <p className="flex items-start gap-2 rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
        <Info className="mt-0.5 size-4 shrink-0" aria-hidden />
        <span>
          Nothing scanned yet. Until you do, FSOps goes by your edition alone — which is a fair guess for base
          aircraft and knows nothing about your add-ons.
        </span>
      </p>
    )
  }

  if (scan.outcome !== 'Scanned') {
    return (
      <div className="flex gap-2 rounded-md border border-warning/40 bg-warning/5 px-3 py-2 text-xs">
        <AlertTriangle className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden />
        <div className="space-y-1 text-muted-foreground">
          <p className="font-medium text-foreground">{SCAN_FAILURE_HEADLINE[scan.outcome]}</p>
          <p>{SCAN_FAILURE_DETAIL[scan.outcome]}</p>
          <p>Nothing has been taken away — everything your edition includes is still available below.</p>
        </div>
      </div>
    )
  }

  const identified = scan.aircraftPackages.filter((p) => p.typeDesignator !== null)
  const unidentified = scan.aircraftPackages.filter((p) => p.typeDesignator === null)

  return (
    <div className="space-y-2 rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
      <p>
        Looked at <span className="text-foreground">{scan.packagesInspected.toLocaleString()}</span> packages on{' '}
        {new Date(scan.scannedUtc).toLocaleString()}. Found{' '}
        <span className="text-foreground">{identified.length}</span> add-on aircraft
        {scan.basePackageTypeDesignators.length > 0 && (
          <>
            {' '}
            and <span className="text-foreground">{scan.basePackageTypeDesignators.length}</span> built-in ones on
            disk
          </>
        )}
        . Tick anything else you have.
      </p>

      {identified.length > 0 && (
        <ul className="space-y-0.5">
          {identified.map((pkg) => (
            <li key={pkg.packageFolder} className="truncate">
              <span className="font-mono text-foreground">{pkg.typeDesignator}</span> — {pkg.packageTitle}
            </li>
          ))}
        </ul>
      )}

      {unidentified.length > 0 && (
        <p>
          {unidentified.length} aircraft {unidentified.length === 1 ? 'package was' : 'packages were'} not
          recognised
          {unidentified.some((p) => p.rawDesignator) && (
            <>
              {' '}
              ({unidentified
                .filter((p) => p.rawDesignator)
                .map((p) => p.rawDesignator)
                .join(', ')})
            </>
          )}
          . That is normal for liveries, instrument add-ons and aircraft FSOps does not know yet.
        </p>
      )}
    </div>
  )
}

const SCAN_FAILURE_HEADLINE: Record<Exclude<SimAircraftScan['outcome'], 'Scanned'>, string> = {
  NoFolder: 'FSOps could not find your Community folder.',
  FolderMissing: 'That folder is not there any more.',
  NotAPackagesFolder: 'That folder does not look like a Community folder.',
}

const SCAN_FAILURE_DETAIL: Record<Exclude<SimAircraftScan['outcome'], 'Scanned'>, string> = {
  NoFolder:
    'That usually just means MSFS is not installed on this machine. If it is, put the path to your Community folder in the box above.',
  FolderMissing:
    'A moved simulator or an unplugged drive will do this. Check the path above, or clear it and let FSOps look again.',
  NotAPackagesFolder:
    'There are no add-on packages inside it. The folder you want is called Community, and sits inside the simulator’s Packages folder.',
}

function describeEvidence(evidence: SimAircraftEvidence): { variant: BadgeProps['variant']; label: string } {
  switch (evidence) {
    case 'CommunityFolder':
      return { variant: 'success', label: 'Found in Community' }
    case 'InstalledOnDisk':
      return { variant: 'success', label: 'Found on disk' }
    case 'Edition':
      return { variant: 'secondary', label: 'In your edition' }
    case 'TickedOn':
      return { variant: 'outline', label: 'You added it' }
    case 'TickedOff':
      return { variant: 'outline', label: 'You removed it' }
    default:
      return { variant: 'muted', label: 'Not yours' }
  }
}
