import { Check } from 'lucide-react'

import { cn } from '@/lib/utils'

import { STRATEGY_PROFILES, type WizardData } from '../wizardData'

interface StrategyStepProps {
  data: WizardData
  onChange: (patch: Partial<WizardData>) => void
  errorMessage: string | null
}

export function StrategyStep({ data, onChange, errorMessage }: StrategyStepProps) {
  return (
    <div>
      <h2 className="text-2xl font-semibold tracking-tight">Choose your strategy.</h2>
      <p className="mt-2 text-sm text-muted-foreground">
        This shapes the routes that suit you, how you price fares, and where your costs sit. Lean into it, or evolve
        later.
      </p>

      {errorMessage && (
        <div className="mt-6 rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          {errorMessage}
        </div>
      )}

      <div className="mt-8 grid gap-4 sm:grid-cols-2">
        {STRATEGY_PROFILES.map((profile) => {
          const selected = data.strategyProfile === profile.id
          return (
            <button
              key={profile.id}
              type="button"
              onClick={() => onChange({ strategyProfile: profile.id })}
              aria-pressed={selected}
              className={cn(
                'flex flex-col gap-3 rounded-lg border p-5 text-left transition-colors',
                selected ? 'border-accent bg-accent/10' : 'border-border bg-surface hover:border-accent/40',
              )}
            >
              <div className="flex items-center justify-between">
                <span className="text-base font-semibold tracking-tight">{profile.label}</span>
                {selected && (
                  <span className="flex size-5 items-center justify-center rounded-full bg-accent text-accent-foreground">
                    <Check className="size-3" />
                  </span>
                )}
              </div>
              <p className="text-xs font-medium uppercase tracking-wide text-accent">{profile.tagline}</p>
              <dl className="space-y-1.5 text-xs text-muted-foreground">
                <div>
                  <dt className="inline font-medium text-foreground">Routes — </dt>
                  <dd className="inline">{profile.routeLengths}</dd>
                </div>
                <div>
                  <dt className="inline font-medium text-foreground">Fares — </dt>
                  <dd className="inline">{profile.fares}</dd>
                </div>
                <div>
                  <dt className="inline font-medium text-foreground">Costs — </dt>
                  <dd className="inline">{profile.costs}</dd>
                </div>
              </dl>
            </button>
          )
        })}
      </div>
    </div>
  )
}
