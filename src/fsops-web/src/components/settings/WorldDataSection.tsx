import { useCallback, useEffect, useRef, useState } from 'react'
import { Globe2, Loader2, RefreshCw, XCircle } from 'lucide-react'
import { toast } from 'sonner'

import { Badge, type BadgeProps } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { ApiError, get, post } from '@/lib/api'
import type { WorldDataStatus } from '@/types/airport'

const POLL_INTERVAL_MS = 1500

type LoadState = 'loading' | 'ready' | 'error'

function isBusy(data: WorldDataStatus | null): boolean {
  return data !== null && (data.importInProgress || data.refreshInProgress === true)
}

/**
 * The airport and runway data every route, map and airport search is built on.
 *
 * <p>It ships inside the app rather than being downloaded, deliberately — route planning must
 * never depend on being online.
 * A newer data set arrives with an app update and is applied automatically the first time
 * FSOps starts afterwards; this card exists for the times someone wants it sooner, or suspects
 * their airport list is wrong.</p>
 *
 * <p>The card is deliberately explicit that a refresh never removes anything. Airports do
 * disappear from the upstream source, and an airport the player has flown to, built a route to, or
 * parked an aircraft at is kept regardless — so the honest thing is to say so where the button is,
 * not to leave someone wondering whether pressing it will cost them their history.</p>
 */
export function WorldDataSection() {
  const [state, setState] = useState<LoadState>('loading')
  const [data, setData] = useState<WorldDataStatus | null>(null)
  const [starting, setStarting] = useState(false)

  const timerRef = useRef<ReturnType<typeof setTimeout>>()
  const cancelledRef = useRef(false)

  const poll = useCallback(() => {
    get<WorldDataStatus>('/worlddata/status')
      .then((result) => {
        if (cancelledRef.current) return
        setData(result)
        setState('ready')
        // Only keep polling while something is actually running — a settings page that talks to
        // the server twice a second forever is a battery cost for no information.
        if (isBusy(result)) {
          timerRef.current = setTimeout(poll, POLL_INTERVAL_MS)
        }
      })
      .catch((err: unknown) => {
        if (cancelledRef.current) return
        if (!(err instanceof ApiError)) return
        setState('error')
      })
  }, [])

  useEffect(() => {
    cancelledRef.current = false
    poll()
    return () => {
      cancelledRef.current = true
      clearTimeout(timerRef.current)
    }
  }, [poll])

  async function startRefresh() {
    setStarting(true)
    try {
      const started = await post<WorldDataStatus>('/worlddata/refresh')
      setData(started)
      toast.success('Refreshing world data. Your routes, flights and fleet are untouched.')
      clearTimeout(timerRef.current)
      timerRef.current = setTimeout(poll, POLL_INTERVAL_MS)
    } catch (err) {
      toast.error(
        err instanceof ApiError ? err.message : 'Could not start the refresh. Try again in a moment.',
      )
    } finally {
      setStarting(false)
    }
  }

  const busy = isBusy(data)
  const health = describeHealth(state, data)

  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <CardTitle className="flex items-center gap-2 text-base">
              <Globe2 className="size-4 text-accent" />
              World data
            </CardTitle>
            <CardDescription className="mt-1.5">
              Every airport and runway FSOps knows about, from the public-domain OurAirports data
              set. It ships inside the app, so route planning works with no internet connection.
              A newer data set arrives with an FSOps update and is applied automatically the first
              time you start it afterwards.
            </CardDescription>
          </div>
          {state === 'loading' ? (
            <Skeleton className="h-6 w-24 rounded-full" />
          ) : (
            <Badge variant={health.variant}>{health.label}</Badge>
          )}
        </div>
      </CardHeader>

      <CardContent className="space-y-6">
        {state === 'loading' && (
          <div className="space-y-2 rounded-lg border border-border bg-surface p-4">
            <Skeleton className="h-4 w-2/3" />
            <Skeleton className="h-4 w-1/2" />
            <Skeleton className="h-4 w-3/5" />
          </div>
        )}

        {state === 'error' && (
          <div className="flex flex-col gap-3 rounded-lg border border-danger/30 bg-danger/5 p-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex items-start gap-2 text-sm text-danger">
              <XCircle className="mt-0.5 size-4 shrink-0" />
              <span>Could not read the world data status.</span>
            </div>
            <Button type="button" variant="outline" size="sm" onClick={poll} className="shrink-0">
              <RefreshCw /> Try again
            </Button>
          </div>
        )}

        {state === 'ready' && data && (
          <div className="rounded-lg border border-border bg-surface px-4 py-1">
            <StatusRow label="Airports">
              {data.airportCount > 0 ? (
                <span className="text-muted-foreground">{data.airportCount.toLocaleString()} loaded</span>
              ) : (
                <span className="text-warning">None yet</span>
              )}
            </StatusRow>
            <StatusRow label="Runways">
              <span className="text-muted-foreground">{data.runwayCount.toLocaleString()} loaded</span>
            </StatusRow>
            <StatusRow label="Data set">
              <span className="font-mono text-xs text-muted-foreground">{data.dataVersion ?? 'Unknown'}</span>
            </StatusRow>
            <StatusRow label="Last updated">
              <span className="text-muted-foreground">{formatApplied(data.lastAppliedUtc)}</span>
            </StatusRow>
          </div>
        )}

        {busy && data && (
          <div className="space-y-2 rounded-lg border border-accent/30 bg-accent/5 p-4">
            <p className="flex items-center gap-2 text-sm">
              <Loader2 className="size-4 shrink-0 animate-spin text-accent" />
              {data.importInProgress
                ? 'Importing world airport data…'
                : 'Refreshing world data — everything keeps working while this runs.'}
            </p>
            <div
              role="progressbar"
              aria-valuenow={Math.round(clamp(data.progressPercent))}
              aria-valuemin={0}
              aria-valuemax={100}
              aria-label="World data progress"
              className="h-1.5 w-full overflow-hidden rounded-full bg-muted"
            >
              <div
                className="h-full rounded-full bg-accent transition-all duration-500"
                style={{ width: `${clamp(data.progressPercent)}%` }}
              />
            </div>
            <p className="text-xs text-muted-foreground">{Math.round(clamp(data.progressPercent))}% complete</p>
          </div>
        )}

        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <p className="text-xs text-muted-foreground">
            A refresh only ever adds new airports and updates existing ones. Nothing is deleted — an
            airport that has been removed upstream is kept, so your routes, flight history and
            parked aircraft always still point at somewhere real. It runs in the background — carry
            on using FSOps while it does.
          </p>
          <Button
            type="button"
            variant="outline"
            className="shrink-0"
            onClick={() => void startRefresh()}
            disabled={starting || busy || state !== 'ready'}
          >
            {starting || busy ? <Loader2 className="animate-spin" /> : <RefreshCw />}
            {busy ? 'Refreshing…' : starting ? 'Starting…' : 'Refresh world data'}
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}

function clamp(percent: number): number {
  if (!Number.isFinite(percent)) return 0
  return Math.min(100, Math.max(0, percent))
}

function formatApplied(iso: string | null | undefined): string {
  if (!iso) return 'Unknown'
  const parsed = new Date(iso)
  if (Number.isNaN(parsed.getTime())) return 'Unknown'
  return parsed.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

function describeHealth(
  state: LoadState,
  data: WorldDataStatus | null,
): { variant: BadgeProps['variant']; label: string } {
  if (state === 'error') return { variant: 'danger', label: 'Unknown' }
  if (!data) return { variant: 'muted', label: 'Unknown' }
  if (data.importInProgress) return { variant: 'warning', label: 'Importing' }
  if (data.refreshInProgress) return { variant: 'warning', label: 'Refreshing' }
  if (!data.seeded || data.airportCount === 0) return { variant: 'danger', label: 'Not imported' }
  return { variant: 'success', label: 'Up to date' }
}

function StatusRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5 border-b border-border py-2.5 last:border-b-0 sm:flex-row sm:items-baseline sm:justify-between sm:gap-6">
      <span className="shrink-0 text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</span>
      <span className="min-w-0 text-sm sm:text-right">{children}</span>
    </div>
  )
}
