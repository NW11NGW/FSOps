import { useEffect, useMemo, useState } from 'react'
import { Search, Shuffle } from 'lucide-react'
import { toast } from 'sonner'

import { ConditionBar } from '@/components/fleet/ConditionBar'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ToggleGroup } from '@/components/shared/ToggleGroup'
import { useAircraftTypes } from '@/hooks/useAircraftTypes'
import { useSettings } from '@/hooks/useSettings'
import { ApiError, get, post } from '@/lib/api'
import { cn } from '@/lib/utils'
import { AircraftRegistrationGenerator } from '@/lib/registration'
import type { AircraftCategory, AircraftCondition, AircraftTypeOption } from '@/types/fleet'

interface BuyLeaseDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  onSuccess: () => void
}

type Mode = 'lease' | 'buy'
type CategoryFilter = AircraftCategory | 'All'

const CATEGORY_OPTIONS: { value: CategoryFilter; label: string }[] = [
  { value: 'All', label: 'All' },
  { value: 'Narrowbody', label: 'Narrowbody' },
  { value: 'Widebody', label: 'Widebody' },
  { value: 'Regional', label: 'Regional' },
]

/** "1 month" / "2 months" - singular only for exactly 1, matching how the founding lease and
 *  LeaseAsync's own ledger description word it elsewhere in the app. */
function formatMonths(months: number): string {
  const rounded = Math.round(months * 10) / 10
  return `${rounded % 1 === 0 ? rounded : rounded.toFixed(1)} month${rounded === 1 ? '' : 's'}`
}

function matchesSearch(type: AircraftTypeOption, query: string): boolean {
  if (!query.trim()) return true
  const haystack = `${type.icaoType} ${type.manufacturer} ${type.name}`.toLowerCase()
  return haystack.includes(query.trim().toLowerCase())
}

export function BuyLeaseDialog({ open, onOpenChange, onSuccess }: BuyLeaseDialogProps) {
  const { types, status } = useAircraftTypes()
  const { fmt } = useSettings()

  const [mode, setMode] = useState<Mode>('lease')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [condition, setCondition] = useState<AircraftCondition>('New')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [search, setSearch] = useState('')
  const [category, setCategory] = useState<CategoryFilter>('All')

  const [registration, setRegistration] = useState('')
  const [registrationTouched, setRegistrationTouched] = useState(false)
  const [suggestionLoading, setSuggestionLoading] = useState(false)

  const filteredTypes = useMemo(
    () => types.filter((t) => (category === 'All' || t.category === category) && matchesSearch(t, search)),
    [types, category, search],
  )

  const selected = useMemo<AircraftTypeOption | null>(
    () => types.find((t) => t.id === selectedId) ?? filteredTypes[0] ?? types[0] ?? null,
    [types, filteredTypes, selectedId],
  )

  // Fresh, format-correct suggestion each time the dialog opens, shown pre-filled so that
  // randomising is the zero-effort path and typing over it is the deliberate one.
  // Falls back to a client-side generator if the request fails, so a flaky
  // connection never blocks buying/leasing outright (the server re-validates uniqueness anyway).
  useEffect(() => {
    if (!open) {
      setRegistration('')
      setRegistrationTouched(false)
      setSearch('')
      setCategory('All')
      return
    }

    let cancelled = false
    setSuggestionLoading(true)
    get<{ registration: string }>('/fleet/registration-suggestion')
      .then((result) => {
        if (cancelled) return
        setRegistration(result.registration)
      })
      .catch(() => {
        if (cancelled) return
        setRegistration(AircraftRegistrationGenerator.fallback())
      })
      .finally(() => {
        if (!cancelled) setSuggestionLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [open])

  async function handleRandomise() {
    setSuggestionLoading(true)
    try {
      const result = await get<{ registration: string }>('/fleet/registration-suggestion')
      setRegistration(result.registration)
      setRegistrationTouched(false)
    } catch {
      setRegistration(AircraftRegistrationGenerator.fallback())
    } finally {
      setSuggestionLoading(false)
    }
  }

  async function handleSubmit() {
    if (!selected) return
    setSubmitting(true)
    setError(null)

    try {
      if (mode === 'lease') {
        await post('/fleet/lease', { aircraftTypeId: selected.id, registration })
        toast.success(`Leased a ${selected.name} (${selected.icaoType}) - ${registration}.`)
      } else {
        await post('/fleet/buy', { aircraftTypeId: selected.id, condition, registration })
        toast.success(`Bought a ${condition.toLowerCase()} ${selected.name} (${selected.icaoType}) - ${registration}.`)
      }
      onOpenChange(false)
      onSuccess()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not complete this purchase. Check your connection and try again.')
    } finally {
      setSubmitting(false)
    }
  }

  // Deposit months come from the SAME playstyle-resolved figure LeaseAsync actually charges
  // (AircraftTypeOption.leaseDepositMonths, sourced from economyConfig.AirlineStartup.LeaseDepositMonths)
  // - never a hardcoded "1 month". True-life's deposit is 2 months, not 1; a preview that disagrees
  // with what gets charged is worse than no preview at all.
  const leaseDeposit = selected ? selected.monthlyLeaseRate * selected.leaseDepositMonths : 0
  const leaseDepositMonthsLabel = selected ? formatMonths(selected.leaseDepositMonths) : ''
  const buyPrice = selected ? (condition === 'Used' ? selected.purchasePriceUsed : selected.purchasePriceNew) : 0

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!submitting) onOpenChange(next)
      }}
    >
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle>Add an aircraft</DialogTitle>
          <DialogDescription>Lease it (deposit plus monthly rent) or buy it outright, new or used.</DialogDescription>
        </DialogHeader>

        <Tabs value={mode} onValueChange={(v) => setMode(v as Mode)}>
          <TabsList>
            <TabsTrigger value="lease">Lease</TabsTrigger>
            <TabsTrigger value="buy">Buy</TabsTrigger>
          </TabsList>

          {/* One shared content block switched by `mode`, not per-tab TabsContent panels - the
           *  picker list and summary card are identical in shape between Lease and Buy, only the
           *  figures shown differ, so a single conditional block is simpler than duplicating it. */}
          <div className="mt-2 space-y-3">
            {status === 'ready' && types.length > 0 && (
              <div className="space-y-2">
                <div className="relative">
                  <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
                  <Input
                    value={search}
                    onChange={(e) => setSearch(e.target.value)}
                    placeholder="Search by ICAO type, manufacturer or name…"
                    className="pl-8"
                    aria-label="Search the aircraft catalogue"
                  />
                </div>
                <ToggleGroup<CategoryFilter> value={category} onChange={setCategory} ariaLabel="Filter by size or role" options={CATEGORY_OPTIONS} />
              </div>
            )}

            {status === 'loading' && (
              <div className="space-y-2">
                <Skeleton className="h-12 w-full" />
                <Skeleton className="h-12 w-full" />
                <Skeleton className="h-12 w-full" />
              </div>
            )}

            {status === 'error' && <p className="text-sm text-danger">Could not load the aircraft catalogue.</p>}

            {status === 'ready' && (
              <div className="max-h-64 space-y-2 overflow-y-auto pr-1">
                {filteredTypes.length === 0 && (
                  <p className="py-6 text-center text-sm text-muted-foreground">No aircraft match "{search}".</p>
                )}
                {filteredTypes.map((type) => {
                  const isSelected = selected?.id === type.id
                  return (
                    <button
                      key={type.id}
                      type="button"
                      onClick={() => setSelectedId(type.id)}
                      aria-pressed={isSelected}
                      className={cn(
                        'flex w-full items-center justify-between gap-3 rounded-md border p-3 text-left transition-colors',
                        isSelected ? 'border-accent bg-accent/10' : 'border-border hover:border-accent/40',
                      )}
                    >
                      <div className="min-w-0">
                        <p className="min-w-0 break-words text-sm font-medium">
                          {type.name} <span className="text-muted-foreground">({type.icaoType})</span>
                        </p>
                        <p className="text-xs text-muted-foreground">
                          {type.category} &middot; {type.paxCapacity} seats &middot; {type.rangeNm.toLocaleString()} nm range
                        </p>
                      </div>
                      <div className="shrink-0 text-right text-xs text-muted-foreground">
                        {mode === 'lease' ? (
                          <p className="font-medium text-foreground">{fmt.money(type.monthlyLeaseRate)}/mo</p>
                        ) : (
                          <p className="font-medium text-foreground">
                            {fmt.money(condition === 'Used' ? type.purchasePriceUsed : type.purchasePriceNew)}
                          </p>
                        )}
                      </div>
                    </button>
                  )
                })}
              </div>
            )}

            {selected && mode === 'lease' && (
              <div className="rounded-md border border-border bg-muted/40 p-3 text-sm">
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Deposit ({leaseDepositMonthsLabel})</span>
                  <span className="font-medium tabular-nums">{fmt.money(leaseDeposit)}</span>
                </div>
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Monthly rate</span>
                  <span className="font-medium tabular-nums">{fmt.money(selected.monthlyLeaseRate)}</span>
                </div>
                <p className="mt-2 text-xs text-muted-foreground">A leased aircraft always arrives fresh - 100% condition, zero hours.</p>
              </div>
            )}

            {selected && mode === 'buy' && (
              <div className="space-y-3">
                <ToggleGroup<AircraftCondition>
                  value={condition}
                  onChange={setCondition}
                  ariaLabel="Condition"
                  options={[
                    { value: 'New', label: 'New' },
                    { value: 'Used', label: 'Used' },
                  ]}
                />

                <div className="rounded-md border border-border bg-muted/40 p-3 text-sm">
                  <div className="flex items-center justify-between">
                    <span className="text-muted-foreground">Price</span>
                    <span className="font-medium tabular-nums">{fmt.money(buyPrice)}</span>
                  </div>

                  {condition === 'Used' ? (
                    <>
                      <div className="mt-2 grid grid-cols-2 gap-x-4 gap-y-1 text-xs">
                        <div className="flex items-center justify-between">
                          <span className="text-muted-foreground">Airframe hours</span>
                          <span className="tabular-nums">{Math.round(selected.usedAirframeHours).toLocaleString()}</span>
                        </div>
                        <div className="flex items-center justify-between">
                          <span className="text-muted-foreground">Condition</span>
                          <ConditionBar percent={selected.usedConditionPercent} />
                        </div>
                        <div className="flex items-center justify-between">
                          <span className="text-muted-foreground">Next A-check in</span>
                          <span className="tabular-nums">
                            {Math.round(selected.aCheckIntervalHours - selected.usedHoursSinceACheck).toLocaleString()} h
                          </span>
                        </div>
                        <div className="flex items-center justify-between">
                          <span className="text-muted-foreground">Next C-check in</span>
                          <span className="tabular-nums">
                            {Math.round(selected.cCheckIntervalHours - selected.usedHoursSinceCCheck).toLocaleString()} h
                          </span>
                        </div>
                      </div>
                      <p className="mt-2 text-xs text-muted-foreground">
                        Cheaper to buy, but its next check comes around much sooner - the saving is repaid through the maintenance cycle.
                      </p>
                    </>
                  ) : (
                    <p className="mt-2 text-xs text-muted-foreground">Fresh airframe - 100% condition, zero hours, full price.</p>
                  )}
                </div>
              </div>
            )}

            {selected && (
              <div className="space-y-1.5">
                <label htmlFor="registration" className="text-sm font-medium">
                  Registration
                </label>
                <div className="flex gap-2">
                  <Input
                    id="registration"
                    value={registration}
                    onChange={(e) => {
                      setRegistration(e.target.value.toUpperCase())
                      setRegistrationTouched(true)
                    }}
                    placeholder="e.g. G-EZBA"
                    maxLength={10}
                    className="font-mono uppercase"
                    disabled={suggestionLoading}
                  />
                  <Button type="button" variant="outline" size="icon" onClick={handleRandomise} disabled={suggestionLoading} aria-label="Randomise registration">
                    <Shuffle />
                  </Button>
                </div>
                <p className="text-xs text-muted-foreground">
                  {registrationTouched
                    ? 'Type your own to match a livery, or randomise a fresh suggestion.'
                    : `Suggested for your fleet - edit it or hit randomise for another.`}
                </p>
              </div>
            )}
          </div>
        </Tabs>

        {error && <p className="text-sm text-danger">{error}</p>}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
            Cancel
          </Button>
          <Button onClick={handleSubmit} disabled={!selected || !registration.trim() || submitting || status !== 'ready'}>
            {submitting ? 'Working…' : mode === 'lease' ? 'Lease aircraft' : `Buy ${condition.toLowerCase()}`}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
