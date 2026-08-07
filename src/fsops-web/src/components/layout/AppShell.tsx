import { Outlet } from 'react-router-dom'

import { Sidebar } from '@/components/layout/Sidebar'
import { TopBar } from '@/components/layout/TopBar'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'
import { useAirlineSummary } from '@/hooks/useAirlineSummary'
import { useLiveConnection } from '@/hooks/useLiveConnection'
import type { LiveContext } from '@/types/live-context'

export function AppShell() {
  const { status, heartbeat } = useLiveConnection()
  const airlineSummary = useAirlineSummary()
  const context: LiveContext = { status, heartbeat, airlineSummary }

  return (
    <TooltipProvider delayDuration={200}>
      <div className="flex h-screen w-full overflow-hidden bg-background text-foreground">
        <Sidebar />
        <div className="flex min-w-0 flex-1 flex-col">
          <TopBar hubStatus={status} simConnected={heartbeat?.simConnected ?? false} airlineSummary={airlineSummary} />
          <main className="flex-1 overflow-y-auto scrollbar-thin">
            <div className="mx-auto w-full max-w-7xl px-6 py-6">
              <Outlet context={context} />
            </div>
          </main>
        </div>
      </div>
      <Toaster position="bottom-right" />
    </TooltipProvider>
  )
}
