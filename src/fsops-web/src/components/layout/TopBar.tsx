import { PlaneTakeoff } from 'lucide-react'

import { HubStatusPill, SimStatusPill } from '@/components/layout/ConnectionPill'
import { ThemeToggle } from '@/components/layout/ThemeToggle'
import type { HubStatus } from '@/types/live'

interface TopBarProps {
  hubStatus: HubStatus
  simConnected: boolean
}

export function TopBar({ hubStatus, simConnected }: TopBarProps) {
  return (
    <header className="flex h-14 shrink-0 items-center justify-between border-b border-border bg-surface px-4">
      <div className="flex items-center gap-2 text-sm font-semibold">
        <PlaneTakeoff className="size-4 text-accent" />
        <span>FSOps</span>
      </div>

      <div className="flex items-center gap-3">
        {/* No airline exists yet — honest placeholder until airline creation wires up a real balance. */}
        <div
          className="hidden items-center gap-1 rounded-full border border-border bg-muted/50 px-3 py-1 text-xs font-medium tabular-nums text-muted-foreground sm:flex"
          title="No airline yet"
        >
          <span>Cash</span>
          <span>—</span>
        </div>
        <SimStatusPill simConnected={simConnected} />
        <HubStatusPill status={hubStatus} />
        <ThemeToggle />
      </div>
    </header>
  )
}
