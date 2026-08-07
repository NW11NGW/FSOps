import { PlaneTakeoff } from 'lucide-react'

import { HubStatusPill, SimStatusPill } from '@/components/layout/ConnectionPill'
import { ThemeToggle } from '@/components/layout/ThemeToggle'
import { Skeleton } from '@/components/ui/skeleton'
import type { AirlineSummaryState } from '@/hooks/useAirlineSummary'
import { useSettings } from '@/hooks/useSettings'
import type { HubStatus } from '@/types/live'

interface TopBarProps {
  hubStatus: HubStatus
  simConnected: boolean
  airlineSummary: AirlineSummaryState
}

export function TopBar({ hubStatus, simConnected, airlineSummary }: TopBarProps) {
  const { fmt } = useSettings()
  const { status, data } = airlineSummary

  return (
    <header className="flex h-14 shrink-0 items-center justify-between border-b border-border bg-surface px-4">
      <div className="flex items-center gap-2 text-sm font-semibold">
        <PlaneTakeoff className="size-4 text-accent" />
        <span className="truncate">{data?.airline.name ?? 'FSOps'}</span>
      </div>

      <div className="flex items-center gap-3">
        <div
          className="hidden items-center gap-1.5 rounded-full border border-border bg-muted/50 px-3 py-1 text-xs font-medium tabular-nums text-muted-foreground sm:flex"
          title={data ? 'Current cash balance' : 'Cash balance unavailable'}
        >
          <span>Cash</span>
          {status === 'loading' ? (
            <Skeleton className="h-3 w-14" />
          ) : data ? (
            <span className="text-foreground">{fmt.money(data.cashBalance)}</span>
          ) : (
            <span>—</span>
          )}
        </div>
        <SimStatusPill simConnected={simConnected} />
        <HubStatusPill status={hubStatus} />
        <ThemeToggle />
      </div>
    </header>
  )
}
