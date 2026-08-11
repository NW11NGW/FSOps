import { Skeleton } from '@/components/ui/skeleton'

/** Suspense fallback for the lazily-loaded chart components - shown for the brief window between
 *  a chart section mounting and its recharts chunk finishing download/parse. */
export function ChartSkeleton({ height = 240 }: { height?: number }) {
  return <Skeleton className="w-full rounded-lg" style={{ height }} />
}
