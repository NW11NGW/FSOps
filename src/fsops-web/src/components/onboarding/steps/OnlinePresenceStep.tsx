import { PlaneTakeoff, RadioTower } from 'lucide-react'

import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useSettings } from '@/hooks/useSettings'

import type { WizardData } from '../wizardData'

interface OnlinePresenceStepProps {
  data: WizardData
  onChange: (patch: Partial<WizardData>) => void
}

const NUMERIC_PATTERN = /^\d+$/

/**
 * "Online flying" onboarding step: SimBrief Pilot ID and VATSIM CID, the same two fields as
 * SimBriefSection and VatsimSection in Settings (see those for the server-side story - both are
 * stored on UserSettings and read only by FSOps' own server). Both are genuinely optional; see
 * STEP_VALIDATORS.onlinePresence in wizardData.ts - this step can never block founding an airline.
 *
 * No network call happens here: this is local, offline-safe format hinting only ("looks like a
 * plain number" or not), never a check against SimBrief or VATSIM. An invalid-looking value never
 * blocks Next either - it just would not match anything later, same as leaving it blank.
 *
 * Existing values are shown locked, not re-prompted. The wizard only ever appears with no airline
 * on file, but UserSettings can already have values from a previous airline - founding a new one
 * after deleting the old one from Settings' danger zone is the one path that reaches this step
 * with settings.simBriefPilotId / settings.vatsimCid already set. Rendering those as editable
 * inputs pre-filled with the stored value would let an absent-minded clear-and-continue wipe out a
 * value the player never meant to touch, so a field that already has a value is shown read-only
 * here instead, with a pointer to Settings to actually change it.
 */
export function OnlinePresenceStep({ data, onChange }: OnlinePresenceStepProps) {
  const { settings } = useSettings()
  const simBriefLocked = settings.simBriefPilotId !== null
  const vatsimLocked = settings.vatsimCid !== null

  return (
    <div>
      <h2 className="text-2xl font-semibold tracking-tight">Flying online.</h2>
      <p className="mt-2 text-sm text-muted-foreground">
        Both fields below are entirely optional. Nothing is checked over the network here — set them now, or skip
        and add them anytime from Settings.
      </p>

      <div className="mt-8 space-y-8">
        <div className="space-y-1.5">
          <Label htmlFor="wizard-simbrief-pilot-id" className="flex items-center gap-1.5">
            <PlaneTakeoff className="size-3.5 text-accent" />
            SimBrief Pilot ID
          </Label>
          <p className="text-xs text-muted-foreground">
            Unlocks pulling your OFP's real fuel, route and block time into the Fly screen instead of FSOps' own
            estimate.
          </p>
          {simBriefLocked ? (
            <LockedValue value={settings.simBriefPilotId ?? ''} />
          ) : (
            <>
              <Input
                id="wizard-simbrief-pilot-id"
                value={data.simBriefPilotId ?? ''}
                placeholder="123456"
                inputMode="numeric"
                onChange={(event) => onChange({ simBriefPilotId: event.target.value || null })}
                className="font-mono text-sm sm:max-w-xs"
              />
              <FormatHint value={data.simBriefPilotId} example="123456" />
            </>
          )}
        </div>

        <div className="space-y-1.5">
          <Label htmlFor="wizard-vatsim-cid" className="flex items-center gap-1.5">
            <RadioTower className="size-3.5 text-accent" />
            VATSIM CID
          </Label>
          <p className="text-xs text-muted-foreground">
            Unlocks online-flight verification — a small bonus and a "flown online" badge when a tracked flight is
            corroborated on the network.
          </p>
          {vatsimLocked ? (
            <LockedValue value={settings.vatsimCid ?? ''} />
          ) : (
            <>
              <Input
                id="wizard-vatsim-cid"
                value={data.vatsimCid ?? ''}
                placeholder="1234567"
                inputMode="numeric"
                onChange={(event) => onChange({ vatsimCid: event.target.value || null })}
                className="font-mono text-sm sm:max-w-xs"
              />
              <FormatHint value={data.vatsimCid} example="1234567" />
            </>
          )}
        </div>
      </div>

      <p className="mt-8 text-xs text-muted-foreground">
        Leave either blank to skip — FSOps stays fully usable with neither set, and nothing here is ever sent
        anywhere except back to FSOps' own server.
      </p>
    </div>
  )
}

function LockedValue({ value }: { value: string }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-border bg-surface px-3 py-2 text-sm sm:max-w-xs">
      <span className="font-mono">{value}</span>
      <span className="text-xs text-muted-foreground">Already set — change it in Settings</span>
    </div>
  )
}

function FormatHint({ value, example }: { value: string | null; example: string }) {
  const trimmed = value?.trim() ?? ''
  if (trimmed === '') {
    return <p className="text-xs text-muted-foreground">Leave blank to skip.</p>
  }
  if (NUMERIC_PATTERN.test(trimmed)) {
    return null
  }
  return (
    <p className="text-xs text-warning">
      Numbers only, e.g. {example} — this won't stop you continuing, but a value in the wrong shape won't match
      anything either.
    </p>
  )
}
