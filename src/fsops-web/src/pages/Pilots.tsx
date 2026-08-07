import { Users } from 'lucide-react'

import { PageHeader } from '@/components/shared/PageHeader'
import { EmptyState } from '@/components/shared/EmptyState'

export function Pilots() {
  return (
    <div>
      <PageHeader title="Pilots" description="Roster of pilots flying for your airline." />
      <EmptyState
        icon={Users}
        title="No pilots yet"
        description="Nothing here yet — this arrives in a later update."
      />
    </div>
  )
}
