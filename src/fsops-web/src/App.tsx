import { lazy, Suspense, useEffect } from 'react'
import { Navigate, Route, Routes, useNavigate } from 'react-router-dom'

import { AppShell } from '@/components/layout/AppShell'
import { OnboardingWizard } from '@/components/onboarding/OnboardingWizard'
import { ErrorBoundary } from '@/components/shared/ErrorBoundary'
import { PageSkeleton } from '@/components/shared/PageSkeleton'
import { useAirlineGate } from '@/hooks/useAirlineGate'
import { applyAccentColour } from '@/lib/theme'

// Each page is its own chunk, fetched only when the player navigates there. This matters most for
// routes that pull in maplibre-gl (Dashboard, Routes, and - via LiveFlightView - Fly indirectly):
// none of that has to download before the app shell can even paint. It also keeps a future compact
// /panel route (see docs/PLAN.md) from ever paying for the rest of the app's pages.
const Dashboard = lazy(() => import('@/pages/Dashboard').then((m) => ({ default: m.Dashboard })))
const Fly = lazy(() => import('@/pages/Fly').then((m) => ({ default: m.Fly })))
const RoutesPage = lazy(() => import('@/pages/Routes').then((m) => ({ default: m.RoutesPage })))
const Fleet = lazy(() => import('@/pages/Fleet').then((m) => ({ default: m.Fleet })))
const Pilots = lazy(() => import('@/pages/Pilots').then((m) => ({ default: m.Pilots })))
const Finances = lazy(() => import('@/pages/Finances').then((m) => ({ default: m.Finances })))
const Stats = lazy(() => import('@/pages/Stats').then((m) => ({ default: m.Stats })))
const Settings = lazy(() => import('@/pages/Settings').then((m) => ({ default: m.Settings })))

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
      <Suspense fallback={<PageSkeleton />}>
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
      </Suspense>
    </ErrorBoundary>
  )
}

export default App
