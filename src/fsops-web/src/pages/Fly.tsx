import { PlaneTakeoff } from 'lucide-react'

import { PageHeader } from '@/components/shared/PageHeader'
import { EmptyState } from '@/components/shared/EmptyState'

export function Fly() {
  return (
    <div>
      <PageHeader title="Fly" description="Track an active flight in MSFS in real time." />
      <EmptyState
        icon={PlaneTakeoff}
        title="No flight in progress"
        description="Nothing here yet — this arrives in a later update."
      />
    </div>
  )
}
