# FSOps

**Build and fly your own virtual airline in Microsoft Flight Simulator 2024.**

FSOps is a Windows companion app for MSFS 2024. Found an airline, pick a home base and a strategy, build out a route network, and fly it — FSOps tracks every flight live against the simulator and runs a proper economy underneath it, itemising exactly what each sector earned and cost. Growing your airline on its own schedule between sessions, via virtual pilots, is planned but not yet built — see [Status](#status-in-development) below for what's real today.

## Status: in development

FSOps is being built in the open, in public view, one feature at a time. Founding an airline, planning a route network, flying a route with full live tracking through MSFS, and posting the resulting ticket revenue and costs to your airline's ledger all work today. Virtual pilots, fleet purchasing, maintenance, and statistics dashboards are not available yet — see the [user guide](docs/guides/user-guide.md) for what's built and what's coming.

## Headline features

- **Found your airline** — a full-screen setup wizard walks you through naming your airline, picking a home base airport and strategy (international, domestic, low-cost, premium, or the balanced all-rounder), an accent colour and starter aircraft, your currency and units, and an optional startup loan. Strategy isn't locked in — change it any time from Settings → Airline.
- **Plan a round-trip route network** — search the airport database, pick a departure and arrival, and see a live plan: great-circle distance and bearing on an interactive map, block time and cruise altitude, a full fuel breakdown, and a suggested fare you can override before creating the route. Creating a route always creates both directions, with paired flight numbers, so your aircraft is never stranded at the outstation. Every saved route draws as a curved great-circle line on your network map, with your hub marked distinctly.
- **Fly a tracked flight** — pick a route on the Fly screen, review the flight brief (distance, cruise altitude, block time, block fuel breakdown), then start flying. FSOps connects to MSFS 2024 via SimConnect, auto-reconnecting whenever the sim isn't running, and tracks your flight live: phase detection (preflight through shutdown), a moving map, and live readouts of altitude, speed and fuel.
- **Landing quality scoring and report cards** — every landing is graded on touchdown rate, peak G-force, bounce count, and deviation from the runway centreline. A post-flight report card compares your actual block time and fuel burn against the plan and shows the full phase timeline with captured OOOI times.
- **Plan in SimBrief** — one click opens SimBrief with your flight prefilled (origin, destination, aircraft type, airline, flight number, registration); nothing is sent anywhere except to SimBrief's own site in your browser.
- **Aircraft-type awareness** — FSOps checks the aircraft you're actually flying against the route's expected aircraft at family level (a 737-800 matches a 737-700 route); a mismatch is flagged for information only and never affects payment.
- **Settings that stay out of your way** — currency, distance/altitude/weight units, time display, theme and accent colour are all adjustable; changing currency only changes how numbers are displayed, never your actual balance.
- **A real airport database** — world airport and runway data imports automatically on first launch, backing route search, runway-length checks, and range validation against your fleet.
- **A deep economy simulation** — passenger demand and price elasticity decide who actually books your fare (load factor is capped around 92% no matter how cheap you go), and every flight posts itemised ticket revenue, fuel (charged on uplift, not burn), landing/handling/parking/passenger fees, maintenance accrual, and crew cost to your airline's append-only ledger. There's no fare cap — the simulation is the guardrail: price too high and passengers, and eventually the market itself, evaporate rather than rewarding you for gouging a captive handful of them. A slew or teleport during tracking voids that sector's revenue outright, never quietly discounts it. Starting capital, your first lease deposit, and any startup loan post once, when you found your airline — recurring monthly costs (lease, salary, insurance) aren't billed on a schedule yet, so today's game is easier than the model intends; virtual pilots, aircraft purchasing, and that recurring billing *(coming in a later update)* will plug into the same engine.
- **Virtual pilots** *(coming in a later update)* — hire pilots who fly your scheduled routes on the real-world clock, even while FSOps is closed.
- **Buying and leasing aircraft, and maintenance** *(coming in a later update)* — grow your fleet beyond your starter aircraft and keep it airworthy.
- **In-game panel** *(coming in a later update)* — see live airline stats without leaving MSFS.
- **Statistics dashboards** *(coming in a later update)* — track your airline's performance over time.

## Quick start

New to FSOps? Start with the [Getting Started guide](docs/guides/getting-started.md) for installation and first run.

Once it's running, the [User Guide](docs/guides/user-guide.md) walks through every feature area — what exists today and what's arriving in later updates.

Running into trouble? Check the [Troubleshooting guide](docs/guides/troubleshooting.md).

Interested in how it's built? See [Architecture](docs/architecture.md).

## Tech stack

- **Backend:** .NET 8 (C#), talking to MSFS via SimConnect (through the `CTrue.FsConnect` library)
- **API & real-time:** ASP.NET Core REST API plus SignalR for live push updates (telemetry, hub connection status, flight-completion notifications)
- **Frontend:** React + TypeScript, built with Vite and styled with Tailwind CSS and shadcn/ui, served locally by the backend and opened in your browser; MapLibre GL for the route and live-flight maps
- **Storage:** SQLite, accessed through Entity Framework Core
- **Tests:** xUnit

The UI runs locally in your browser at `http://localhost:5977` — there's no separate installer step for it; the backend serves it directly.

## Project structure

```
FSOps/
├── src/
│   ├── FSOps.Core/       # Domain model, money/planning/finance logic (no framework deps)
│   │   ├── Airlines/     #   Airline creation defaults, registration generation
│   │   ├── Airports/     #   Airport search ranking, size categorisation
│   │   ├── Economy/      #   Demand, fares, fuel pricing, flight costs, itemised ledger postings
│   │   ├── Entities/     #   Domain entities (Airline, Route, FleetAircraft, Flight, FlightEvent, ...)
│   │   ├── Finance/      #   Loan calculations
│   │   ├── Flights/      #   Flight-phase state machine, landing quality, aircraft-type matching
│   │   ├── Money/        #   Currency catalogue and base-unit formatting
│   │   ├── Planning/     #   Route preview: distance, bearing, block time, fuel, fare
│   │   └── Routes/       #   Flight number generation
│   ├── FSOps.Data/       # EF Core + SQLite persistence, world data import
│   ├── FSOps.Sim/        # Sim abstraction: the SimConnect adapter and a replay-based fake source
│   ├── FSOps.Server/     # API endpoints, SignalR hubs, flight lifecycle service, and serves the built web UI
│   └── fsops-web/        # React + TypeScript + Vite + Tailwind + shadcn/ui frontend
│       └── src/components/
│           ├── flight/   #   Fly screen pieces: route selector, brief, live view, report card
│           └── map/      #   Route network and live-flight maps (MapLibre GL)
├── tests/
│   ├── FSOps.Core.Tests/   # xUnit tests for domain, planning, and flight-tracking logic
│   └── FSOps.Server.Tests/ # xUnit tests for route pairing and airline-summary endpoints
├── docs/
│   ├── architecture.md
│   └── guides/
│       ├── getting-started.md
│       ├── user-guide.md
│       └── troubleshooting.md
└── README.md
```

## Screenshots

> _Coming soon — screenshots will be added here as the UI takes shape._
