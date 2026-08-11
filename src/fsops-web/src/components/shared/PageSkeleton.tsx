import { Skeleton } from '@/components/ui/skeleton'

/**
 * Suspense fallback for a lazily-loaded route. Shown for the brief window between navigating to
 * a page and its chunk finishing download/parse - mirrors the shape every page already settles
 * into (PageHeader + card-ish content) so the swap-in doesn't cause a layout jump, and reuses the
 * same Skeleton/tokens pages use for their own loading states (see Dashboard's reputation card).
 */
export function PageSkeleton() {
  return (
    <div>
      <div className="mb-6 flex items-start justify-between gap-4">
        <div className="space-y-2">
          <Skeleton className="h-6 w-40" />
          <Skeleton className="h-4 w-64" />
        </div>
      </div>
      <div className="space-y-4">
        <Skeleton className="h-24 w-full rounded-lg" />
        <Skeleton className="h-64 w-full rounded-lg" />
      </div>
    </div>
  )
}
