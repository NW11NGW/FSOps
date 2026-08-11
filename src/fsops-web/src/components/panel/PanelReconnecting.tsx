import { WifiOff } from 'lucide-react'

/**
 * The server could not be reached on the panel's first load - most likely it's still starting up
 * or restarting. A plain, honest state rather than a blank white box; the panel keeps retrying on
 * its own (see Panel.tsx) and this clears itself once a response comes back.
 */
export function PanelReconnecting() {
  return (
    <div className="flex flex-col items-center justify-center gap-3 py-16 text-center">
      <div className="flex size-10 items-center justify-center rounded-full bg-muted text-muted-foreground">
        <WifiOff className="size-5" />
      </div>
      <div className="space-y-1">
        <p className="text-sm font-medium">Reconnecting…</p>
        <p className="text-xs text-muted-foreground">Waiting for FSOps to come back.</p>
      </div>
    </div>
  )
}
