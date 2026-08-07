import { Check } from 'lucide-react'

import { cn } from '@/lib/utils'

import { WIZARD_STEPS } from './wizardData'

interface WizardProgressProps {
  currentIndex: number
  furthestIndex: number
  onJump: (index: number) => void
}

export function WizardProgress({ currentIndex, furthestIndex, onJump }: WizardProgressProps) {
  return (
    <ol className="flex flex-col gap-1" aria-label="Onboarding progress">
      {WIZARD_STEPS.map((step, index) => {
        const isCurrent = index === currentIndex
        const isComplete = index < currentIndex
        const isReachable = index <= furthestIndex

        return (
          <li key={step.key}>
            <button
              type="button"
              disabled={!isReachable}
              onClick={() => onJump(index)}
              aria-current={isCurrent ? 'step' : undefined}
              className={cn(
                'flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm transition-colors',
                isCurrent
                  ? 'bg-accent/15 font-medium text-accent'
                  : isReachable
                    ? 'text-foreground/80 hover:bg-muted hover:text-foreground'
                    : 'cursor-not-allowed text-muted-foreground/50',
              )}
            >
              <span
                className={cn(
                  'flex size-5 shrink-0 items-center justify-center rounded-full border text-xs font-semibold',
                  isCurrent
                    ? 'border-accent bg-accent text-accent-foreground'
                    : isComplete
                      ? 'border-accent/50 bg-accent/15 text-accent'
                      : 'border-border text-muted-foreground',
                )}
              >
                {isComplete ? <Check className="size-3" /> : index + 1}
              </span>
              <span className="truncate">{step.label}</span>
            </button>
          </li>
        )
      })}
    </ol>
  )
}
