import { AlertTriangle, CheckCircle2, MapPin } from 'lucide-react'

import { AirportSearch } from '@/components/shared/AirportSearch'
import { Badge } from '@/components/ui/badge'
import type { AirportSummary } from '@/types/airport'

import type { WizardData } from '../wizardData'

interface HomeBaseStepProps {
  data: WizardData
  onChange: (patch: Partial<WizardData>) => void
  errorMessage: string | null
}

const SHORT_RUNWAY_FT = 6000

function evaluateWarnings(airport: AirportSummary): string[] {
  const warnings: string[] = []
  if (!airport.hasScheduledService) {
    warnings.push('This airport has no scheduled commercial service today — passenger demand here may be thin.')
  }
  if (airport.longestRunwayFt === null || airport.longestRunwayFt < SHORT_RUNWAY_FT) {
    warnings.push(
      `Longest runway is ${airport.longestRunwayFt !== null ? `${airport.longestRunwayFt.toLocaleString()} ft` : 'unknown'} — short for larger airliners.`,
    )
  }
  if (airport.sizeCategory === 'Small' || airport.sizeCategory === 'Heliport' || airport.sizeCategory === 'Seaplane') {
    warnings.push(`Classified as a ${airport.sizeCategory.toLowerCase()} field — an unusual choice for an airline hub.`)
  }
  return warnings
}

export function HomeBaseStep({ data, onChange, errorMessage }: HomeBaseStepProps) {
  const airport = data.homeAirport
  const warnings = airport ? evaluateWarnings(airport) : []

  return (
    <div>
      <h2 className="text-2xl font-semibold tracking-tight">Where&rsquo;s your home base?</h2>
      <p className="mt-2 text-sm text-muted-foreground">
        Every route and every flight starts and ends here. Search by ICAO, IATA, or name.
      </p>

      {errorMessage && (
        <div className="mt-6 flex items-start gap-2 rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          <AlertTriangle className="mt-0.5 size-4 shrink-0" />
          <span>{errorMessage}</span>
        </div>
      )}

      <div className="mt-8">
        <AirportSearch onSelect={(selected) => onChange({ homeAirport: selected })} autoFocus />
      </div>

      {airport && (
        <div className="mt-6 rounded-lg border border-border bg-surface p-5">
          <div className="flex items-start justify-between gap-4">
            <div className="min-w-0">
              <p className="flex items-center gap-2 text-lg font-semibold tracking-tight">
                <span className="font-mono text-accent">{airport.icao}</span>
                <span className="truncate">{airport.name}</span>
              </p>
              <p className="mt-1 flex items-center gap-1.5 text-sm text-muted-foreground">
                <MapPin className="size-3.5" />
                {[airport.municipality, airport.country].filter(Boolean).join(', ') || 'Location unknown'}
              </p>
            </div>
            <Badge variant={warnings.length === 0 ? 'success' : 'warning'}>
              {warnings.length === 0 ? 'Good hub' : 'Review below'}
            </Badge>
          </div>

          <dl className="mt-4 grid grid-cols-2 gap-4 text-sm sm:grid-cols-3">
            <div>
              <dt className="text-xs uppercase tracking-wide text-muted-foreground">Longest runway</dt>
              <dd className="mt-0.5 font-medium tabular-nums">
                {airport.longestRunwayFt !== null ? `${airport.longestRunwayFt.toLocaleString()} ft` : 'Unknown'}
              </dd>
            </div>
            <div>
              <dt className="text-xs uppercase tracking-wide text-muted-foreground">Scheduled service</dt>
              <dd className="mt-0.5 flex items-center gap-1 font-medium">
                {airport.hasScheduledService ? (
                  <>
                    <CheckCircle2 className="size-3.5 text-success" /> Yes
                  </>
                ) : (
                  'No'
                )}
              </dd>
            </div>
            <div>
              <dt className="text-xs uppercase tracking-wide text-muted-foreground">Category</dt>
              <dd className="mt-0.5 font-medium">{airport.sizeCategory}</dd>
            </div>
          </dl>

          {warnings.length > 0 && (
            <div className="mt-4 space-y-2 rounded-md border border-warning/30 bg-warning/10 p-3">
              {warnings.map((warning) => (
                <p key={warning} className="flex items-start gap-2 text-xs text-warning">
                  <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
                  <span>{warning}</span>
                </p>
              ))}
              <p className="pl-5 text-xs text-muted-foreground">
                You can still use this airport — this is just a heads-up, not a blocker.
              </p>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
