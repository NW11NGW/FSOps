# Architecture

This document describes how FSOps is put together: the solution layout, how a request or a piece of sim data flows through the system, and the design principles the codebase is built around.

## Table of contents

- [Solution layout](#solution-layout)
- [Request and data flow](#request-and-data-flow)
- [Design principles](#design-principles)
  - [Local-first, structured to move online later](#local-first-structured-to-move-online-later)
  - [Append-only ledger](#append-only-ledger)
  - [Deterministic, testable economy logic](#deterministic-testable-economy-logic)
  - [Sim abstraction](#sim-abstraction)
- [Layering diagram](#layering-diagram)

## Solution layout

FSOps is a single .NET solution (`FSOps.sln`) with a React frontend alongside it:

| Project | Responsibility |
|---|---|
| `src/FSOps.Core` | Domain model and economy logic. Airlines, routes, aircraft, flights, pilots, and the pricing/demand/cost calculations that drive the economy. No dependency on ASP.NET Core, EF Core, or SimConnect — this project is plain C# so it can be unit tested in isolation. |
| `src/FSOps.Data` | Persistence. Entity Framework Core mapping of the domain model onto SQLite, migrations, and repository/query access used by the server. |
| `src/FSOps.Sim` | The SimConnect adapter. Wraps the SimConnect API to read live aircraft state from MSFS and expose it to the rest of the app through a sim-agnostic interface (see [Sim abstraction](#sim-abstraction)). |
| `src/FSOps.Server` | The ASP.NET Core host. Exposes the REST API and SignalR hubs, wires everything together via dependency injection, and serves the built frontend as static files. |
| `src/fsops-web` | The React + TypeScript frontend, built with Vite and styled with Tailwind CSS and shadcn/ui. Runs entirely in the browser against the local server. |
| `tests/FSOps.Core.Tests` | xUnit tests, focused on `FSOps.Core`'s domain and economy logic. |

## Request and data flow

There are two flows through the system: user-driven requests from the browser, and live telemetry from the simulator.

**Browser-driven flow:**

```
Browser SPA (fsops-web)
    │
    ├── REST calls  ──────────────► /api/v1/*
    └── live updates ◄────────────► /hubs/live  (SignalR)
                                        │
                                        ▼
                              Backend services (FSOps.Server)
                                        │
                                        ▼
                              Domain / economy logic (FSOps.Core)
                                        │
                                        ▼
                              EF Core (FSOps.Data) ──► SQLite
```

The frontend talks to the backend two ways: versioned REST endpoints under `/api/v1` for requests with a clear request/response shape (creating an airline, adding a route, and so on), and a SignalR hub at `/hubs/live` for anything that needs to push to the browser without being asked — a heartbeat today, live flight telemetry and event notifications as flight tracking is built out.

**Simulator-driven flow:**

```
MSFS 2024
    │
    ▼
SimConnect  ──────────────► FSOps.Sim (telemetry channel)
                                        │
                                        ▼
                              Flight tracker (FSOps.Server / FSOps.Core)
                                        │
                                        ▼
                              Broadcast over /hubs/live ──► Browser SPA
```

`FSOps.Sim` owns the SimConnect connection and turns raw sim variables into a telemetry stream. A flight tracker consumes that stream, applies the domain logic that turns raw telemetry into flight-phase detection and landing-quality scoring, persists what needs persisting, and broadcasts live updates back out over the same `/hubs/live` hub the browser is already listening on.

## Design principles

### Local-first, structured to move online later

FSOps runs entirely on your machine today — SQLite on disk, no account, no server to talk to. But it's structured so that moving parts of it online later (sync across machines, a hosted backend, multi-user features) is a matter of extension rather than rewrite:

- **GUID primary keys** throughout the domain model, rather than auto-incrementing integers, so records generated locally on different machines never collide.
- **UTC timestamps** everywhere, so time comparisons are never ambiguous once data starts moving between machines in different time zones.
- **Append-only event and ledger tables** (see below) rather than rows that get overwritten in place, which is what makes eventual sync or replay tractable.
- **A versioned API** (`/api/v1`) from day one, so the contract between frontend and backend can evolve without breaking older clients.
- **An auth abstraction** (`ICurrentUser` in `FSOps.Server`) that every endpoint and query is expected to depend on instead of reading identity directly off the request. Locally there's only ever one user, but this means introducing real authentication later is a dependency-injection change, not a rewrite of every endpoint.

### Append-only ledger

Your airline's cash balance is never stored as a single mutable number that gets incremented and decremented in place. Instead, every financial event — a ticket sale, a fuel bill, a lease payment, a loan drawdown — is written as its own row in an append-only ledger table. The cash balance you see in the UI is always derived as the **sum of every transaction** rather than read off a stored field.

This costs a bit of query overhead in exchange for a system where the financial history is self-auditing: nothing is ever silently overwritten, and any balance can be reconstructed or verified independently just by re-summing the ledger.

### Deterministic, testable economy logic

The economy simulation — demand, pricing, costs, maintenance wear, and so on — lives in `FSOps.Core` with no dependency on the database, the web framework, or the simulator. Given the same inputs, it produces the same outputs every time. That determinism is what makes it practical to unit test thoroughly in `FSOps.Core.Tests`: economy behaviour can be verified in isolation, without spinning up a server or a database, and without MSFS anywhere in the loop.

### Sim abstraction

`FSOps.Sim` sits behind an interface that the rest of the app depends on, not the concrete SimConnect implementation directly. That means a fake or replay-based telemetry source can stand in for MSFS entirely — which is what makes it possible to develop and test flight tracking, phase detection, and landing scoring without the simulator running at all. The real SimConnect adapter is just one implementation of that interface; a recorded-flight replay source is another.

## Layering diagram

```
┌─────────────────────────────────────────────────────────┐
│                    src/fsops-web (SPA)                   │
│         React + TypeScript, Vite, Tailwind, shadcn/ui    │
└───────────────────────────┬───────────────────────────────┘
                REST /api/v1 │ SignalR /hubs/live
┌───────────────────────────▼───────────────────────────────┐
│                     FSOps.Server                          │
│   API endpoints · SignalR hubs · DI composition · statics │
└───────┬───────────────────────────────────────────┬───────┘
        │                                           │
┌───────▼───────────────┐                 ┌─────────▼─────────┐
│      FSOps.Core        │                 │     FSOps.Sim      │
│ Domain model, economy   │                 │  SimConnect adapter │
│ (deterministic, tested) │                 │  (swappable source) │
└───────┬────────────────┘                 └─────────┬─────────┘
        │                                             │
┌───────▼────────────────┐                            │
│      FSOps.Data         │                            ▼
│  EF Core ──► SQLite      │                     MSFS 2024 (SimConnect)
└──────────────────────────┘
```

`FSOps.Server` is the only project that depends on ASP.NET Core; `FSOps.Core` stays free of framework, persistence, and simulator dependencies so it can be tested and reasoned about on its own.
