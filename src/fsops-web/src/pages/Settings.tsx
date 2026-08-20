import { AirlineSection } from '@/components/settings/AirlineSection'
import { DataSection } from '@/components/settings/DataSection'
import { DisplaySection } from '@/components/settings/DisplaySection'
import { SimAircraftSection } from '@/components/settings/SimAircraftSection'
import { SimBriefSection } from '@/components/settings/SimBriefSection'
import { UpdatesSection } from '@/components/settings/UpdatesSection'
import { VatsimSection } from '@/components/settings/VatsimSection'
import { PageHeader } from '@/components/shared/PageHeader'

export function Settings() {
  return (
    <div>
      <PageHeader title="Settings" description="Airline identity, preferences, and app configuration." />
      <div className="space-y-6">
        <DisplaySection />
        <AirlineSection />
        <SimBriefSection />
        <SimAircraftSection />
        <VatsimSection />
        <DataSection />
        <UpdatesSection />
      </div>
    </div>
  )
}
