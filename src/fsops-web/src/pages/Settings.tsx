import { Settings as SettingsIcon } from 'lucide-react'

import { PageHeader } from '@/components/shared/PageHeader'
import { EmptyState } from '@/components/shared/EmptyState'

export function Settings() {
  return (
    <div>
      <PageHeader title="Settings" description="Airline identity, preferences, and app configuration." />
      <EmptyState
        icon={SettingsIcon}
        title="Nothing to configure yet"
        description="Nothing here yet — this arrives in a later update."
      />
    </div>
  )
}
