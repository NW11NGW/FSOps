import { cn } from '@/lib/utils'

interface ToggleOption<T extends string> {
  value: T
  label: string
}

interface ToggleGroupProps<T extends string> {
  value: T
  onChange: (value: T) => void
  options: ToggleOption<T>[]
  ariaLabel: string
}

/** A small segmented control for two-to-few mutually exclusive options (units, time format, theme). */
export function ToggleGroup<T extends string>({ value, onChange, options, ariaLabel }: ToggleGroupProps<T>) {
  return (
    <div role="radiogroup" aria-label={ariaLabel} className="inline-flex rounded-md border border-border bg-muted p-1">
      {options.map((option) => {
        const selected = option.value === value
        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={selected}
            onClick={() => onChange(option.value)}
            className={cn(
              'rounded-sm px-3 py-1.5 text-sm font-medium transition-colors',
              selected ? 'bg-surface text-foreground shadow-elevation-1' : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {option.label}
          </button>
        )
      })}
    </div>
  )
}
