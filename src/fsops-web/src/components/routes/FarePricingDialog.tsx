import { lazy, Suspense, useEffect, useMemo, useState } from 'react'
import { Coins, Percent, TrendingUp, Users } from 'lucide-react'
import { toast } from 'sonner'

import { StatTile } from '@/components/shared/StatTile'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { useRoutePricing } from '@/hooks/usePlanning'
import { useSettings } from '@/hooks/useSettings'
import { ApiError, put } from '@/lib/api'
import { fareVerdictSentence } from '@/lib/fareVerdict'
import { cn } from '@/lib/utils'
import type { RouteSummary } from '@/types/route'

const FareCurveChart = lazy(() => import('@/components/routes/FareCurveChart').then((m) => ({ default: m.FareCurveChart })))

interface FarePricingDialogProps {
  /** The leg whose fare is being set, or null when the dialog is closed. */
  route: RouteSummary | null
  /** The other half of the pair, if it exists - a route is always a there-and-back, and both legs
   *  are created sharing a fare, so changing one and not the other is almost never what was meant. */
  returnRoute: RouteSummary | null
  onClose: () => void
  onSaved: () => void
}

const DEBOUNCE_MS = 250

/**
 * The fare workbench: set a fare on a saved route, and see what it does BEFORE committing.
 *
 * <p>Every figure comes from GET /routes/{id}/pricing, which runs the same projection
 * FlightEconomicsPoster posts from - so what this shows is what the ledger will hold. Money arrives
 * in the app's base unit and is rendered only through `fmt.money`; the fare the player types is in
 * their own currency and is converted back to base units exactly once, on save.</p>
 */
export function FarePricingDialog({ route, returnRoute, onClose, onSaved }: FarePricingDialogProps) {
  const { fmt, currentCurrency } = useSettings()

  const [fareText, setFareText] = useState('')
  const [applyToReturn, setApplyToReturn] = useState(true)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  // Reset to the route's own fare whenever a different leg is opened.
  useEffect(() => {
    if (!route) return
    setFareText((route.baseFare * currentCurrency.rate).toFixed(currentCurrency.decimalPlaces))
    setApplyToReturn(true)
    setSaveError(null)
  }, [route, currentCurrency])

  const parsedFare = Number(fareText)
  const fareInBase =
    Number.isFinite(parsedFare) && parsedFare > 0 ? parsedFare / currentCurrency.rate : null
  const debouncedFare = useDebouncedValue(fareInBase, DEBOUNCE_MS)

  const pricing = useRoutePricing(route?.id ?? null, debouncedFare)
  const data = pricing.data

  const priceable = data?.priceable === true ? data : null

  const bandHint = useMemo(() => {
    if (!priceable) return null
    return `${fmt.money(priceable.fareBand.minimum)} – ${fmt.money(priceable.fareBand.maximum)}`
  }, [priceable, fmt])

  function setFareFromBase(baseAmount: number) {
    setFareText((baseAmount * currentCurrency.rate).toFixed(currentCurrency.decimalPlaces))
  }

  async function save() {
    if (!route || fareInBase === null) return
    setSaving(true)
    setSaveError(null)
    try {
      await put(`/routes/${route.id}`, { baseFare: fareInBase })
      if (applyToReturn && returnRoute) {
        await put(`/routes/${returnRoute.id}`, { baseFare: fareInBase })
      }
      toast.success(
        applyToReturn && returnRoute
          ? `Fare set to ${fmt.money(fareInBase)} on both legs of ${route.departureIcao} ⇄ ${route.arrivalIcao}.`
          : `Fare set to ${fmt.money(fareInBase)} on ${route.departureIcao} → ${route.arrivalIcao}.`,
      )
      onSaved()
      onClose()
    } catch (err) {
      setSaveError(err instanceof ApiError ? err.message : 'Could not save this fare. Check your connection and try again.')
    } finally {
      setSaving(false)
    }
  }

  const profit = priceable?.atFare.profit ?? 0
  const showSkeleton = pricing.status === 'loading' && !data

  return (
    <Dialog open={route !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[90vh] max-w-3xl overflow-y-auto">
        <DialogHeader>
          <DialogTitle>
            Set the fare{route ? ` — ${route.departureIcao} → ${route.arrivalIcao}` : ''}
          </DialogTitle>
          <DialogDescription>
            Raising a fare trades passengers for yield. Everything below is what this sector would actually
            book and earn at the fare you pick, using the same figures the ledger posts.
          </DialogDescription>
        </DialogHeader>

        {showSkeleton && (
          <div className="space-y-3">
            <Skeleton className="h-24 w-full" />
            <Skeleton className="h-60 w-full" />
          </div>
        )}

        {pricing.status === 'error' && !data && (
          <p className="text-sm text-danger">Could not price this route. Check your connection and try again.</p>
        )}

        {data && data.priceable === false && (
          <p className="text-sm text-warning">{data.reason}</p>
        )}

        {priceable && (
          <div className={cn('space-y-4 transition-opacity duration-200', pricing.isRefreshing && 'opacity-60')}>
            <div className="flex flex-wrap items-end gap-4">
              <div className="space-y-1.5">
                <Label htmlFor="route-fare">Fare per passenger</Label>
                <div className="relative w-40">
                  {currentCurrency.symbolBefore && (
                    <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-sm text-muted-foreground">
                      {currentCurrency.symbol}
                    </span>
                  )}
                  <Input
                    id="route-fare"
                    type="number"
                    min={0}
                    step="0.01"
                    inputMode="decimal"
                    value={fareText}
                    onChange={(event) => setFareText(event.target.value)}
                    className={currentCurrency.symbolBefore ? 'pl-7' : 'pr-7'}
                  />
                  {!currentCurrency.symbolBefore && (
                    <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-sm text-muted-foreground">
                      {currentCurrency.symbol}
                    </span>
                  )}
                </div>
                {bandHint && <p className="text-xs text-muted-foreground">Allowed: {bandHint}</p>}
              </div>

              <div className="flex flex-wrap gap-2">
                <Button type="button" variant="outline" size="sm" onClick={() => setFareFromBase(priceable.referenceFare)}>
                  Suggested {fmt.money(priceable.referenceFare)}
                </Button>
                <Button type="button" variant="outline" size="sm" onClick={() => setFareFromBase(priceable.bestSampledProfitFare)}>
                  Best profit {fmt.money(priceable.bestSampledProfitFare)}
                </Button>
                <Button type="button" variant="outline" size="sm" onClick={() => setFareFromBase(priceable.revenueMaximizingFare)}>
                  Most revenue {fmt.money(priceable.revenueMaximizingFare)}
                </Button>
              </div>
            </div>

            <div className="grid grid-cols-[repeat(auto-fit,minmax(9.5rem,1fr))] gap-3">
              <StatTile label="Passengers" icon={Users} value={`${priceable.atFare.paxBooked} of ${priceable.atFare.seats}`} />
              <StatTile label="Load factor" icon={Percent} value={`${priceable.atFare.loadFactorPercent.toFixed(1)}%`} />
              <StatTile label="Revenue per sector" icon={Coins} value={fmt.money(priceable.atFare.revenue)} />
              <StatTile
                label="Profit per sector"
                icon={TrendingUp}
                value={fmt.money(profit)}
                trend={{
                  direction: profit > 0 ? 'up' : profit < 0 ? 'down' : 'flat',
                  label: `after ${fmt.money(priceable.atFare.cost)} of costs`,
                }}
              />
            </div>

            <p className="rounded-md border border-border bg-muted/20 p-3 text-sm">
              {fareVerdictSentence(priceable.verdict, fmt.money)}
            </p>

            <div className="rounded-md border border-border p-3">
              <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
                <p className="text-sm font-medium">What every other fare would do</p>
                <p className="text-xs text-muted-foreground">
                  Market today: about {priceable.marketDemandPax} passengers a day on this pair
                </p>
              </div>
              <Suspense fallback={<Skeleton className="h-[240px] w-full" />}>
                <FareCurveChart
                  points={priceable.curve}
                  currentFare={priceable.atFare.fare}
                  referenceFare={priceable.referenceFare}
                  bestProfitFare={priceable.bestSampledProfitFare}
                  formatMoney={fmt.money}
                />
              </Suspense>
            </div>

            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <Badge variant="outline">{priceable.assumedAircraft.typeName}</Badge>
              <span>{priceable.assumedAircraft.basis}</span>
              {!priceable.assumedAircraft.canOperate && (
                <span className="text-warning">This aircraft can&rsquo;t actually operate this route.</span>
              )}
            </div>

            {returnRoute && (
              <label className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  className="size-4 rounded border-border accent-accent"
                  checked={applyToReturn}
                  onChange={(event) => setApplyToReturn(event.target.checked)}
                />
                Also set the return leg ({returnRoute.departureIcao} → {returnRoute.arrivalIcao})
              </label>
            )}

            {saveError && <p className="text-sm text-danger">{saveError}</p>}
          </div>
        )}

        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button type="button" onClick={save} disabled={saving || fareInBase === null || !priceable}>
            {saving ? 'Saving…' : 'Save fare'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
