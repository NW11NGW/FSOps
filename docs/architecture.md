# Architecture

This document describes how FSOps is put together: the solution layout, how a request or a piece of sim data flows through the system, and the design principles the codebase is built around.

## Table of contents

- [Solution layout](#solution-layout)
- [FSOps.Core areas](#fsopscore-areas)
- [API endpoint surface](#api-endpoint-surface)
- [Request and data flow](#request-and-data-flow)
- [Design principles](#design-principles)
  - [Local-first, structured to move online later](#local-first-structured-to-move-online-later)
  - [Money is stored in a single base unit](#money-is-stored-in-a-single-base-unit)
  - [Append-only ledger](#append-only-ledger)
  - [App paths: no hardcoded filesystem paths](#app-paths-no-hardcoded-filesystem-paths)
  - [Deterministic, testable planning logic](#deterministic-testable-planning-logic)
  - [Sim abstraction](#sim-abstraction)
- [Layering diagram](#layering-diagram)

## Solution layout

FSOps is a single .NET solution (`FSOps.sln`) with a React frontend alongside it:

| Project | Responsibility |
|---|---|
| `src/FSOps.Core` | Domain model, money handling, route planning, and finance calculations. Airlines, routes, aircraft, flights, pilots, and the entities and pure logic that drive them — see [FSOps.Core areas](#fsopscore-areas). No dependency on ASP.NET Core, EF Core, or SimConnect — this project is plain C# so it can be unit tested in isolation. |
| `src/FSOps.Data` | Persistence. Entity Framework Core mapping of the domain model onto SQLite, entity configurations, world data import, and the `FsOpsDbContext` used by the server. |
| `src/FSOps.Sim` | The SimConnect adapter. Wraps the SimConnect API to read live aircraft state from MSFS and expose it to the rest of the app through a sim-agnostic interface (see [Sim abstraction](#sim-abstraction)). |
| `src/FSOps.Server` | The ASP.NET Core host. Exposes the REST API (see [API endpoint surface](#api-endpoint-surface)) and SignalR hubs, wires everything together via dependency injection, and serves the built frontend as static files. |
| `src/fsops-web` | The React + TypeScript frontend, built with Vite and styled with Tailwind CSS and shadcn/ui. Runs entirely in the browser against the local server. |
| `tests/FSOps.Core.Tests` | xUnit tests, focused on `FSOps.Core`'s domain and planning logic. |

## FSOps.Core areas

`FSOps.Core` is organised into a handful of focused areas rather than one flat namespace:

| Area | Contents |
|---|---|
| `Entities/` | The domain model: `Airline`, `Route`, `FleetAircraft`, `Flight`, `FlightEvent`, `Pilot`, `Loan`, `Lease`, `LedgerTransaction`, `MaintenanceEvent`, `EconomyState`, `Airport`, `Runway`, `AircraftType`, `UserSettings`, and the shared enums (strategy profile, units, currencies, statuses). |
| `Money/` | `CurrencyCatalogue` (the supported-currency list, each with a fixed display rate against the GBP-pegged base unit) and `MoneyFormatter` (base-unit → display-currency conversion and formatting). See [Money is stored in a single base unit](#money-is-stored-in-a-single-base-unit). |
| `Planning/` | Route preview math: `GreatCircle` (distance/bearing), `CruiseAltitudeSelector`, `BlockTimeEstimator`, `BlockFuelEstimator`, `FareEstimator`, composed together by `RoutePreviewCalculator` into one preview result plus validation warnings. Pure, deterministic, no I/O. |
| `Finance/` | `LoanCalculator` — amortising loan monthly-payment math, used both when a startup loan is taken and for the review-step preview in the wizard. |
| `Airlines/` | `AirlineCreationDefaults` (starting capital, starting pilot salary) and `AircraftRegistrationGenerator` (country-appropriate tail number generation for a newly purchased aircraft). |
| `Airports/` | `AirportSearchRanking` and `AirportSizeCategoryMapper`, used by airport search and by the home-base-suitability check in airline creation. |
| `AppPaths.cs` | Runtime resolution of every path FSOps writes to. See [App paths](#app-paths-no-hardcoded-filesystem-paths). |

## API endpoint surface

All REST endpoints are versioned under `/api/v1`:

| Route group | Endpoints | Covers |
|---|---|---|
| `/airline` | `POST`, `GET`, `PUT`, `GET /summary`, `DELETE` | Founding an airline (creates the airline, starter fleet aircraft, first pilot, and opening ledger entries in one transaction), fetching/updating it, a summary including derived cash balance and counts, and the "start over" soft-delete. |
| `/settings` | `GET`, `PUT`, `GET /currencies` | Per-user display settings — currency, units, theme, accent colour, Community folder path — created lazily on first access, and the supported-currency catalogue. |
| `/routes` | `POST`, `GET`, `GET /{id}`, `DELETE /{id}`, `POST /preview` | Route creation and listing (list includes each route's estimated block time), fetching or soft-deleting a single route, and the live, throw-free preview used while picking airports. |
| `/airports` | search / lookup endpoints | Backing the airport pickers in route building and airline creation, using the imported world airport/runway data. |
| `/worlddata` | `GET /status` | Reports whether world data import has finished, is in progress, and how many airports/runways were loaded — polled by the frontend on first launch. |

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
- **Every row scoped by owner.** `OwnerUserId` on account-level records (`Airline`, `UserSettings`) and `AirlineId` on everything that hangs off an airline (routes, fleet, pilots, ledger transactions, flights) mean every query already filters by who owns what. Locally that's always the same single user, but the schema is already shaped for multiple users sharing one database once real auth lands.

### Money is stored in a single base unit

Every amount that touches money — `LedgerTransaction.Amount`, an aircraft's purchase price, a route's fare, a loan's principal — is stored in one fixed base currency unit (GBP-pegged; see `CurrencyCatalogue.BaseCurrencyCode` in `FSOps.Core.Money`), never in whatever currency the user happens to have selected.

Display currency is purely a read-time transform: `MoneyFormatter.ConvertFromBase` multiplies a base-unit amount by the selected currency's fixed `DisplayRate` and formats it with that currency's symbol and decimal places. Nothing is ever converted the other way and written back — a user switching their display currency in settings changes what every screen shows, never what's stored. This also means display rates don't need to be fetched or kept in sync with real exchange rates; FSOps is a game economy, not a forex simulator.

### Append-only ledger

Your airline's cash balance is never stored as a single mutable number that gets incremented and decremented in place. Instead, every financial event — a ticket sale, a fuel bill, a lease payment, a loan drawdown, starting capital — is written as its own row in an append-only `LedgerTransaction` table. The cash balance the API and UI show is always derived as **`SUM(LedgerTransaction.Amount)`** for the airline, computed at read time, rather than read off a stored column.

`FlightEvent` (the phase-by-phase record of a tracked flight, once flight tracking lands) follows the same append-only rule for the same reason: a historical record that's only ever added to is self-auditing — nothing is silently overwritten, and any derived value can be reconstructed or verified independently just by re-reading the log.

This costs a bit of query overhead — SQLite's EF provider can't translate `Sum()` over `decimal` into SQL, so the (small, per-airline) set of amounts is pulled into memory and summed there — in exchange for a system where the financial history can't drift out of sync with how it got there.

### App paths: no hardcoded filesystem paths

FSOps installs into `Program Files`, which is read-only for standard users, so nothing may ever be written next to the executable. Every path FSOps writes to — the SQLite database, log files, and any future config — is resolved at runtime through `AppPaths` (`FSOps.Core.AppPaths`), which resolves everything under the current Windows user's `%LOCALAPPDATA%\FSOps\` and creates directories on first access:

- `AppPaths.DataDirectory` → `%LOCALAPPDATA%\FSOps\`
- `AppPaths.DatabasePath` → `%LOCALAPPDATA%\FSOps\fsops.db`
- `AppPaths.LogsDirectory` → `%LOCALAPPDATA%\FSOps\logs\`

No file path is ever hardcoded elsewhere in the codebase — everything goes through `AppPaths`. That's what makes FSOps work correctly wherever it's installed and for whichever Windows account is running it, without an installer needing to set permissions on a shared location.

### Deterministic, testable planning logic

Route planning — distance and bearing, cruise altitude selection, block time and fuel estimation, fare suggestion — lives in `FSOps.Core.Planning` with no dependency on the database, the web framework, or the simulator. Given the same inputs, it produces the same outputs every time. That determinism is what makes it practical to unit test thoroughly in `FSOps.Core.Tests`: planning behaviour can be verified in isolation, without spinning up a server or a database, and without MSFS anywhere in the loop. The same pattern is intended for the economy simulation (demand, pricing, maintenance wear) as it's built out.

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
