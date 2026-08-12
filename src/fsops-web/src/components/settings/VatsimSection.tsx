import { useState } from 'react'
import { RadioTower } from 'lucide-react'
import { toast } from 'sonner'

import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useSettings } from '@/hooks/useSettings'
import { ApiError } from '@/lib/api'

/** VATSIM CID - server-side only (see VatsimFlightCorroborationService). Entirely opt-in: with
 *  this blank, the ATC layer and other-traffic layer still work exactly as before, and no
 *  online-flying detection, history, or bonus is ever attempted. FSOps never sends this anywhere
 *  but its own API, and only reads VATSIM's own public feed with it - never the other way round. */
export function VatsimSection() {
  const { settings, updateSettings } = useSettings()
  const [cid, setCid] = useState(settings.vatsimCid ?? '')
  const [saving, setSaving] = useState(false)

  const trimmed = cid.trim()
  const unchanged = trimmed === (settings.vatsimCid ?? '')

  async function save() {
    setSaving(true)
    try {
      await updateSettings({ vatsimCid: trimmed || null })
      toast.success(trimmed ? 'VATSIM CID saved.' : 'VATSIM CID cleared.')
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not save the VATSIM CID — reverted.')
      setCid(settings.vatsimCid ?? '')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <RadioTower className="size-4 text-accent" />
          VATSIM
        </CardTitle>
        <CardDescription>
          Set your VATSIM CID and FSOps will corroborate a tracked flight against the public network while you fly —
          callsign, position and timing checked against FSOps' own telemetry, never a replacement for it. A
          corroborated flight earns a small online-flying bonus and shows a "flown online" badge on its report card.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <Label htmlFor="vatsim-cid">VATSIM CID</Label>
        <Input
          id="vatsim-cid"
          value={cid}
          placeholder="1234567"
          inputMode="numeric"
          onChange={(event) => setCid(event.target.value)}
          className="font-mono text-sm sm:max-w-xs"
        />
        <p className="text-xs text-muted-foreground">
          Find yours on your VATSIM member profile. Leave blank to turn this off entirely — the ATC layer and other
          traffic on the map keep working with no CID at all, and nothing about you is ever sent to VATSIM either
          way; FSOps only reads its public feed.
        </p>
        <div className="flex justify-end">
          <Button type="button" onClick={() => void save()} disabled={saving || unchanged}>
            {saving ? 'Saving…' : 'Save'}
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}
