import { AlertTriangle } from 'lucide-react'

import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

import type { WizardData } from '../wizardData'

interface IdentityStepProps {
  data: WizardData
  onChange: (patch: Partial<WizardData>) => void
  errorMessage: string | null
}

export function IdentityStep({ data, onChange, errorMessage }: IdentityStepProps) {
  const { name, icaoCode } = data
  const nameError = name.length > 0 && (name.trim().length < 2 || name.trim().length > 40)
  const icaoError = icaoCode.length > 0 && !/^[A-Z]{2,3}$/.test(icaoCode)

  return (
    <div>
      <h2 className="text-2xl font-semibold tracking-tight">What&rsquo;s your airline called?</h2>
      <p className="mt-2 text-sm text-muted-foreground">
        Pick a name and an ICAO airline code — both appear throughout FSOps and on your flights.
      </p>

      {errorMessage && (
        <div className="mt-6 flex items-start gap-2 rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          <AlertTriangle className="mt-0.5 size-4 shrink-0" />
          <span>{errorMessage}</span>
        </div>
      )}

      <div className="mt-8 space-y-6">
        <div className="space-y-2">
          <Label htmlFor="airline-name">Airline name</Label>
          <Input
            id="airline-name"
            value={name}
            maxLength={40}
            autoFocus
            placeholder="e.g. Meridian Airways"
            onChange={(event) => onChange({ name: event.target.value })}
            aria-invalid={nameError}
            aria-describedby="airline-name-hint"
          />
          <p id="airline-name-hint" className={nameError ? 'text-xs text-danger' : 'text-xs text-muted-foreground'}>
            {nameError ? '2–40 characters.' : `${name.trim().length}/40 characters`}
          </p>
        </div>

        <div className="space-y-2">
          <Label htmlFor="airline-icao">ICAO airline code</Label>
          <Input
            id="airline-icao"
            value={icaoCode}
            maxLength={3}
            placeholder="e.g. MER"
            onChange={(event) => onChange({ icaoCode: event.target.value.toUpperCase().replace(/[^A-Z]/g, '') })}
            aria-invalid={icaoError}
            aria-describedby="airline-icao-hint"
            className="w-32 font-mono uppercase tracking-widest"
          />
          <p id="airline-icao-hint" className={icaoError ? 'text-xs text-danger' : 'text-xs text-muted-foreground'}>
            {icaoError ? '2–3 letters, A–Z only.' : '2–3 letters, used on flight numbers and callsigns.'}
          </p>
        </div>
      </div>

      {(name.trim() || icaoCode) && (
        <div className="mt-8 rounded-lg border border-border bg-muted/40 p-4">
          <p className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">Preview</p>
          <p className="text-lg font-semibold tracking-tight">
            {name.trim() || 'Your airline'}{' '}
            {icaoCode && <span className="font-mono text-accent">({icaoCode})</span>}
          </p>
        </div>
      )}
    </div>
  )
}
