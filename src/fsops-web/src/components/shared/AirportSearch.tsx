import { useCallback, useEffect, useId, useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import { AlertTriangle, Search, X } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import type { BadgeProps } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { ApiError, get } from '@/lib/api'
import { cn } from '@/lib/utils'
import type { AirportSizeCategory, AirportSummary } from '@/types/airport'

const MIN_QUERY_LENGTH = 2
const DEBOUNCE_MS = 250
const RESULT_LIMIT = 20

const SIZE_BADGE_VARIANT: Record<AirportSizeCategory, NonNullable<BadgeProps['variant']>> = {
  Large: 'success',
  Medium: 'warning',
  Small: 'muted',
  Heliport: 'secondary',
  Seaplane: 'outline',
  Closed: 'danger',
}

const RUNWAY_FORMATTER = new Intl.NumberFormat('en-US')

type SearchStatus = 'idle' | 'loading' | 'success' | 'empty' | 'error'

function describeError(err: unknown): string {
  return err instanceof ApiError ? err.message : 'Could not search airports. Check your connection and try again.'
}

interface AirportSearchProps {
  onSelect: (airport: AirportSummary) => void
  placeholder?: string
  autoFocus?: boolean
  className?: string
}

export function AirportSearch({ onSelect, placeholder, autoFocus, className }: AirportSearchProps) {
  const listId = useId()
  const inputRef = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLUListElement>(null)

  const [rawQuery, setRawQuery] = useState('')
  const [focused, setFocused] = useState(false)
  const [status, setStatus] = useState<SearchStatus>('idle')
  const [results, setResults] = useState<AirportSummary[]>([])
  const [errorMessage, setErrorMessage] = useState('')
  const [activeIndex, setActiveIndex] = useState(-1)
  const [retryToken, setRetryToken] = useState(0)

  const trimmedQuery = rawQuery.trim()
  const debouncedQuery = useDebouncedValue(trimmedQuery, DEBOUNCE_MS)

  useEffect(() => {
    if (debouncedQuery.length < MIN_QUERY_LENGTH) {
      setStatus('idle')
      setResults([])
      setActiveIndex(-1)
      return
    }

    const controller = new AbortController()
    setStatus('loading')
    setErrorMessage('')

    get<AirportSummary[]>('/airports/search', { q: debouncedQuery, limit: RESULT_LIMIT }, { signal: controller.signal })
      .then((airports) => {
        setResults(airports)
        setActiveIndex(-1)
        setStatus(airports.length > 0 ? 'success' : 'empty')
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return
        setResults([])
        setActiveIndex(-1)
        setStatus('error')
        setErrorMessage(describeError(err))
      })

    return () => controller.abort()
  }, [debouncedQuery, retryToken])

  const clear = useCallback(() => {
    setRawQuery('')
    setResults([])
    setStatus('idle')
    setActiveIndex(-1)
  }, [])

  const select = useCallback(
    (airport: AirportSummary) => {
      onSelect(airport)
      clear()
      inputRef.current?.blur()
    },
    [onSelect, clear],
  )

  useEffect(() => {
    if (activeIndex < 0) return
    listRef.current?.querySelector<HTMLLIElement>(`[data-index="${activeIndex}"]`)?.scrollIntoView({ block: 'nearest' })
  }, [activeIndex])

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Escape') {
      if (rawQuery) {
        event.preventDefault()
        clear()
      } else {
        inputRef.current?.blur()
      }
      return
    }

    if (status !== 'success' || results.length === 0) return

    if (event.key === 'ArrowDown') {
      event.preventDefault()
      setActiveIndex((prev) => Math.min(prev + 1, results.length - 1))
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      setActiveIndex((prev) => Math.max(prev - 1, 0))
    } else if (event.key === 'Enter') {
      const airport = activeIndex >= 0 ? results[activeIndex] : undefined
      if (airport) {
        event.preventDefault()
        select(airport)
      }
    }
  }

  const isPanelOpen = focused && trimmedQuery.length >= MIN_QUERY_LENGTH
  const activeOptionId = activeIndex >= 0 ? `${listId}-option-${activeIndex}` : undefined
  const helperText =
    trimmedQuery.length === 0
      ? 'Search by ICAO, IATA, or airport name — e.g. EGLL, LAX, Heathrow.'
      : trimmedQuery.length < MIN_QUERY_LENGTH
        ? `Keep typing — ${MIN_QUERY_LENGTH} characters minimum.`
        : null

  return (
    <div className={cn('relative', className)}>
      <div className="relative">
        <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          ref={inputRef}
          role="combobox"
          aria-expanded={isPanelOpen}
          aria-controls={listId}
          aria-activedescendant={activeOptionId}
          aria-autocomplete="list"
          aria-label="Search airports"
          autoComplete="off"
          autoFocus={autoFocus}
          placeholder={placeholder ?? 'Search by ICAO, IATA, or name…'}
          value={rawQuery}
          onChange={(event) => setRawQuery(event.target.value)}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
          onKeyDown={handleKeyDown}
          className="pl-9 pr-9"
        />
        {rawQuery && (
          <button
            type="button"
            aria-label="Clear search"
            className="absolute right-2 top-1/2 flex size-6 -translate-y-1/2 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
            onMouseDown={(event) => event.preventDefault()}
            onClick={() => {
              clear()
              inputRef.current?.focus()
            }}
          >
            <X className="size-4" />
          </button>
        )}
      </div>

      {helperText && <p className="mt-1.5 text-xs text-muted-foreground">{helperText}</p>}

      {isPanelOpen && (
        <div className="absolute z-20 mt-1.5 w-full overflow-hidden rounded-md border border-border bg-surface-elevated shadow-elevation-3">
          {status === 'loading' && (
            <div className="space-y-2 p-3">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
            </div>
          )}

          {status === 'error' && (
            <div className="flex flex-col items-center gap-2 px-4 py-6 text-center">
              <AlertTriangle className="size-5 text-danger" />
              <p className="text-sm text-foreground">{errorMessage}</p>
              <Button
                type="button"
                variant="outline"
                size="sm"
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => setRetryToken((token) => token + 1)}
              >
                Try again
              </Button>
            </div>
          )}

          {status === 'empty' && (
            <p className="px-4 py-6 text-center text-sm text-muted-foreground">
              No airports match &ldquo;{debouncedQuery}&rdquo;.
            </p>
          )}

          {status === 'success' && (
            <ul
              ref={listRef}
              id={listId}
              role="listbox"
              aria-label="Airport search results"
              className="max-h-80 overflow-y-auto py-1 scrollbar-thin"
            >
              {results.map((airport, index) => (
                <li key={airport.icao} data-index={index} role="option" id={`${listId}-option-${index}`} aria-selected={index === activeIndex}>
                  <button
                    type="button"
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={() => select(airport)}
                    onMouseEnter={() => setActiveIndex(index)}
                    className={cn(
                      'flex w-full items-center gap-3 px-3 py-2 text-left transition-colors',
                      index === activeIndex ? 'bg-accent/15' : 'hover:bg-muted',
                    )}
                  >
                    <span className="w-16 shrink-0 font-mono text-sm font-semibold tracking-tight">{airport.icao}</span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm font-medium">{airport.name}</span>
                      <span className="block truncate text-xs text-muted-foreground">
                        {[airport.municipality, airport.country].filter(Boolean).join(', ') || '—'}
                      </span>
                    </span>
                    <span className="flex shrink-0 flex-col items-end gap-1">
                      <Badge variant={SIZE_BADGE_VARIANT[airport.sizeCategory]}>{airport.sizeCategory}</Badge>
                      {airport.longestRunwayFt !== null && (
                        <span className="text-xs tabular-nums text-muted-foreground">
                          {RUNWAY_FORMATTER.format(airport.longestRunwayFt)} ft
                        </span>
                      )}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}
