import { useEffect, useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import { Check, Info } from 'lucide-react'
import { toast } from 'sonner'

import { PlaystyleCard } from '@/components/shared/PlaystyleCard'
import { StrategyProfileCard } from '@/components/shared/StrategyProfileCard'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { PLAYSTYLE_META, STRATEGY_PROFILES } from '@/components/onboarding/wizardData'
import { usePlaystyles } from '@/hooks/usePlaystyles'
import { useSettings } from '@/hooks/useSettings'
import { useStrategyProfiles } from '@/hooks/useStrategyProfiles'
import { ACCENT_PALETTE } from '@/lib/accentPalette'
import { ApiError, put } from '@/lib/api'
import { STRATEGY_NEVER_BLOCKS_NOTICE } from '@/lib/strategyProfileCopy'
import { applyAccentColour, isValidHexColour } from '@/lib/theme'
import { cn } from '@/lib/utils'
import type { StrategyProfile } from '@/types/airline'
import type { LiveContext } from '@/types/live-context'

export function AirlineSection() {
  const { airlineSummary } = useOutletContext<LiveContext>()
  const airline = airlineSummary.data?.airline ?? null
  const { status: strategyStatus, profiles: strategyProfiles, refetch: refetchStrategyProfiles } = useStrategyProfiles()
  const { status: playstyleStatus, playstyles } = usePlaystyles()
  const { currentCurrency } = useSettings()

  const [name, setName] = useState('')
  const [pilotName, setPilotName] = useState('')
  const [accentColour, setAccentColour] = useState('#0EA5E9')
  const [strategyProfile, setStrategyProfile] = useState<StrategyProfile | null>(null)
  const [saving, setSaving] = useState(false)
  const [dirty, setDirty] = useState(false)

  const playerPilotName = airlineSummary.data?.playerPilotName ?? null

  useEffect(() => {
    if (airline && !dirty) {
      setName(airline.name)
      setAccentColour(airline.accentColour)
      setStrategyProfile(airline.strategyProfile)
    }
  }, [airline, dirty])

  useEffect(() => {
    if (playerPilotName !== null && !dirty) {
      setPilotName(playerPilotName)
    }
  }, [playerPilotName, dirty])

  const nameValid = name.trim().length >= 2 && name.trim().length <= 40
  const pilotNameValid = pilotName.trim().length >= 1 && pilotName.trim().length <= 40
  const colourValid = isValidHexColour(accentColour)
  const strategyChanged = airline !== null && strategyProfile !== null && strategyProfile !== airline.strategyProfile
  const canSave = dirty && nameValid && pilotNameValid && colourValid && strategyProfile !== null && !saving

  async function handleSave() {
    if (!airline || !strategyProfile) return
    setSaving(true)
    try {
      await put('/airline', {
        ...airline,
        name: name.trim(),
        accentColour,
        strategyProfile,
        pilotName: pilotName.trim(),
      })
      applyAccentColour(accentColour)
      setDirty(false)
      toast.success('Airline details updated')
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not save airline details — reverted.')
      setName(airline.name)
      setAccentColour(airline.accentColour)
      setStrategyProfile(airline.strategyProfile)
      if (playerPilotName !== null) setPilotName(playerPilotName)
      setDirty(false)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Airline</CardTitle>
        <CardDescription>
          Your airline&rsquo;s identity. Home base is fixed once you&rsquo;ve founded your airline.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-6">
        {airlineSummary.status === 'loading' && !airline ? (
          <div className="space-y-3">
            <Skeleton className="h-9 w-full" />
            <Skeleton className="h-9 w-48" />
          </div>
        ) : !airline ? (
          <p className="text-sm text-muted-foreground">Airline details are unavailable right now.</p>
        ) : (
          <>
            <div className="space-y-2">
              <Label htmlFor="settings-airline-name">Airline name</Label>
              <Input
                id="settings-airline-name"
                value={name}
                maxLength={40}
                onChange={(event) => {
                  setName(event.target.value)
                  setDirty(true)
                }}
                aria-invalid={!nameValid}
              />
            </div>

            <div className="space-y-2">
              <Label htmlFor="settings-pilot-name">Your name</Label>
              <Input
                id="settings-pilot-name"
                value={pilotName}
                maxLength={40}
                onChange={(event) => {
                  setPilotName(event.target.value)
                  setDirty(true)
                }}
                aria-invalid={!pilotNameValid}
                aria-describedby="settings-pilot-name-hint"
              />
              <p
                id="settings-pilot-name-hint"
                className={!pilotNameValid ? 'text-xs text-danger' : 'text-xs text-muted-foreground'}
              >
                {!pilotNameValid
                  ? '1–40 characters.'
                  : 'Shows on every flight you fly and throughout the Pilots page.'}
              </p>
            </div>

            <div>
              <Label>Accent colour</Label>
              <div className="mt-3 flex flex-wrap items-center gap-3">
                {ACCENT_PALETTE.map((swatch) => {
                  const selected = accentColour.toLowerCase() === swatch.hex.toLowerCase()
                  return (
                    <button
                      key={swatch.hex}
                      type="button"
                      title={swatch.name}
                      aria-label={swatch.name}
                      aria-pressed={selected}
                      onClick={() => {
                        setAccentColour(swatch.hex)
                        setDirty(true)
                      }}
                      className={cn(
                        'flex size-8 items-center justify-center rounded-full border-2 transition-transform',
                        selected ? 'scale-110 border-foreground' : 'border-transparent hover:scale-105',
                      )}
                      style={{ backgroundColor: swatch.hex }}
                    >
                      {selected && <Check className="size-3.5 text-white drop-shadow" />}
                    </button>
                  )
                })}
                <Input
                  value={accentColour}
                  maxLength={7}
                  className="w-28 font-mono"
                  aria-invalid={!colourValid}
                  onChange={(event) => {
                    setAccentColour(event.target.value)
                    setDirty(true)
                  }}
                />
              </div>
              {!colourValid && <p className="mt-1 text-xs text-danger">Use a 6-digit hex colour, e.g. #0EA5E9.</p>}
            </div>

            <div className="space-y-1">
              <Label>Home base</Label>
              <p className="text-sm text-muted-foreground">
                {airline.homeAirportIcao} <span className="text-xs">(fixed for now)</span>
              </p>
            </div>

            <div className="space-y-3 border-t border-border pt-6">
              <div>
                <Label>Playstyle</Label>
                <p className="mt-1 text-sm text-muted-foreground">
                  Chosen when this airline was founded — permanent, not editable here.
                </p>
              </div>

              {playstyleStatus === 'error' ? (
                <p className="text-sm text-muted-foreground">
                  {airline.playstyle === 'TrueLife' ? 'True-life' : 'Casual'}{' '}
                  <span className="text-xs">(figures unavailable right now)</span>
                </p>
              ) : (
                <div className="max-w-sm">
                  <PlaystyleCard
                    meta={
                      PLAYSTYLE_META.find((p) => p.id === airline.playstyle) ?? {
                        id: airline.playstyle,
                        label: airline.playstyle,
                        tagline: '',
                      }
                    }
                    info={
                      playstyleStatus === 'ready' ? playstyles.find((p) => p.playstyle === airline.playstyle) : undefined
                    }
                    currency={currentCurrency}
                    selected
                  />
                </div>
              )}
            </div>

            <div className="space-y-3 border-t border-border pt-6">
              <div>
                <Label>Strategy</Label>
                <p className="mt-1 text-sm text-muted-foreground">
                  Chosen at onboarding, but never permanent — pick a different profile any time.
                </p>
              </div>

              <div className="flex items-start gap-2 rounded-md border border-accent/30 bg-accent/10 px-4 py-3 text-sm text-foreground">
                <Info className="mt-0.5 size-4 shrink-0 text-accent" />
                <p className="min-w-0 break-words">{STRATEGY_NEVER_BLOCKS_NOTICE}</p>
              </div>

              {strategyStatus === 'error' && (
                <div className="flex flex-wrap items-start gap-2 rounded-md border border-warning/30 bg-warning/10 px-4 py-3 text-sm text-foreground">
                  <Info className="mt-0.5 size-4 shrink-0 text-warning" />
                  <p className="min-w-0 flex-1 break-words">
                    Couldn't load the figures for each strategy. If FSOps was updated while running, restarting it
                    should fix this. Choosing a profile still works.
                  </p>
                  <Button type="button" variant="outline" size="sm" onClick={refetchStrategyProfiles}>
                    Try again
                  </Button>
                </div>
              )}

              <div className="grid gap-4 sm:grid-cols-2">
                {STRATEGY_PROFILES.map((profile) => (
                  <StrategyProfileCard
                    key={profile.id}
                    meta={profile}
                    info={
                      strategyStatus === 'ready' ? strategyProfiles.find((p) => p.profile === profile.id) : undefined
                    }
                    figuresUnavailable={strategyStatus === 'error'}
                    selected={strategyProfile === profile.id}
                    onSelect={() => {
                      setStrategyProfile(profile.id)
                      setDirty(true)
                    }}
                  />
                ))}
              </div>

              {strategyChanged && (
                <div className="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 px-4 py-3 text-sm text-foreground">
                  <Info className="mt-0.5 size-4 shrink-0 text-warning" />
                  <p className="min-w-0 break-words">
                    This applies <span className="font-medium">going forward only</span>. Saving changes the fares
                    and demand suggested for new routes, and which routes get an advisory note from here on. It
                    never touches completed flights, posted ledger entries, or the fares already set on your
                    existing routes.
                  </p>
                </div>
              )}
            </div>

            <div className="flex justify-end">
              <Button type="button" onClick={() => void handleSave()} disabled={!canSave}>
                {saving ? 'Saving…' : 'Save changes'}
              </Button>
            </div>
          </>
        )}
      </CardContent>
    </Card>
  )
}
