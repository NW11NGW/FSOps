import { Info } from 'lucide-react'

import { StrategyProfileCard } from '@/components/shared/StrategyProfileCard'
import { useStrategyProfiles } from '@/hooks/useStrategyProfiles'
import { STRATEGY_NEVER_BLOCKS_NOTICE } from '@/lib/strategyProfileCopy'

import { STRATEGY_PROFILES, type WizardData } from '../wizardData'

interface StrategyStepProps {
  data: WizardData
  onChange: (patch: Partial<WizardData>) => void
  errorMessage: string | null
}

export function StrategyStep({ data, onChange, errorMessage }: StrategyStepProps) {
  const { status, profiles } = useStrategyProfiles()

  return (
    <div>
      <h2 className="text-2xl font-semibold tracking-tight">Choose your strategy.</h2>
      <p className="mt-2 text-sm text-muted-foreground">
        This shapes how you price fares, how demand responds, and where your costs sit. Lean into it, or evolve
        later.
      </p>

      <div className="mt-4 flex items-start gap-2 rounded-md border border-accent/30 bg-accent/10 px-4 py-3 text-sm text-foreground">
        <Info className="mt-0.5 size-4 shrink-0 text-accent" />
        <p className="min-w-0 break-words">{STRATEGY_NEVER_BLOCKS_NOTICE}</p>
      </div>

      {errorMessage && (
        <div className="mt-6 rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          {errorMessage}
        </div>
      )}

      <div className="mt-8 grid gap-4 sm:grid-cols-2">
        {STRATEGY_PROFILES.map((profile) => (
          <StrategyProfileCard
            key={profile.id}
            meta={profile}
            info={status === 'ready' ? profiles.find((p) => p.profile === profile.id) : undefined}
            figuresUnavailable={status === 'error'}
            selected={data.strategyProfile === profile.id}
            onSelect={() => onChange({ strategyProfile: profile.id })}
          />
        ))}
      </div>

      {status === 'error' && (
        <p className="mt-3 text-xs text-muted-foreground">
          Couldn&rsquo;t load the live figures for each strategy right now — you can still choose one; the exact
          numbers will be available once you&rsquo;re connected.
        </p>
      )}
    </div>
  )
}
