# FSOps

**Build and fly your own virtual airline in Microsoft Flight Simulator 2024.**

FSOps is a Windows companion app for MSFS 2024. Found an airline, pick a home base and a strategy, build out a route network, and fly it — FSOps tracks every flight live against the simulator, runs a proper economy underneath it, and lets your airline keep growing on its own schedule between sessions.

## Status: in development

FSOps is being built in the open, in public view, one feature at a time. Founding an airline, planning routes on a live map, and adjusting settings all work today. Flight tracking, the economy simulation, virtual pilots, fleet purchasing, maintenance, and statistics dashboards are not available yet — see the [user guide](docs/guides/user-guide.md) for what's built and what's coming.

## Headline features

- **Found your airline** — a full-screen setup wizard walks you through naming your airline, picking a home base airport and strategy (international, domestic, low-cost, or premium), an accent colour and starter aircraft, your currency and units, and an optional startup loan.
- **Plan your route network** — search the airport database, pick a departure and arrival, and see a live plan: great-circle distance and bearing on an interactive map, block time and cruise altitude, a full fuel breakdown, and a suggested fare you can override before creating the route.
- **Settings that stay out of your way** — currency, distance/altitude/weight units, time display, theme and accent colour are all adjustable; changing currency only changes how numbers are displayed, never your actual balance.
- **A real airport database** — world airport and runway data imports automatically on first launch, backing route search, runway-length checks, and range validation against your fleet.
- **Live flight tracking** *(coming in a later update)* — FSOps watches your flight through SimConnect in real time, with automatic flight-phase detection.
- **Landing quality scoring** *(coming in a later update)* — every landing graded on touchdown rate, G-force, and centerline accuracy, with a post-flight report card comparing planned vs actual block time.
- **A deep economy simulation** *(coming in a later update)* — passenger demand, ticket pricing, fuel and fees, maintenance, loans, and leases all driving your airline's finances.
- **Virtual pilots** *(coming in a later update)* — hire pilots who fly your scheduled routes on the real-world clock, even while FSOps is closed.
- **In-game panel** *(coming in a later update)* — see live airline stats without leaving MSFS.
- **Statistics dashboards** *(coming in a later update)* — track your airline's performance over time.

## Quick start

New to FSOps? Start with the [Getting Started guide](docs/guides/getting-started.md) for installation and first run.

Once it's running, the [User Guide](docs/guides/user-guide.md) walks through every feature area — what exists today and what's arriving in later updates.

Running into trouble? Check the [Troubleshooting guide](docs/guides/troubleshooting.md).

Interested in how it's built? See [Architecture](docs/architecture.md).

## Tech stack

- **Backend:** .NET 8 (C#), talking to MSFS via SimConnect
- **API & real-time:** ASP.NET Core REST API plus SignalR for live push updates
- **Frontend:** React + TypeScript, built with Vite and styled with Tailwind CSS and shadcn/ui, served locally by the backend and opened in your browser
- **Storage:** SQLite, accessed through Entity Framework Core
- **Tests:** xUnit

The UI runs locally in your browser at `http://localhost:5977` — there's no separate installer step for it; the backend serves it directly.

## Project structure

```
FSOps/
├── src/
│   ├── FSOps.Core/      # Domain model, money/planning/finance logic (no framework deps)
│   │   ├── Airlines/    #   Airline creation defaults, registration generation
│   │   ├── Airports/    #   Airport search ranking, size categorisation
│   │   ├── Entities/    #   Domain entities (Airline, Route, FleetAircraft, LedgerTransaction, ...)
│   │   ├── Finance/     #   Loan calculations
│   │   ├── Money/       #   Currency catalogue and base-unit formatting
│   │   └── Planning/    #   Route preview: distance, bearing, block time, fuel, fare
│   ├── FSOps.Data/      # EF Core + SQLite persistence, world data import
│   ├── FSOps.Sim/       # SimConnect adapter for talking to MSFS
│   ├── FSOps.Server/    # API endpoints, SignalR hubs, and serves the built web UI
│   └── fsops-web/       # React + TypeScript + Vite + Tailwind + shadcn/ui frontend
├── tests/
│   └── FSOps.Core.Tests/  # xUnit tests for domain and planning logic
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
