import { CalendarClock, Gauge, Layers, Plane, Route as RouteIcon } from 'lucide-react'

import { ContractLegChain, ContractLegPips } from '@/components/contracts/ContractLegChain'
import { contractEndpoints, contractScale, isExpedition, kindHeadline, kindStyle } from '@/components/contracts/contractKind'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { useSettings } from '@/hooks/useSettings'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { Contract } from '@/types/contract'

interface ContractCardProps {
  contract: Contract
  /** Rendered in the card's footer - "Accept", "Open", whatever the caller's context calls for. */
  action?: React.ReactNode
  /** Board offers summarise the chain; an accepted job shows it in full. */
  expanded?: boolean
  onOpen?: () => void
}

function Fact({
  icon: Icon,
  label,
  value,
  emphasis,
}: {
  icon: typeof Plane
  label: string
  value: string
  emphasis?: boolean
}) {
  return (
    <div className="min-w-0">
      <p className="flex items-center gap-1.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
        <Icon className="size-3 shrink-0" />
        <span className="truncate">{label}</span>
      </p>
      <p className={cn('mt-0.5 break-words text-sm tabular-nums', emphasis ? 'font-semibold text-foreground' : 'text-foreground')}>
        {value}
      </p>
    </div>
  )
}

/**
 * One job on the board.
 *
 * <p><b>The whole design problem here is honest scale.</b> A forty-minute domestic hop and an
 * eleven-leg crossing of the North Atlantic are both "a contract", and a board that renders them as
 * two identical rows throws away the only thing that makes this feature worth having. So three things
 * vary with the size of the job rather than being fixed furniture: the leg pips show the real chain
 * length, the scale word names the commitment in plain English, and a job of four legs or more gets
 * its <b>full chain of stops shown inline</b> - because for those the chain is not a detail of the
 * offer, it is the offer.</p>
 *
 * <p>Kind is carried by the coloured left edge, the icon and the badge, and reinforced by which fact
 * the card leads with - freight weight for cargo, seats filled for a charter, legs and distance for a
 * ferry. Nothing here re-derives a server rule: the ordering, the fee shares and the next leg all
 * arrive decided.</p>
 */
export function ContractCard({ contract, action, expanded = false, onOpen }: ContractCardProps) {
  const { fmt } = useSettings()
  const style = kindStyle(contract.kind)
  const Icon = style.icon
  const ends = contractEndpoints(contract)
  const scale = contractScale(contract)
  const big = isExpedition(contract)
  // Anything with more than one leg shows its chain. The player is agreeing to fly all of them in
  // order, so hiding them behind a click would mean accepting a job without having been told what it
  // is. A single-sector job needs no chain - the route line above already is the chain.
  const showChain = expanded || contract.legCount > 1

  const inProgress = contract.status === 'Accepted' && contract.flownLegCount > 0

  return (
    <Card
      className={cn(
        'overflow-hidden border-l-4 transition-shadow',
        style.stripe,
        onOpen && 'cursor-pointer hover:shadow-elevation-2',
      )}
      onClick={onOpen}
      {...(onOpen
        ? {
            role: 'button',
            tabIndex: 0,
            onKeyDown: (event: React.KeyboardEvent) => {
              if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault()
                onOpen()
              }
            },
          }
        : {})}
    >
      <CardContent className="space-y-4 p-5">
        {/* Kind, operator and the commitment, before any numbers. */}
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0 space-y-1.5">
            <div className="flex flex-wrap items-center gap-2">
              <Badge className={cn('border-transparent', style.chip)}>
                <Icon className="size-3" />
                {style.label}
              </Badge>
              <Badge variant="outline">{scale}</Badge>
              {contract.status === 'Accepted' && (
                <Badge variant="success">{inProgress ? 'In progress' : 'Accepted'}</Badge>
              )}
            </div>
            <p className="truncate text-sm text-muted-foreground">{contract.operatorName}</p>
          </div>

          {/* The fee is what the legs sum to; the bonus is only paid for finishing. Shown as two
           *  lines rather than one total, because they are won and lost differently - the legs pay as
           *  you fly them and are yours, the bonus is all-or-nothing. */}
          <div className="text-right">
            <p className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
              {contract.completionBonus > 0 ? 'Legs pay' : 'Fee'}
            </p>
            <p className="text-xl font-semibold tabular-nums tracking-tight">{fmt.money(contract.fee)}</p>
            {contract.completionBonus > 0 && (
              <p className="mt-1 text-xs text-success">
                +{fmt.money(contract.completionBonus)} on finishing
              </p>
            )}
          </div>
        </div>

        {/* The route, big, because it is what the player is choosing between. */}
        <div>
          <p className="font-mono text-lg font-semibold tracking-tight">
            {ends ? (
              <>
                {ends.from} <span className="text-muted-foreground">→</span> {ends.to}
              </>
            ) : (
              'Route unavailable'
            )}
          </p>
          <ContractLegPips contract={contract} className="mt-2" />
        </div>

        <div className="grid grid-cols-2 gap-x-4 gap-y-3 sm:grid-cols-4">
          <Fact
            icon={Layers}
            label={contract.legCount === 1 ? 'Leg' : 'Legs'}
            value={
              contract.status === 'Accepted'
                ? `${contract.flownLegCount} of ${contract.legCount} flown`
                : String(contract.legCount)
            }
            emphasis={big}
          />
          <Fact icon={RouteIcon} label="Distance" value={fmt.distance(contract.totalDistanceNm)} emphasis={big} />
          <Fact icon={Gauge} label="Block time" value={fmt.duration(contract.totalPlannedBlockMinutes)} />
          <Fact
            icon={Plane}
            label="Aircraft"
            value={contract.aircraft.name ?? contract.aircraft.typeDesignator}
          />
        </div>

        {/* What the job actually is, in the server's own words. */}
        <div className="rounded-md border border-border bg-muted/40 p-3">
          <p className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
            {kindHeadline(contract)}
          </p>
          <p className="mt-0.5 text-sm">{contract.loadDescription}</p>
        </div>

        {showChain && (
          <div className="rounded-md border border-border p-3">
            <p className="mb-3 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
              {contract.legCount === 1 ? 'The sector' : `The chain — ${contract.legCount} legs, flown in order`}
            </p>
            <ContractLegChain contract={contract} />
          </div>
        )}

        {contract.status === 'Accepted' && (
          <div className="space-y-2 border-t border-border pt-3 text-sm">
            <div className="flex flex-wrap items-center justify-between gap-2">
              {/* Banked, from the ledger - not the value of the legs marked flown. Those differ
               *  whenever a leg was completed with estimates or invalidated. */}
              <span className="text-muted-foreground">
                Earned so far <span className="font-medium tabular-nums text-success">{fmt.money(contract.earnedSoFar)}</span>
              </span>
              <span className="text-muted-foreground">
                Still to earn <span className="font-medium tabular-nums text-foreground">{fmt.money(contract.outstandingFee)}</span>
              </span>
            </div>
            {contract.completionBonus > 0 && (
              <p className="text-xs text-muted-foreground">
                <span className="font-medium text-success">{fmt.money(contract.completionBonus)}</span> bonus
                waiting on the last leg — you lose it if you hand the job back.
              </p>
            )}
          </div>
        )}

        <div className="flex flex-wrap items-center justify-between gap-3 pt-1">
          <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
            <CalendarClock className="size-3.5 shrink-0" />
            {/* Shown before accepting and never recalculated afterwards, so it cannot move under them. */}
            Deadline {formatDate(contract.deadlineUtc)}
          </p>
          {action && <div onClick={(e) => e.stopPropagation()}>{action}</div>}
        </div>
      </CardContent>
    </Card>
  )
}

/** The board's own legend - three sentences saying what the three kinds of job are. Shown once, above
 *  the offers, because "Ferry" means nothing until somebody explains that it is several legs over
 *  several sessions. */
export function ContractKindLegend() {
  return (
    <div className="grid gap-3 sm:grid-cols-3">
      {(['Ferry', 'Cargo', 'Charter'] as const).map((kind) => {
        const style = kindStyle(kind)
        const Icon = style.icon
        return (
          <div key={kind} className={cn('rounded-md border border-l-4 border-border p-3', style.stripe)}>
            <p className={cn('flex items-center gap-1.5 text-sm font-medium', style.text)}>
              <Icon className="size-4 shrink-0" />
              {style.label}
            </p>
            <p className="mt-1 text-xs text-muted-foreground">{style.blurb}</p>
          </div>
        )
      })}
    </div>
  )
}

/** Kept out of the card so the card never fetches or mutates on its own behalf. */
export function ContractAcceptButton({
  onAccept,
  disabled,
}: {
  onAccept: () => void
  disabled?: boolean
}) {
  return (
    <Button type="button" onClick={onAccept} disabled={disabled}>
      Accept job
    </Button>
  )
}
