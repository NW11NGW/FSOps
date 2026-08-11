import { AlertTriangle } from 'lucide-react'

import type { MaintenanceWarning } from '@/lib/panelFormat'

interface MaintenanceWarningLineProps {
  warning: MaintenanceWarning
}

/** The one maintenance fact worth a glance on the panel - see selectMaintenanceWarning. */
export function MaintenanceWarningLine({ warning }: MaintenanceWarningLineProps) {
  const checkLabel = warning.checkType === 'A' ? 'A-check' : 'C-check'
  return (
    <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 px-3 py-2 text-xs text-warning">
      <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
      <span>
        <span className="font-medium">{warning.aircraft.registration}</span> is {Math.round(warning.hoursRemaining)}h from
        its next {checkLabel}.
      </span>
    </div>
  )
}
