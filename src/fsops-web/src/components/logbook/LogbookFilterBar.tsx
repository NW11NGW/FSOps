import { Search } from 'lucide-react'

import { Input } from '@/components/ui/input'
import { ToggleGroup } from '@/components/shared/ToggleGroup'
import type { LogbookFilters } from '@/lib/logbook'
import { cn } from '@/lib/utils'

interface LogbookFilterBarProps {
  filters: LogbookFilters
  onChange: (next: LogbookFilters) => void
  /** How many sectors match right now, out of how many are loaded - shown so a filter that hides
   *  everything reads as a filter rather than as an empty logbook. */
  matching: number
  loaded: number
}

const STATUS_OPTIONS = [
  { value: 'all', label: 'All' },
  { value: 'Completed', label: 'Completed' },
  { value: 'Abandoned', label: 'Abandoned' },
  { value: 'Interrupted', label: 'Interrupted' },
] as const

const FLOWN_BY_OPTIONS = [
  { value: 'all', label: 'Everyone' },
  { value: 'mine', label: 'Flown by me' },
  { value: 'crew', label: 'Flown by crew' },
] as const

export function LogbookFilterBar({ filters, onChange, matching, loaded }: LogbookFilterBarProps) {
  return (
    <div className="flex flex-wrap items-center gap-3">
      <div className="relative min-w-[200px] flex-1">
        <Search aria-hidden className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          type="search"
          value={filters.query}
          onChange={(event) => onChange({ ...filters, query: event.target.value })}
          placeholder="Airport, flight number, registration or pilot"
          aria-label="Search the logbook"
          className="pl-9"
        />
      </div>

      <ToggleGroup
        ariaLabel="Filter by status"
        value={filters.status}
        onChange={(status) => onChange({ ...filters, status })}
        options={STATUS_OPTIONS.map((option) => ({ value: option.value, label: option.label }))}
      />

      <ToggleGroup
        ariaLabel="Filter by who was flying"
        value={filters.flownBy}
        onChange={(flownBy) => onChange({ ...filters, flownBy })}
        options={FLOWN_BY_OPTIONS.map((option) => ({ value: option.value, label: option.label }))}
      />

      <button
        type="button"
        role="switch"
        aria-checked={filters.withTrackOnly}
        onClick={() => onChange({ ...filters, withTrackOnly: !filters.withTrackOnly })}
        className={cn(
          'rounded-md border px-3 py-1.5 text-sm font-medium transition-colors',
          filters.withTrackOnly
            ? 'border-accent bg-accent/15 text-accent'
            : 'border-border text-muted-foreground hover:text-foreground',
        )}
      >
        With flown track
      </button>

      <p className="text-xs text-muted-foreground" aria-live="polite">
        {matching === loaded ? `${loaded} sectors` : `${matching} of ${loaded} sectors`}
      </p>
    </div>
  )
}
