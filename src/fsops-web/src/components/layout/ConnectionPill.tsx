import { Radio, RadioTower, WifiOff, PlaneLanding } from 'lucide-react'

import { cn } from '@/lib/utils'
import type { HubStatus } from '@/types/live'

const HUB_COPY: Record<HubStatus, { label: string; dot: string; text: string }> = {
  connected: { label: 'Connected', dot: 'bg-success', text: 'text-success' },
  connecting: { label: 'Connecting', dot: 'bg-warning', text: 'text-warning' },
  reconnecting: { label: 'Reconnecting', dot: 'bg-warning', text: 'text-warning' },
  disconnected: { label: 'Disconnected', dot: 'bg-danger', text: 'text-danger' },
}

export function HubStatusPill({ status }: { status: HubStatus }) {
  const copy = HUB_COPY[status]
  const Icon = status === 'disconnected' ? WifiOff : status === 'connected' ? RadioTower : Radio

  return (
    <div
      className="flex items-center gap-2 rounded-full border border-border bg-muted/50 py-1 pl-2.5 pr-3 text-xs font-medium"
      title={`Live connection: ${copy.label}`}
    >
      <span className="relative flex size-2">
        <span
          className={cn(
            'absolute inline-flex size-full rounded-full opacity-75',
            copy.dot,
            (status === 'connecting' || status === 'reconnecting') && 'animate-pulse-ring',
          )}
        />
        <span className={cn('relative inline-flex size-2 rounded-full', copy.dot)} />
      </span>
      <Icon className={cn('size-3.5', copy.text)} />
      <span className={copy.text}>{copy.label}</span>
    </div>
  )
}

export function SimStatusPill({ simConnected }: { simConnected: boolean }) {
  return (
    <div
      className={cn(
        'flex items-center gap-2 rounded-full border py-1 pl-2.5 pr-3 text-xs font-medium',
        simConnected
          ? 'border-success/30 bg-success/10 text-success'
          : 'border-border bg-muted/50 text-muted-foreground',
      )}
      title={simConnected ? 'MSFS is connected' : 'MSFS is not connected yet'}
    >
      <PlaneLanding className="size-3.5" />
      <span>{simConnected ? 'Sim connected' : 'Sim offline'}</span>
    </div>
  )
}
