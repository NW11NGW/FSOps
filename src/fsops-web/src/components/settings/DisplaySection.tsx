import { toast } from 'sonner'

import { ToggleGroup } from '@/components/shared/ToggleGroup'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useSettings } from '@/hooks/useSettings'
import { useTheme } from '@/hooks/useTheme'
import { ApiError } from '@/lib/api'
import { formatMoney } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { AppSettings } from '@/types/settings'

const EXAMPLE_BASE_AMOUNT = 1_234_567.89

export function DisplaySection() {
  const { settings, currencies, status, error, updateSettings, currentCurrency, refetch } = useSettings()
  const [theme, toggleTheme] = useTheme()

  async function change(patch: Partial<AppSettings>, label: string) {
    try {
      await updateSettings(patch)
      toast.success(`${label} updated`)
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : `Could not update ${label.toLowerCase()} — reverted.`)
    }
  }

  if (status === 'loading') {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Display</CardTitle>
          <CardDescription>Currency, units, and how times are shown across FSOps.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <Skeleton className="h-11 w-full" />
          <Skeleton className="h-11 w-2/3" />
          <Skeleton className="h-11 w-1/2" />
        </CardContent>
      </Card>
    )
  }

  if (status === 'error') {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Display</CardTitle>
          <CardDescription>Currency, units, and how times are shown across FSOps.</CardDescription>
        </CardHeader>
        <CardContent className="flex items-center justify-between gap-4">
          <p className="text-sm text-danger">{error ?? 'Could not load settings.'}</p>
          <Button type="button" variant="outline" size="sm" onClick={refetch}>
            Try again
          </Button>
        </CardContent>
      </Card>
    )
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Display</CardTitle>
        <CardDescription>Currency, units, and how times are shown across FSOps.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-6">
        <div>
          <p className="text-sm font-medium">Currency</p>
          <div className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-4">
            {currencies.map((currency) => {
              const selected = currency.code === settings.currencyCode
              return (
                <button
                  key={currency.code}
                  type="button"
                  aria-pressed={selected}
                  onClick={() => void change({ currencyCode: currency.code }, 'Currency')}
                  className={cn(
                    'rounded-md border px-3 py-2 text-left text-sm transition-colors',
                    selected ? 'border-accent bg-accent/10' : 'border-border bg-surface hover:border-accent/40',
                  )}
                >
                  <span className="font-mono font-semibold">{currency.code}</span>
                  <span className="ml-1.5 text-xs text-muted-foreground">{currency.symbol}</span>
                </button>
              )
            })}
          </div>
          <p className="mt-2 text-xs text-muted-foreground">
            Example: <span className="font-medium text-foreground">{formatMoney(EXAMPLE_BASE_AMOUNT, currentCurrency)}</span>
          </p>
        </div>

        <div className="grid gap-6 sm:grid-cols-2">
          <div>
            <p className="text-sm font-medium">Distance</p>
            <div className="mt-2">
              <ToggleGroup
                ariaLabel="Distance unit"
                value={settings.distanceUnit}
                onChange={(v) => void change({ distanceUnit: v }, 'Distance unit')}
                options={[
                  { value: 'Nm', label: 'Nautical miles' },
                  { value: 'Km', label: 'Kilometres' },
                ]}
              />
            </div>
          </div>
          <div>
            <p className="text-sm font-medium">Altitude</p>
            <div className="mt-2">
              <ToggleGroup
                ariaLabel="Altitude unit"
                value={settings.altitudeUnit}
                onChange={(v) => void change({ altitudeUnit: v }, 'Altitude unit')}
                options={[
                  { value: 'Feet', label: 'Feet' },
                  { value: 'Metres', label: 'Metres' },
                ]}
              />
            </div>
          </div>
          <div>
            <p className="text-sm font-medium">Weight</p>
            <div className="mt-2">
              <ToggleGroup
                ariaLabel="Weight unit"
                value={settings.weightUnit}
                onChange={(v) => void change({ weightUnit: v }, 'Weight unit')}
                options={[
                  { value: 'Kg', label: 'Kilograms' },
                  { value: 'Lb', label: 'Pounds' },
                ]}
              />
            </div>
          </div>
          <div>
            <p className="text-sm font-medium">Time zone</p>
            <div className="mt-2">
              <ToggleGroup
                ariaLabel="Time display"
                value={settings.timeDisplay}
                onChange={(v) => void change({ timeDisplay: v }, 'Time display')}
                options={[
                  { value: 'Utc', label: 'UTC' },
                  { value: 'Local', label: 'Local' },
                ]}
              />
            </div>
          </div>
          <div>
            <p className="text-sm font-medium">Clock format</p>
            <div className="mt-2">
              <ToggleGroup
                ariaLabel="Clock format"
                value={settings.use24HourClock ? '24' : '12'}
                onChange={(v) => void change({ use24HourClock: v === '24' }, 'Clock format')}
                options={[
                  { value: '24', label: '24-hour' },
                  { value: '12', label: '12-hour' },
                ]}
              />
            </div>
          </div>
          <div>
            <p className="text-sm font-medium">Theme</p>
            <div className="mt-2">
              <ToggleGroup
                ariaLabel="Theme"
                value={theme}
                onChange={(v) => {
                  if (v !== theme) toggleTheme()
                  void change({ theme: v }, 'Theme')
                }}
                options={[
                  { value: 'dark', label: 'Dark' },
                  { value: 'light', label: 'Light' },
                ]}
              />
            </div>
          </div>
        </div>
      </CardContent>
    </Card>
  )
}
