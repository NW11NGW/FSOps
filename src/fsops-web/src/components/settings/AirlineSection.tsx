import { useEffect, useState } from 'react'
import { useOutletContext } from 'react-router-dom'
import { Check } from 'lucide-react'
import { toast } from 'sonner'

import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { ACCENT_PALETTE } from '@/lib/accentPalette'
import { ApiError, put } from '@/lib/api'
import { applyAccentColour, isValidHexColour } from '@/lib/theme'
import { cn } from '@/lib/utils'
import type { LiveContext } from '@/types/live-context'

export function AirlineSection() {
  const { airlineSummary } = useOutletContext<LiveContext>()
  const airline = airlineSummary.data?.airline ?? null

  const [name, setName] = useState('')
  const [accentColour, setAccentColour] = useState('#0EA5E9')
  const [saving, setSaving] = useState(false)
  const [dirty, setDirty] = useState(false)

  useEffect(() => {
    if (airline && !dirty) {
      setName(airline.name)
      setAccentColour(airline.accentColour)
    }
  }, [airline, dirty])

  const nameValid = name.trim().length >= 2 && name.trim().length <= 40
  const colourValid = isValidHexColour(accentColour)
  const canSave = dirty && nameValid && colourValid && !saving

  async function handleSave() {
    if (!airline) return
    setSaving(true)
    try {
      await put('/airline', { ...airline, name: name.trim(), accentColour })
      applyAccentColour(accentColour)
      setDirty(false)
      toast.success('Airline details updated')
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not save airline details — reverted.')
      setName(airline.name)
      setAccentColour(airline.accentColour)
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
