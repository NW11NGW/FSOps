import { Banknote, PlaneTakeoff } from 'lucide-react'

import { MaintenanceWarningLine } from '@/components/panel/MaintenanceWarningLine'
import { NextFlightsList } from '@/components/panel/NextFlightsList'
import { useSettings } from '@/hooks/useSettings'
import type { MaintenanceWarning } from '@/lib/panelFormat'
import type { FlightOption } from '@/types/flight'

interface PanelIdleProps {
  cashBalance: number | null
  nextFlights: FlightOption[]
  airlineIcaoCode: string | null
  maintenanceWarning: MaintenanceWarning | null
}

/** Nothing is in the air right now: just the numbers that still matter for a glance - cash on
 *  hand and what's ready to fly next. */
export function PanelIdle({ cashBalance, nextFlights, airlineIcaoCode, maintenanceWarning }: PanelIdleProps) {
  const { fmt } = useSettings()

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2.5 rounded-md border border-border bg-surface px-3 py-2.5 text-muted-foreground">
        <PlaneTakeoff className="size-4 shrink-0" />
        <p className="text-sm">No flight in progress.</p>
      </div>

      <div className="rounded-md border border-border bg-surface p-3">
        <p className="flex items-center gap-1.5 text-[10px] font-medium uppercase tracking-wide text-muted-foreground">
          <Banknote className="size-3.5" /> Cash
        </p>
        <p className="font-mono text-3xl font-bold tabular-nums tracking-tight">
          {cashBalance !== null ? fmt.money(cashBalance) : '—'}
        </p>
      </div>

      {maintenanceWarning && <MaintenanceWarningLine warning={maintenanceWarning} />}

      <div className="space-y-1.5 border-t border-border pt-3">
        <p className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Ready to fly</p>
        <NextFlightsList flights={nextFlights} airlineIcaoCode={airlineIcaoCode} />
      </div>
    </div>
  )
}
