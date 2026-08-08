import { TriangleAlert } from 'lucide-react'

interface ConflictListProps {
  error: string
  conflicts: string[]
}

/**
 * Renders the backend's own conflict sentences verbatim - never re-worded, truncated, or
 * summarised. Each one is already a complete explanation - e.g. "G-OLAF lands at EGPF ... you'd
 * need to create a EGPF -> EGSS route on the Routes page for this chain to work" when the route
 * doesn't exist yet, or "...schedule a EGPF -> EGSS leg before this one to reposition it" when it
 * already exists but nothing is scheduled on it - so this component's only job is to make them
 * impossible to miss.
 */
export function ConflictList({ error, conflicts }: ConflictListProps) {
  return (
    <div className="space-y-2 rounded-md border border-danger/30 bg-danger/10 p-3">
      <p className="flex items-start gap-2 text-sm font-medium text-danger">
        <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
        <span className="min-w-0 break-words">{error}</span>
      </p>
      {conflicts.length > 0 && (
        <ul className="ml-6 list-disc space-y-1 text-sm text-danger">
          {conflicts.map((conflict, index) => (
            <li key={index} className="min-w-0 break-words">
              {conflict}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
