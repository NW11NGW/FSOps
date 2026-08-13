import { AlertTriangle, Loader2, PenLine } from 'lucide-react'

import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { usePlaystyles } from '@/hooks/usePlaystyles'
import { useSettings } from '@/hooks/useSettings'
import { formatMoney } from '@/lib/format'
import { cn } from '@/lib/utils'

import {
  AIRCRAFT_OPTIONS,
  FALLBACK_CURRENCY,
  PLAYSTYLE_META,
  STRATEGY_PROFILES,
  WIZARD_STEPS,
  estimateMonthlyPayment,
  type WizardData,
  type WizardStepKey,
} from '../wizardData'

interface ReviewStepProps {
  data: WizardData
  onChange: (patch: Partial<WizardData>) => void
  onEditStep: (index: number) => void
  submitting: boolean
  errorMessage: string | null
}

function findIndex(key: WizardStepKey) {
  return WIZARD_STEPS.findIndex((s) => s.key === key)
}

export function ReviewStep({ data, onChange, onEditStep, submitting, errorMessage }: ReviewStepProps) {
  const { currencies } = useSettings()
  const { status: playstylesStatus, playstyles } = usePlaystyles()
  const currency = currencies.find((c) => c.code === data.currencyCode) ?? FALLBACK_CURRENCY
  const strategy = STRATEGY_PROFILES.find((s) => s.id === data.strategyProfile)
  const playstyle = PLAYSTYLE_META.find((p) => p.id === data.playstyle)
  const aircraft = AIRCRAFT_OPTIONS.find((a) => a.id === data.starterAircraftFamily)

  // The startup loan's rate is NEVER chosen by the player: one they could set would be set to
  // zero, which makes borrowing free. A brand-new airline has no trading history, so the
  // backend always prices a starting loan at the playstyle's rate ceiling; startingLoanAnnualRatePct
  // (from GET /airline/playstyles) is that same figure, quoted here rather than guessed.
  const playstyleInfo = data.playstyle ? playstyles.find((p) => p.playstyle === data.playstyle) : undefined
  const loanRatePct = playstyleInfo?.startingLoanAnnualRatePct ?? null
  const loanRateAvailable = playstylesStatus === 'ready' && loanRatePct !== null
  const effectiveLoanRatePct = loanRatePct ?? 0
  const monthlyPayment = data.loanEnabled && loanRateAvailable
    ? estimateMonthlyPayment(data.loanAmount, data.loanTermMonths, effectiveLoanRatePct)
    : 0
  const totalInterest = data.loanEnabled && loanRateAvailable ? monthlyPayment * data.loanTermMonths - data.loanAmount : 0
  const loanFieldsInvalid = data.loanEnabled && (data.loanAmount <= 0 || data.loanTermMonths <= 0)

  const rows: { label: string; value: string; stepKey: WizardStepKey }[] = [
    { label: 'Airline', value: `${data.name.trim()} (${data.icaoCode})`, stepKey: 'identity' },
    { label: 'Your name', value: data.pilotName.trim() || 'Local Pilot (default)', stepKey: 'identity' },
    {
      label: 'Home base',
      value: data.homeAirport ? `${data.homeAirport.icao} — ${data.homeAirport.name}` : '—',
      stepKey: 'homeBase',
    },
    { label: 'Playstyle', value: playstyle ? `${playstyle.label} (permanent)` : '—', stepKey: 'playstyle' },
    { label: 'Strategy', value: strategy?.label ?? '—', stepKey: 'strategy' },
    { label: 'Starter aircraft', value: aircraft?.label ?? '—', stepKey: 'aircraft' },
    { label: 'Currency', value: currency ? `${currency.code} (${currency.symbol})` : data.currencyCode, stepKey: 'currency' },
    {
      label: 'Units',
      value: `${data.distanceUnit} · ${data.altitudeUnit} · ${data.weightUnit} · ${data.timeDisplay} · ${data.use24HourClock ? '24h' : '12h'}`,
      stepKey: 'currency',
    },
    {
      label: 'MSFS panel',
      value: data.communityFolderPath ? data.communityFolderPath : 'Skipped — set up later in Settings',
      stepKey: 'communityFolder',
    },
    {
      label: 'SimBrief Pilot ID',
      value: data.simBriefPilotId ? data.simBriefPilotId : 'Skipped — set up later in Settings',
      stepKey: 'onlinePresence',
    },
    {
      label: 'VATSIM CID',
      value: data.vatsimCid ? data.vatsimCid : 'Skipped — set up later in Settings',
      stepKey: 'onlinePresence',
    },
  ]

  return (
    <div>
      <h2 className="text-2xl font-semibold tracking-tight">Finance &amp; review.</h2>
      <p className="mt-2 text-sm text-muted-foreground">
        Start with a loan if you want a bigger opening fleet, then check everything over.
      </p>

      {errorMessage && (
        <div className="mt-6 flex items-start gap-2 rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          <AlertTriangle className="mt-0.5 size-4 shrink-0" />
          <span>{errorMessage}</span>
        </div>
      )}

      <div className="mt-8">
        <Label>Startup loan</Label>
        <div className="mt-3 grid gap-3 sm:grid-cols-2">
          <button
            type="button"
            aria-pressed={!data.loanEnabled}
            onClick={() => onChange({ loanEnabled: false })}
            className={cn(
              'rounded-lg border p-4 text-left transition-colors',
              !data.loanEnabled ? 'border-accent bg-accent/10' : 'border-border bg-surface hover:border-accent/40',
            )}
          >
            <p className="text-sm font-semibold">Start debt-free</p>
            <p className="mt-1 text-xs text-muted-foreground">Launch on cash alone — a smaller opening fleet.</p>
          </button>
          <button
            type="button"
            aria-pressed={data.loanEnabled}
            onClick={() => onChange({ loanEnabled: true })}
            className={cn(
              'rounded-lg border p-4 text-left transition-colors',
              data.loanEnabled ? 'border-accent bg-accent/10' : 'border-border bg-surface hover:border-accent/40',
            )}
          >
            <p className="text-sm font-semibold">Add a startup loan</p>
            <p className="mt-1 text-xs text-muted-foreground">Borrow now, repay monthly, grow faster.</p>
          </button>
        </div>

        {data.loanEnabled && (
          <div className="mt-4 grid gap-4 rounded-lg border border-border bg-surface p-4 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="loan-amount" className="text-xs">
                Amount
              </Label>
              <Input
                id="loan-amount"
                type="number"
                min={0}
                step={10000}
                value={data.loanAmount}
                onChange={(event) => onChange({ loanAmount: Number(event.target.value) })}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="loan-term" className="text-xs">
                Term (months)
              </Label>
              <Input
                id="loan-term"
                type="number"
                min={1}
                step={1}
                value={data.loanTermMonths}
                onChange={(event) => onChange({ loanTermMonths: Number(event.target.value) })}
              />
            </div>
            <div className="sm:col-span-2">
              {loanFieldsInvalid ? (
                <p className="text-xs text-danger">Amount and term must be greater than zero.</p>
              ) : !loanRateAvailable ? (
                <p className="text-xs text-muted-foreground">
                  {playstylesStatus === 'error' ? 'Could not load the rate for this loan.' : 'Pricing this loan…'}
                </p>
              ) : (
                currency && (
                  <div className="space-y-1 text-sm text-muted-foreground">
                    <p>
                      Annual rate:{' '}
                      <span className="font-medium tabular-nums text-foreground">{effectiveLoanRatePct.toFixed(2)}%</span>{' '}
                      <span className="text-xs">
                        — a new airline has no trading history yet, so this is priced at your playstyle&rsquo;s ceiling,
                        not chosen by you.
                      </span>
                    </p>
                    <p>
                      Monthly repayment:{' '}
                      <span className="font-medium tabular-nums text-foreground">{formatMoney(monthlyPayment, currency)}</span>
                    </p>
                    <p>
                      Total interest over the term:{' '}
                      <span className="font-medium tabular-nums text-foreground">{formatMoney(totalInterest, currency)}</span>
                    </p>
                  </div>
                )
              )}
            </div>
          </div>
        )}
      </div>

      <div
        className="mt-8 flex items-center gap-4 rounded-lg border p-5"
        style={{ borderColor: data.accentColour, backgroundColor: `${data.accentColour}1a` }}
      >
        <div
          className="flex size-12 shrink-0 items-center justify-center rounded-lg text-white"
          style={{ backgroundColor: data.accentColour }}
        >
          <span className="font-mono text-sm font-bold">{data.icaoCode || '—'}</span>
        </div>
        <div className="min-w-0">
          <p className="truncate text-lg font-semibold tracking-tight">{data.name.trim() || 'Your airline'}</p>
          <p className="text-xs text-muted-foreground">{strategy?.label ?? 'No strategy chosen'} carrier</p>
        </div>
      </div>

      <dl className="mt-6 divide-y divide-border rounded-lg border border-border">
        {rows.map((row) => (
          <div key={row.label} className="flex items-center justify-between gap-4 px-4 py-3">
            <div className="min-w-0">
              <dt className="text-xs uppercase tracking-wide text-muted-foreground">{row.label}</dt>
              <dd className="mt-0.5 truncate text-sm font-medium">{row.value}</dd>
            </div>
            <button
              type="button"
              onClick={() => onEditStep(findIndex(row.stepKey))}
              className="flex shrink-0 items-center gap-1 rounded-md px-2 py-1 text-xs font-medium text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
            >
              <PenLine className="size-3" />
              Edit
            </button>
          </div>
        ))}
        <div className="flex items-center justify-between gap-4 px-4 py-3">
          <div className="min-w-0">
            <dt className="text-xs uppercase tracking-wide text-muted-foreground">Startup loan</dt>
            <dd className="mt-0.5 truncate text-sm font-medium">
              {data.loanEnabled && currency && loanRateAvailable
                ? `${formatMoney(data.loanAmount, currency)} over ${data.loanTermMonths} months at ${effectiveLoanRatePct.toFixed(2)}% APR (~${formatMoney(monthlyPayment, currency)}/mo)`
                : data.loanEnabled
                  ? `${formatMoney(data.loanAmount, currency)} over ${data.loanTermMonths} months`
                  : 'None — starting debt-free'}
            </dd>
          </div>
        </div>
      </dl>

      {submitting && (
        <div className="mt-6 flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="size-4 animate-spin" />
          Founding your airline…
        </div>
      )}
    </div>
  )
}
