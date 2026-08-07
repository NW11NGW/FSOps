import { useState } from 'react'
import { Check } from 'lucide-react'

import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { ACCENT_PALETTE } from '@/lib/accentPalette'
import { isValidHexColour } from '@/lib/theme'
import { cn } from '@/lib/utils'

import { AIRCRAFT_OPTIONS, type WizardData } from '../wizardData'

interface AircraftStepProps {
  data: WizardData
  onChange: (patch: Partial<WizardData>) => void
  errorMessage: string | null
}

export function AircraftStep({ data, onChange, errorMessage }: AircraftStepProps) {
  const [customHex, setCustomHex] = useState(data.accentColour)
  const customInvalid = customHex.length > 0 && !isValidHexColour(customHex)

  return (
    <div>
      <h2 className="text-2xl font-semibold tracking-tight">Make it yours.</h2>
      <p className="mt-2 text-sm text-muted-foreground">
        Pick an accent colour for FSOps and the starter aircraft you&rsquo;ll fly first.
      </p>

      {errorMessage && (
        <div className="mt-6 rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          {errorMessage}
        </div>
      )}

      <div className="mt-8">
        <Label>Accent colour</Label>
        <div className="mt-3 flex flex-wrap gap-3">
          {ACCENT_PALETTE.map((swatch) => {
            const selected = data.accentColour.toLowerCase() === swatch.hex.toLowerCase()
            return (
              <button
                key={swatch.hex}
                type="button"
                title={swatch.name}
                aria-label={swatch.name}
                aria-pressed={selected}
                onClick={() => {
                  onChange({ accentColour: swatch.hex })
                  setCustomHex(swatch.hex)
                }}
                className={cn(
                  'flex size-10 items-center justify-center rounded-full border-2 transition-transform',
                  selected ? 'scale-110 border-foreground' : 'border-transparent hover:scale-105',
                )}
                style={{ backgroundColor: swatch.hex }}
              >
                {selected && <Check className="size-4 text-white drop-shadow" />}
              </button>
            )
          })}
        </div>

        <div className="mt-4 flex items-center gap-3">
          <Label htmlFor="custom-accent" className="text-xs text-muted-foreground">
            Custom hex
          </Label>
          <div className="flex items-center gap-2">
            <span
              className="size-8 shrink-0 rounded-md border border-border"
              style={{ backgroundColor: isValidHexColour(customHex) ? customHex : 'transparent' }}
            />
            <Input
              id="custom-accent"
              value={customHex}
              placeholder="#0EA5E9"
              maxLength={7}
              className="w-32 font-mono"
              aria-invalid={customInvalid}
              onChange={(event) => {
                const value = event.target.value
                setCustomHex(value)
                if (isValidHexColour(value)) onChange({ accentColour: value })
              }}
            />
          </div>
        </div>
        {customInvalid && <p className="mt-1 text-xs text-danger">Use a 6-digit hex colour, e.g. #0EA5E9.</p>}
      </div>

      <div className="mt-10">
        <Label>Starter aircraft</Label>
        <div className="mt-3 grid gap-4 sm:grid-cols-2">
          {AIRCRAFT_OPTIONS.map((aircraft) => {
            const selected = data.starterAircraftFamily === aircraft.id
            return (
              <button
                key={aircraft.id}
                type="button"
                aria-pressed={selected}
                onClick={() => onChange({ starterAircraftFamily: aircraft.id })}
                className={cn(
                  'flex flex-col gap-3 rounded-lg border p-5 text-left transition-colors',
                  selected ? 'border-accent bg-accent/10' : 'border-border bg-surface hover:border-accent/40',
                )}
              >
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-base font-semibold tracking-tight">{aircraft.label}</p>
                    <p className="text-xs text-muted-foreground">{aircraft.manufacturer}</p>
                  </div>
                  {selected && (
                    <span className="flex size-5 items-center justify-center rounded-full bg-accent text-accent-foreground">
                      <Check className="size-3" />
                    </span>
                  )}
                </div>
                <dl className="grid grid-cols-3 gap-2 text-xs">
                  <div>
                    <dt className="text-muted-foreground">Pax</dt>
                    <dd className="font-medium tabular-nums">{aircraft.pax}</dd>
                  </div>
                  <div>
                    <dt className="text-muted-foreground">Range</dt>
                    <dd className="font-medium tabular-nums">{aircraft.range}</dd>
                  </div>
                  <div>
                    <dt className="text-muted-foreground">Cruise</dt>
                    <dd className="font-medium tabular-nums">{aircraft.cruise}</dd>
                  </div>
                </dl>
              </button>
            )
          })}
        </div>
      </div>
    </div>
  )
}
