import { useState } from 'react'
import { AlertTriangle } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

interface NeedsResolutionPanelProps {
  onResume: () => void
  onCompleteWithEstimates: () => Promise<void>
  onAbandon: () => Promise<void>
}

/**
 * Shown when the tracker couldn't finish a flight automatically (typically: the app restarted
 * mid-flight and the simulator didn't reconnect close to the last known position within 30s).
 * There's no dedicated "resume" endpoint - Resume just re-checks GET /flights/active in case the
 * simulator reconnected close enough for the backend's own automatic resolution to have already
 * picked tracking back up.
 */
export function NeedsResolutionPanel({ onResume, onCompleteWithEstimates, onAbandon }: NeedsResolutionPanelProps) {
  const [busy, setBusy] = useState<'complete' | 'abandon' | null>(null)

  async function handleComplete() {
    setBusy('complete')
    try {
      await onCompleteWithEstimates()
    } finally {
      setBusy(null)
    }
  }

  async function handleAbandon() {
    setBusy('abandon')
    try {
      await onAbandon()
    } finally {
      setBusy(null)
    }
  }

  return (
    <Card className="border-warning/40">
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base text-warning">
          <AlertTriangle className="size-4" />
          This flight needs your attention
        </CardTitle>
        <p className="text-sm text-muted-foreground">
          Tracking couldn&rsquo;t pick back up automatically — usually because the app restarted mid-flight and the
          simulator didn&rsquo;t reconnect close to where it left off. Choose how to resolve it:
        </p>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="space-y-1 rounded-md border border-border p-3">
          <p className="text-sm font-medium">Resume tracking</p>
          <p className="text-xs text-muted-foreground">
            Checks whether the simulator has reconnected close enough for tracking to pick back up on its own.
          </p>
          <Button type="button" variant="outline" size="sm" onClick={onResume} disabled={busy !== null} className="mt-1">
            Check again
          </Button>
        </div>
        <div className="space-y-1 rounded-md border border-border p-3">
          <p className="text-sm font-medium">Complete with estimates</p>
          <p className="text-xs text-muted-foreground">
            Closes the flight out now using best-available estimates for whatever OOOI/fuel data the tracker never
            captured. No landing quality will be recorded.
          </p>
          <Button type="button" variant="outline" size="sm" onClick={handleComplete} disabled={busy !== null} className="mt-1">
            {busy === 'complete' ? 'Completing…' : 'Complete with estimates'}
          </Button>
        </div>
        <div className="space-y-1 rounded-md border border-border p-3">
          <p className="text-sm font-medium">Abandon</p>
          <p className="text-xs text-muted-foreground">
            Discards this flight entirely — no report card, no landing data. Your aircraft is freed up to fly again.
          </p>
          <Button type="button" variant="destructive" size="sm" onClick={handleAbandon} disabled={busy !== null} className="mt-1">
            {busy === 'abandon' ? 'Abandoning…' : 'Abandon flight'}
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}
