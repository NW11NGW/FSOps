import { Skeleton } from '@/components/ui/skeleton'

/** First-load placeholder, before GET /flights/active has resolved even once. */
export function PanelSkeleton() {
  return (
    <div className="space-y-3">
      <Skeleton className="h-7 w-40" />
      <Skeleton className="h-20 w-full rounded-md" />
      <div className="grid grid-cols-2 gap-2">
        <Skeleton className="h-14 w-full rounded-md" />
        <Skeleton className="h-14 w-full rounded-md" />
        <Skeleton className="h-14 w-full rounded-md" />
        <Skeleton className="h-14 w-full rounded-md" />
      </div>
    </div>
  )
}
