import { useState } from 'react'
import { PlaneTakeoff } from 'lucide-react'
import { toast } from 'sonner'

import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useSettings } from '@/hooks/useSettings'
import { ApiError } from '@/lib/api'

/** SimBrief Pilot ID - server-side only (see SimBriefFlightPlanProvider). FSOps never sends this
 *  anywhere but SimBrief's own fetcher, and only from the server; the SPA never talks to
 *  simbrief.com directly. */
export function SimBriefSection() {
  const { settings, updateSettings } = useSettings()
  const [pilotId, setPilotId] = useState(settings.simBriefPilotId ?? '')
  const [saving, setSaving] = useState(false)

  const trimmed = pilotId.trim()
  const unchanged = trimmed === (settings.simBriefPilotId ?? '')

  async function save() {
    setSaving(true)
    try {
      await updateSettings({ simBriefPilotId: trimmed || null })
      toast.success(trimmed ? 'SimBrief Pilot ID saved.' : 'SimBrief Pilot ID cleared.')
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not save the SimBrief Pilot ID — reverted.')
      setPilotId(settings.simBriefPilotId ?? '')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <PlaneTakeoff className="size-4 text-accent" />
          SimBrief
        </CardTitle>
        <CardDescription>
          Set your Pilot ID and the Fly screen will pull your latest OFP's real fuel, cruise altitude, block time and
          filed route for a sector, instead of FSOps' own estimate — but only when that OFP is for the exact route
          you're about to fly.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <Label htmlFor="simbrief-pilot-id">Pilot ID</Label>
        <Input
          id="simbrief-pilot-id"
          value={pilotId}
          placeholder="123456"
          onChange={(event) => setPilotId(event.target.value)}
          className="font-mono text-sm sm:max-w-xs"
        />
        <p className="text-xs text-muted-foreground">
          Find yours on SimBrief under Account → Pilot ID. Leave blank to always use FSOps' built-in plan — SimBrief
          is entirely optional and nothing about your flight is sent anywhere without it.
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
