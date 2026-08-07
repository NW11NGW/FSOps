import { Wallet } from 'lucide-react'

import { PageHeader } from '@/components/shared/PageHeader'
import { EmptyState } from '@/components/shared/EmptyState'

export function Finances() {
  return (
    <div>
      <PageHeader
        title="Finances"
        description="Cash flow, loans, and the ledger behind your airline's economy."
      />
      <EmptyState
        icon={Wallet}
        title="No financial activity yet"
        description="Nothing here yet — this arrives in a later update."
      />
    </div>
  )
}
