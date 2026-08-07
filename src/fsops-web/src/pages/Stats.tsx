import { BarChart3 } from 'lucide-react'

import { PageHeader } from '@/components/shared/PageHeader'
import { EmptyState } from '@/components/shared/EmptyState'

export function Stats() {
  return (
    <div>
      <PageHeader title="Stats" description="Performance trends across routes, pilots, and fleet." />
      <EmptyState
        icon={BarChart3}
        title="No stats yet"
        description="Nothing here yet — this arrives in a later update."
      />
    </div>
  )
}
