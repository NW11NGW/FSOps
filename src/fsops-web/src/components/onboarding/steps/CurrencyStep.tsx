import { Check } from 'lucide-react'

import { ToggleGroup } from '@/components/shared/ToggleGroup'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { useSettings } from '@/hooks/useSettings'
import { formatMoney } from '@/lib/format'
import { cn } from '@/lib/utils'

import { FALLBACK_CURRENCY, type WizardData } from '../wizardData'

interface CurrencyStepProps {
  data: WizardData
  onChange: (patch: Partial<WizardData>) => void
  errorMessage: string | null
}

const EXAMPLE_BASE_AMOUNT = 1_234_567.89

export function CurrencyStep({ data, onChange, errorMessage }: CurrencyStepProps) {
  const { currencies, status } = useSettings()
  const selectedCurrency = currencies.find((c) => c.code === data.currencyCode) ?? FALLBACK_CURRENCY

  return (
    <div>
      <h2 className="text-2xl font-semibold tracking-tight">Currency &amp; units.</h2>
      <p className="mt-2 text-sm text-muted-foreground">
        How money, distance, altitude, weight, and time show up across FSOps. Change these later in Settings any
        time.
      </p>

      {errorMessage && (
        <div className="mt-6 rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          {errorMessage}
        </div>
      )}

      <div className="mt-8">
        <Label>Currency</Label>
        {status === 'loading' ? (
          <div className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-3">
            {Array.from({ length: 6 }).map((_, i) => (
              <Skeleton key={i} className="h-11 w-full" />
            ))}
          </div>
        ) : (
          <div className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-3">
            {currencies.map((currency) => {
              const selected = currency.code === data.currencyCode
              return (
                <button
                  key={currency.code}
                  type="button"
                  aria-pressed={selected}
                  onClick={() => onChange({ currencyCode: currency.code })}
                  className={cn(
                    'flex items-center justify-between gap-2 rounded-md border px-3 py-2.5 text-left text-sm transition-colors',
                    selected ? 'border-accent bg-accent/10' : 'border-border bg-surface hover:border-accent/40',
                  )}
                >
                  <span>
                    <span className="font-mono font-semibold">{currency.code}</span>
                    <span className="ml-1.5 text-xs text-muted-foreground">{currency.symbol}</span>
                  </span>
                  {selected && <Check className="size-4 shrink-0 text-accent" />}
                </button>
              )
            })}
          </div>
        )}
        {selectedCurrency && (
          <p className="mt-3 text-sm text-muted-foreground">
            Example:{' '}
            <span className="font-medium tabular-nums text-foreground">
              {formatMoney(EXAMPLE_BASE_AMOUNT, selectedCurrency)}
            </span>
          </p>
        )}
      </div>

      <div className="mt-8 grid gap-6 sm:grid-cols-2">
        <div>
          <Label>Distance</Label>
          <div className="mt-2">
            <ToggleGroup
              ariaLabel="Distance unit"
              value={data.distanceUnit}
              onChange={(value) => onChange({ distanceUnit: value })}
              options={[
                { value: 'Nm', label: 'Nautical miles' },
                { value: 'Km', label: 'Kilometres' },
              ]}
            />
          </div>
        </div>
        <div>
          <Label>Altitude</Label>
          <div className="mt-2">
            <ToggleGroup
              ariaLabel="Altitude unit"
              value={data.altitudeUnit}
              onChange={(value) => onChange({ altitudeUnit: value })}
              options={[
                { value: 'Feet', label: 'Feet' },
                { value: 'Metres', label: 'Metres' },
              ]}
            />
          </div>
        </div>
        <div>
          <Label>Weight</Label>
          <div className="mt-2">
            <ToggleGroup
              ariaLabel="Weight unit"
              value={data.weightUnit}
              onChange={(value) => onChange({ weightUnit: value })}
              options={[
                { value: 'Kg', label: 'Kilograms' },
                { value: 'Lb', label: 'Pounds' },
              ]}
            />
          </div>
        </div>
        <div>
          <Label>Time</Label>
          <div className="mt-2 flex flex-wrap gap-2">
            <ToggleGroup
              ariaLabel="Time display"
              value={data.timeDisplay}
              onChange={(value) => onChange({ timeDisplay: value })}
              options={[
                { value: 'Utc', label: 'UTC' },
                { value: 'Local', label: 'Local' },
              ]}
            />
            <ToggleGroup
              ariaLabel="Clock format"
              value={data.use24HourClock ? '24' : '12'}
              onChange={(value) => onChange({ use24HourClock: value === '24' })}
              options={[
                { value: '24', label: '24-hour' },
                { value: '12', label: '12-hour' },
              ]}
            />
          </div>
        </div>
      </div>
    </div>
  )
}
