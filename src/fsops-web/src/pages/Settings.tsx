import { AirlineSection } from '@/components/settings/AirlineSection'
import { DataSection } from '@/components/settings/DataSection'
import { DisplaySection } from '@/components/settings/DisplaySection'
import { SimulatorSection } from '@/components/settings/SimulatorSection'
import { PageHeader } from '@/components/shared/PageHeader'

export function Settings() {
  return (
    <div>
      <PageHeader title="Settings" description="Airline identity, preferences, and app configuration." />
      <div className="space-y-6">
        <DisplaySection />
        <AirlineSection />
        <SimulatorSection />
        <DataSection />
      </div>
    </div>
  )
}
