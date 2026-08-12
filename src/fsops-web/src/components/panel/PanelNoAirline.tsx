import { Building2 } from 'lucide-react'

/**
 * There is no airline yet, and the request came from the MSFS toolbar panel.
 *
 * <p>The rest of the app answers "no airline" by taking over the screen with the nine-step
 * onboarding wizard. In a toolbar iframe that is the wrong answer twice over: the wizard is far too
 * big for the window, and a player sitting in the cockpit cannot found an airline from there
 * anyway. So the panel says the one true thing it can say and points at the place the job actually
 * gets done, rather than rendering a form nobody can complete.</p>
 *
 * <p>Deliberately self-contained - it carries the same outer chrome as Panel.tsx so it looks like
 * the panel's other states, and imports nothing beyond an icon. App.tsx routes here instead of the
 * wizard, which keeps /panel from downloading the onboarding tree to render a sentence.</p>
 */
export function PanelNoAirline() {
  return (
    <div className="min-h-screen bg-background px-3 py-3 text-foreground">
      <div className="mb-3 flex items-center justify-between gap-2">
        <span className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">FSOps</span>
      </div>
      <div className="flex flex-col items-center justify-center gap-3 py-16 text-center">
        <div className="flex size-10 items-center justify-center rounded-full bg-muted text-muted-foreground">
          <Building2 className="size-5" />
        </div>
        <div className="space-y-1">
          <p className="text-sm font-medium">No airline yet</p>
          <p className="text-xs text-muted-foreground">Set one up in the FSOps app, then reopen this panel.</p>
        </div>
      </div>
    </div>
  )
}
