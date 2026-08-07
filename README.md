# FSOps

**Build and fly your own virtual airline in Microsoft Flight Simulator 2024.**

FSOps is a Windows companion app for MSFS 2024. Found an airline, pick a home base and a strategy, build out a route network, and fly it — FSOps tracks every flight live against the simulator, runs a proper economy underneath it, and lets your airline keep growing on its own schedule between sessions.

## Status: in development

FSOps is being built in the open, in public view, one feature at a time. The current build is an early application shell: a .NET backend that serves the web UI and holds a live connection over SignalR. Airline creation, route building, flight tracking, and the economy simulation are not available yet — see the [user guide](docs/guides/user-guide.md) for what's built and what's coming.

## Headline features

- **Found your airline** — name it, choose a home base airport, and pick a strategy: international, domestic, low-cost, or premium.
- **Build your route network** — plan the city pairs your airline will fly and grow the network as you go.
- **Live flight tracking** — FSOps watches your flight through SimConnect in real time, with an interactive map and automatic flight-phase detection.
- **Landing quality scoring** — every landing is graded on touchdown rate, G-force, and centerline accuracy, with a post-flight report card comparing planned vs actual block time.
- **A deep economy simulation** — passenger demand, ticket pricing, fuel and fees, maintenance, loans, and leases all drive your airline's finances.
- **Virtual pilots** — hire pilots who fly your scheduled routes on the real-world clock, even while FSOps is closed.
- **In-game panel** — see live airline stats without leaving MSFS.
- **Statistics dashboards** — track your airline's performance over time.

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
│   ├── FSOps.Core/      # Domain model and economy logic
│   ├── FSOps.Data/      # EF Core + SQLite persistence
│   ├── FSOps.Sim/       # SimConnect adapter for talking to MSFS
│   ├── FSOps.Server/    # API, SignalR hubs, and serves the built web UI
│   └── fsops-web/       # React + TypeScript + Vite + Tailwind + shadcn/ui frontend
├── tests/
│   └── FSOps.Core.Tests/  # xUnit tests for domain and economy logic
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
