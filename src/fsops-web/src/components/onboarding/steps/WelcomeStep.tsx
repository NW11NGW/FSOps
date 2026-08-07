import { PlaneTakeoff, Route, Wallet } from 'lucide-react'

const HIGHLIGHTS = [
  { icon: PlaneTakeoff, text: 'Pick a home base and a starter aircraft.' },
  { icon: Route, text: 'Fly real routes and grow your network.' },
  { icon: Wallet, text: 'Manage cash, loans, and fleet economics.' },
]

export function WelcomeStep() {
  return (
    <div>
      <p className="mb-3 text-sm font-medium uppercase tracking-wide text-accent">Welcome to FSOps</p>
      <h1 className="text-3xl font-semibold tracking-tight sm:text-4xl">Let&rsquo;s found your airline.</h1>
      <p className="mt-4 text-base text-muted-foreground">
        A few quick steps and you&rsquo;ll have a living virtual airline — a home base, a strategy, a starter
        aircraft, and a cash position — ready to fly in Microsoft Flight Simulator 2024.
      </p>
      <ul className="mt-8 space-y-4">
        {HIGHLIGHTS.map(({ icon: Icon, text }) => (
          <li key={text} className="flex items-center gap-3 text-sm">
            <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-accent/15 text-accent">
              <Icon className="size-4" />
            </span>
            <span>{text}</span>
          </li>
        ))}
      </ul>
      <p className="mt-8 text-sm text-muted-foreground">
        Takes about two minutes. Almost everything here can be changed later in Settings.
      </p>
    </div>
  )
}
