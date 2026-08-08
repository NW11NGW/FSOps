import { useEffect } from 'react'
import { Navigate, Route, Routes, useNavigate } from 'react-router-dom'

import { AppShell } from '@/components/layout/AppShell'
import { OnboardingWizard } from '@/components/onboarding/OnboardingWizard'
import { ErrorBoundary } from '@/components/shared/ErrorBoundary'
import { useAirlineGate } from '@/hooks/useAirlineGate'
import { applyAccentColour } from '@/lib/theme'
import { Dashboard } from '@/pages/Dashboard'
import { Fly } from '@/pages/Fly'
import { RoutesPage } from '@/pages/Routes'
import { Fleet } from '@/pages/Fleet'
import { Pilots } from '@/pages/Pilots'
import { Finances } from '@/pages/Finances'
import { Stats } from '@/pages/Stats'
import { Settings } from '@/pages/Settings'

function FullScreenSplash() {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-background">
      <div className="size-8 animate-spin rounded-full border-2 border-accent border-t-transparent" />
    </div>
  )
}

function App() {
  const { status, airline, markCreated } = useAirlineGate()
  const navigate = useNavigate()

  // Once an airline is known (fresh boot or just-created), re-theme the app around its accent colour.
  useEffect(() => {
    if (status === 'app' && airline) {
      applyAccentColour(airline.accentColour)
    }
  }, [status, airline])

  if (status === 'checking') {
    return <FullScreenSplash />
  }

  if (status === 'wizard') {
    return (
      <OnboardingWizard
        onCreated={(created) => {
          markCreated(created)
          navigate('/', { replace: true })
        }}
      />
    )
  }

  return (
    <ErrorBoundary>
      <Routes>
        <Route element={<AppShell />}>
          <Route index element={<Dashboard />} />
          <Route path="fly" element={<Fly />} />
          <Route path="routes" element={<RoutesPage />} />
          <Route path="fleet" element={<Fleet />} />
          <Route path="pilots" element={<Pilots />} />
          <Route path="finances" element={<Finances />} />
          <Route path="stats" element={<Stats />} />
          <Route path="settings" element={<Settings />} />
          {/* Any unknown path lands on the dashboard rather than rendering nothing at all. */}
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </ErrorBoundary>
  )
}

export default App
