import { Plane } from 'lucide-react'

import { PageHeader } from '@/components/shared/PageHeader'
import { EmptyState } from '@/components/shared/EmptyState'

export function Fleet() {
  return (
    <div>
      <PageHeader title="Fleet" description="Aircraft you own or lease, and their status." />
      <EmptyState
        icon={Plane}
        title="No aircraft yet"
        description="Nothing here yet — this arrives in a later update."
      />
    </div>
  )
}
