import { Route, Routes } from 'react-router-dom'

import { AppShell } from '@/components/layout/AppShell'
import { Dashboard } from '@/pages/Dashboard'
import { Fly } from '@/pages/Fly'
import { RoutesPage } from '@/pages/Routes'
import { Fleet } from '@/pages/Fleet'
import { Pilots } from '@/pages/Pilots'
import { Finances } from '@/pages/Finances'
import { Stats } from '@/pages/Stats'
import { Settings } from '@/pages/Settings'

function App() {
  return (
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
      </Route>
    </Routes>
  )
}

export default App
