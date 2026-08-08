import { Lock } from 'lucide-react'

import { PlaystyleCard } from '@/components/shared/PlaystyleCard'
import { useSettings } from '@/hooks/useSettings'
import { usePlaystyles } from '@/hooks/usePlaystyles'

import { FALLBACK_CURRENCY, PLAYSTYLE_META, type WizardData } from '../wizardData'

interface PlaystyleStepProps {
  data: WizardData
  onChange: (patch: Partial<WizardData>) => void
  errorMessage: string | null
}

export function PlaystyleStep({ data, onChange, errorMessage }: PlaystyleStepProps) {
  const { status, playstyles } = usePlaystyles()
  const { currencies } = useSettings()
  const currency = currencies.find((c) => c.code === data.currencyCode) ?? FALLBACK_CURRENCY

  return (
    <div>
      <h2 className="text-2xl font-semibold tracking-tight">Choose your playstyle.</h2>
      <p className="mt-2 text-sm text-muted-foreground">
        This sets your airline&rsquo;s starting capital and fixed costs — lease, insurance and deposit. Unlike
        strategy, it shapes how your airline is <em>run</em>, not just how fast it earns.
      </p>

      <div className="mt-4 flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 px-4 py-3 text-sm text-foreground">
        <Lock className="mt-0.5 size-4 shrink-0 text-warning" />
        <p className="min-w-0 break-words">
          <span className="font-medium">This cannot be changed later.</span> Switching means deleting this airline
          and starting a new one — going Casual → True-life would multiply your fixed costs roughly twelvefold
          overnight, and the reverse would trivialise everything you&rsquo;d earned.
        </p>
      </div>

      {errorMessage && (
        <div className="mt-6 rounded-md border border-danger/30 bg-danger/10 px-4 py-3 text-sm text-danger">
          {errorMessage}
        </div>
      )}

      <div className="mt-8 grid gap-4 sm:grid-cols-2">
        {PLAYSTYLE_META.map((meta) => (
          <PlaystyleCard
            key={meta.id}
            meta={meta}
            info={status === 'ready' ? playstyles.find((p) => p.playstyle === meta.id) : undefined}
            figuresUnavailable={status === 'error'}
            currency={currency}
            selected={data.playstyle === meta.id}
            onSelect={() => onChange({ playstyle: meta.id })}
          />
        ))}
      </div>

      {status === 'error' && (
        <p className="mt-3 text-xs text-muted-foreground">
          Couldn&rsquo;t load the live figures for each playstyle right now — you can still choose one; the exact
          numbers will be available once you&rsquo;re connected.
        </p>
      )}
    </div>
  )
}
