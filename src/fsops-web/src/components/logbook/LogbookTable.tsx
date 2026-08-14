import { ArrowDown, ArrowUp, ChevronsUpDown, MapPin, RadioTower } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useSettings } from '@/hooks/useSettings'
import { formatCallsign } from '@/lib/callsign'
import { assessLanding } from '@/lib/flightFormat'
import { blockDeltaMinutes, type LogbookSortKey, type SortDirection } from '@/lib/logbook'
import { cn } from '@/lib/utils'
import type { FlightStatus, LogbookSector } from '@/types/flight'

interface LogbookTableProps {
  sectors: LogbookSector[]
  sortKey: LogbookSortKey
  sortDirection: SortDirection
  onSort: (key: LogbookSortKey) => void
  onOpen: (sector: LogbookSector) => void
  airlineIcaoCode: string | null
}

const STATUS_BADGE: Record<FlightStatus, 'success' | 'muted' | 'warning' | 'danger' | 'secondary'> = {
  Completed: 'success',
  InProgress: 'secondary',
  Interrupted: 'warning',
  Abandoned: 'danger',
  Planned: 'muted',
}

const DATE_FORMATTER = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: '2-digit' })

interface SortableHeadProps {
  label: string
  sortKeyValue: LogbookSortKey
  activeKey: LogbookSortKey
  direction: SortDirection
  onSort: (key: LogbookSortKey) => void
  className?: string
}

function SortableHead({ label, sortKeyValue, activeKey, direction, onSort, className }: SortableHeadProps) {
  const active = activeKey === sortKeyValue
  const Icon = !active ? ChevronsUpDown : direction === 'asc' ? ArrowUp : ArrowDown
  return (
    <TableHead className={className}>
      <button
        type="button"
        onClick={() => onSort(sortKeyValue)}
        // aria-sort belongs on the header cell, but the button is what announces the action - both
        // are set so the column's state and the control's purpose are each described properly.
        aria-label={`Sort by ${label}`}
        className="inline-flex items-center gap-1 rounded-sm text-left transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        {label}
        <Icon className={cn('size-3', active ? 'text-accent' : 'text-muted-foreground/60')} />
      </button>
    </TableHead>
  )
}

/** Signed minutes against plan, coloured only where it is genuinely good or bad news. */
function BlockDelta({ sector }: { sector: LogbookSector }) {
  const delta = blockDeltaMinutes(sector)
  if (delta === null) {
    return (
      <span className="text-xs text-muted-foreground" title={sector.blockTimeNotMeasured ? 'Time acceleration was used on this sector, so elapsed wall time means nothing.' : 'This sector never recorded both an out and an in time.'}>
        Not measured
      </span>
    )
  }
  if (Math.abs(delta) < 1) return <span className="text-muted-foreground">On plan</span>
  return (
    <span className={delta < 0 ? 'text-success' : 'text-warning'}>
      {delta > 0 ? '+' : '−'}
      {Math.abs(Math.round(delta))}m
    </span>
  )
}

/**
 * The logbook itself: one row per sector flown, sortable by every column that means anything, and
 * clickable through to that flight's full record including the path it actually flew.
 *
 * Deliberately shows the sector's NET rather than its revenue: revenue alone flatters a long sector
 * that burned its earnings in fuel, and net is the figure the flight's own report card shows, so
 * clicking a row never contradicts the row.
 */
export function LogbookTable({ sectors, sortKey, sortDirection, onSort, onOpen, airlineIcaoCode }: LogbookTableProps) {
  const { fmt } = useSettings()

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <SortableHead label="Date" sortKeyValue="date" activeKey={sortKey} direction={sortDirection} onSort={onSort} />
          <SortableHead label="Route" sortKeyValue="route" activeKey={sortKey} direction={sortDirection} onSort={onSort} />
          <SortableHead label="Aircraft" sortKeyValue="aircraft" activeKey={sortKey} direction={sortDirection} onSort={onSort} />
          <TableHead>Pilot</TableHead>
          <TableHead>Block</TableHead>
          <SortableHead label="vs plan" sortKeyValue="blockDelta" activeKey={sortKey} direction={sortDirection} onSort={onSort} />
          <SortableHead label="Landing" sortKeyValue="landing" activeKey={sortKey} direction={sortDirection} onSort={onSort} />
          <TableHead>Load</TableHead>
          <SortableHead label="Net" sortKeyValue="net" activeKey={sortKey} direction={sortDirection} onSort={onSort} className="text-right" />
        </TableRow>
      </TableHeader>
      <TableBody>
        {sectors.map((sector) => {
          const callsign = formatCallsign(airlineIcaoCode, sector.flightNumber)
          const landing = sector.landingFpmFirst !== null ? assessLanding(sector.landingFpmFirst) : null

          return (
            <TableRow
              key={sector.flightId}
              tabIndex={0}
              role="button"
              aria-label={`${sector.departureIcao} to ${sector.arrivalIcao} on ${DATE_FORMATTER.format(new Date(sector.dateUtc))}`}
              className="cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
              onClick={() => onOpen(sector)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' || event.key === ' ') {
                  event.preventDefault()
                  onOpen(sector)
                }
              }}
            >
              <TableCell className="whitespace-nowrap text-xs text-muted-foreground">
                {DATE_FORMATTER.format(new Date(sector.dateUtc))}
              </TableCell>

              <TableCell>
                <div className="flex items-center gap-2">
                  <span className="font-mono">
                    {sector.departureIcao || '—'} → {sector.arrivalIcao || '—'}
                  </span>
                  {sector.hasTrack && (
                    <MapPin
                      className="size-3 shrink-0 text-accent"
                      aria-label="Flown track recorded"
                    />
                  )}
                  {sector.vatsimOnline === true && (
                    <RadioTower className="size-3 shrink-0 text-success" aria-label="Flown online" />
                  )}
                </div>
                {callsign && <span className="text-xs text-muted-foreground">{callsign}</span>}
              </TableCell>

              <TableCell className="text-xs">
                <p className="font-medium text-foreground">{sector.registration ?? '—'}</p>
                <p className="text-muted-foreground">{sector.aircraftIcaoType ?? sector.aircraftTypeName ?? ''}</p>
              </TableCell>

              <TableCell className="text-xs">
                <span className={sector.isPlayerFlight ? 'font-medium text-foreground' : 'text-muted-foreground'}>
                  {sector.pilotName ?? '—'}
                </span>
              </TableCell>

              <TableCell className="whitespace-nowrap tabular-nums">
                {sector.actualBlockMinutes === null ? '—' : fmt.duration(sector.actualBlockMinutes)}
              </TableCell>

              <TableCell className="whitespace-nowrap text-xs tabular-nums">
                <BlockDelta sector={sector} />
              </TableCell>

              <TableCell className="whitespace-nowrap tabular-nums">
                {landing ? (
                  <span className={landing.verdict === 'smooth' ? 'text-success' : landing.verdict === 'firm' ? 'text-warning' : 'text-danger'}>
                    {Math.round(landing.displayFpm)} fpm
                  </span>
                ) : (
                  <span className="text-xs text-muted-foreground">Not measured</span>
                )}
              </TableCell>

              <TableCell className="whitespace-nowrap tabular-nums text-xs">
                {sector.loadFactorPercent === null ? (
                  <span className="text-muted-foreground">—</span>
                ) : (
                  <>
                    {sector.loadFactorPercent.toFixed(0)}%
                    <span className="ml-1 text-muted-foreground">
                      ({sector.paxFlown}/{sector.seats})
                    </span>
                  </>
                )}
              </TableCell>

              <TableCell className="whitespace-nowrap text-right tabular-nums">
                <span className={cn('font-medium', sector.net >= 0 ? 'text-success' : 'text-danger')}>
                  {sector.net < 0 ? '-' : ''}
                  {fmt.money(Math.abs(sector.net))}
                </span>
                {sector.status !== 'Completed' && (
                  <div className="mt-1">
                    <Badge variant={STATUS_BADGE[sector.status]}>{sector.status}</Badge>
                  </div>
                )}
              </TableCell>
            </TableRow>
          )
        })}
      </TableBody>
    </Table>
  )
}
