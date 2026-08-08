import { useEffect, useRef, useState } from 'react'
import { PlaneTakeoff } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { useSettings } from '@/hooks/useSettings'
import { ApiError, post } from '@/lib/api'
import { cn } from '@/lib/utils'
import type { Airline } from '@/types/airline'

import { WizardProgress } from './WizardProgress'
import { AircraftStep } from './steps/AircraftStep'
import { HomeBaseStep } from './steps/HomeBaseStep'
import { IdentityStep } from './steps/IdentityStep'
import { PlaystyleStep } from './steps/PlaystyleStep'
import { ReviewStep } from './steps/ReviewStep'
import { StrategyStep } from './steps/StrategyStep'
import { CurrencyStep } from './steps/CurrencyStep'
import { WelcomeStep } from './steps/WelcomeStep'
import {
  DEFAULT_WIZARD_DATA,
  STEP_VALIDATORS,
  WIZARD_STEPS,
  buildCreateAirlineInput,
  resolveErrorStepIndex,
  type WizardData,
} from './wizardData'

interface OnboardingWizardProps {
  onCreated: (airline: Airline) => void
}

export function OnboardingWizard({ onCreated }: OnboardingWizardProps) {
  const { settings, status: settingsStatus } = useSettings()
  const seededFromSettings = useRef(false)

  const [data, setData] = useState<WizardData>(DEFAULT_WIZARD_DATA)
  const [currentIndex, setCurrentIndex] = useState(0)
  const [furthestIndex, setFurthestIndex] = useState(0)
  const [direction, setDirection] = useState<1 | -1>(1)
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)

  // Seed sensible defaults from the app's existing settings the first time they load,
  // without clobbering anything the user has already typed.
  useEffect(() => {
    if (seededFromSettings.current || settingsStatus !== 'ready') return
    seededFromSettings.current = true
    setData((prev) => ({
      ...prev,
      currencyCode: settings.currencyCode,
      distanceUnit: settings.distanceUnit,
      altitudeUnit: settings.altitudeUnit,
      weightUnit: settings.weightUnit,
      timeDisplay: settings.timeDisplay,
      use24HourClock: settings.use24HourClock,
    }))
  }, [settingsStatus, settings])

  const update = (patch: Partial<WizardData>) => {
    setData((prev) => ({ ...prev, ...patch }))
    if (submitError) setSubmitError(null)
  }

  const step = WIZARD_STEPS[currentIndex] ?? WIZARD_STEPS[0]
  const isValid = STEP_VALIDATORS[step.key](data)
  const isLastStep = currentIndex === WIZARD_STEPS.length - 1

  const goTo = (index: number, dir: 1 | -1) => {
    setDirection(dir)
    setCurrentIndex(index)
  }

  const handleBack = () => {
    if (currentIndex === 0 || submitting) return
    goTo(currentIndex - 1, -1)
  }

  const handleJump = (index: number) => {
    if (index > furthestIndex || submitting) return
    goTo(index, index > currentIndex ? 1 : -1)
  }

  async function handleSubmit() {
    setSubmitting(true)
    setSubmitError(null)
    try {
      const payload = buildCreateAirlineInput(data)
      const created = await post<Airline>('/airline', payload)
      onCreated(created)
    } catch (err) {
      const message =
        err instanceof ApiError
          ? err.message
          : 'Something went wrong creating your airline. Check your connection and try again.'
      setSubmitError(message)
      const errorIndex = resolveErrorStepIndex(message)
      setFurthestIndex((prev) => Math.max(prev, errorIndex))
      goTo(errorIndex, errorIndex >= currentIndex ? 1 : -1)
    } finally {
      setSubmitting(false)
    }
  }

  const handleNext = () => {
    if (!isValid || submitting) return
    if (isLastStep) {
      void handleSubmit()
      return
    }
    const nextIndex = currentIndex + 1
    setFurthestIndex((prev) => Math.max(prev, nextIndex))
    goTo(nextIndex, 1)
  }

  const previewName = data.name.trim() || 'Your airline'

  return (
    <div className="fixed inset-0 z-50 flex bg-background text-foreground">
      <aside className="hidden w-80 shrink-0 flex-col justify-between border-r border-border bg-surface p-8 lg:flex">
        <div>
          <div className="mb-10 flex items-center gap-3">
            <div
              className="flex size-10 items-center justify-center rounded-lg text-white shadow-elevation-2 transition-colors"
              style={{ backgroundColor: data.accentColour }}
            >
              <PlaneTakeoff className="size-5" />
            </div>
            <div className="min-w-0">
              <p className="truncate text-sm font-semibold tracking-tight">{previewName}</p>
              <p className="text-xs text-muted-foreground">{data.icaoCode || 'New airline'}</p>
            </div>
          </div>
          <WizardProgress currentIndex={currentIndex} furthestIndex={furthestIndex} onJump={handleJump} />
        </div>
        <p className="text-xs text-muted-foreground">
          FSOps · Build and fly your own virtual airline in Microsoft Flight Simulator 2024.
        </p>
      </aside>

      <div className="flex flex-1 flex-col overflow-hidden">
        <header className="flex items-center justify-between border-b border-border px-6 py-4 lg:hidden">
          <div className="flex items-center gap-2">
            <div
              className="flex size-8 items-center justify-center rounded-md text-white"
              style={{ backgroundColor: data.accentColour }}
            >
              <PlaneTakeoff className="size-4" />
            </div>
            <span className="text-sm font-semibold">{previewName}</span>
          </div>
          <span className="text-xs text-muted-foreground">
            Step {currentIndex + 1} of {WIZARD_STEPS.length}
          </span>
        </header>

        <div className="flex-1 overflow-y-auto scrollbar-thin">
          <div className="mx-auto w-full max-w-2xl px-6 py-10 sm:py-16">
            <div
              key={currentIndex}
              className={cn(
                'animate-in fade-in-0 duration-300',
                direction === 1 ? 'slide-in-from-right-6' : 'slide-in-from-left-6',
              )}
            >
              {step.key === 'welcome' && <WelcomeStep />}
              {step.key === 'identity' && <IdentityStep data={data} onChange={update} errorMessage={submitError} />}
              {step.key === 'homeBase' && <HomeBaseStep data={data} onChange={update} errorMessage={submitError} />}
              {step.key === 'playstyle' && <PlaystyleStep data={data} onChange={update} errorMessage={submitError} />}
              {step.key === 'strategy' && <StrategyStep data={data} onChange={update} errorMessage={submitError} />}
              {step.key === 'aircraft' && <AircraftStep data={data} onChange={update} errorMessage={submitError} />}
              {step.key === 'currency' && <CurrencyStep data={data} onChange={update} errorMessage={submitError} />}
              {step.key === 'review' && (
                <ReviewStep
                  data={data}
                  onChange={update}
                  onEditStep={handleJump}
                  submitting={submitting}
                  errorMessage={submitError}
                />
              )}
            </div>
          </div>
        </div>

        <footer className="flex items-center justify-between border-t border-border px-6 py-4">
          <Button type="button" variant="ghost" onClick={handleBack} disabled={currentIndex === 0 || submitting}>
            Back
          </Button>
          <Button type="button" onClick={handleNext} disabled={!isValid || submitting}>
            {isLastStep ? (submitting ? 'Founding your airline…' : 'Found your airline') : 'Next'}
          </Button>
        </footer>
      </div>
    </div>
  )
}
