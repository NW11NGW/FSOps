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
- [Simulator connection and the telemetry pipeline](#simulator-connection-and-the-telemetry-pipeline)
- [Flight-phase detection and landing quality](#flight-phase-detection-and-landing-quality)
- [The append-only flight event log and crash recovery](#the-append-only-flight-event-log-and-crash-recovery)
- [Layering diagram](#layering-diagram)

## Solution layout

FSOps is a single .NET solution (`FSOps.sln`) with a React frontend alongside it:

| Project | Responsibility |
|---|---|
| `src/FSOps.Core` | Domain model, money handling, route planning, and finance calculations. Airlines, routes, aircraft, flights, pilots, and the entities and pure logic that drive them — see [FSOps.Core areas](#fsopscore-areas). No dependency on ASP.NET Core, EF Core, or SimConnect — this project is plain C# so it can be unit tested in isolation. |
| `src/FSOps.Data` | Persistence. Entity Framework Core mapping of the domain model onto SQLite, entity configurations, world data import, and the `FsOpsDbContext` used by the server. |
| `src/FSOps.Sim` | The sim abstraction and its two implementations: `SimConnectSource`, which wraps `CTrue.FsConnect` to read live aircraft state from a running copy of MSFS, and `FakeSimSource`, which replays a recorded flight from a JSON script with no simulator needed. Both implement the same `ISimSource` interface (see [Sim abstraction](#sim-abstraction)). |
| `src/FSOps.Server` | The ASP.NET Core host. Exposes the REST API (see [API endpoint surface](#api-endpoint-surface)) and SignalR hubs, runs the background services that pump sim telemetry and drive flight tracking (see [Simulator connection and the telemetry pipeline](#simulator-connection-and-the-telemetry-pipeline)), wires everything together via dependency injection, and serves the built frontend as static files. |
| `src/fsops-web` | The React + TypeScript frontend, built with Vite and styled with Tailwind CSS and shadcn/ui, with MapLibre GL for the route-network and live-flight maps. Runs entirely in the browser against the local server. |
| `tests/FSOps.Core.Tests` | xUnit tests, focused on `FSOps.Core`'s domain, planning, and flight-tracking logic (phase state machine, landing quality, aircraft-type matching, flight numbering). |
| `tests/FSOps.Server.Tests` | xUnit tests for server-level behaviour that needs a database, chiefly round-trip route pairing and airline-summary route counting. |

## FSOps.Core areas

`FSOps.Core` is organised into a handful of focused areas rather than one flat namespace:

| Area | Contents |
|---|---|
| `Entities/` | The domain model: `Airline`, `Route`, `FleetAircraft`, `Flight`, `FlightEvent`, `Pilot`, `Loan`, `Lease`, `LedgerTransaction`, `MaintenanceEvent`, `EconomyState`, `Airport`, `Runway`, `AircraftType`, `UserSettings`, and the shared enums (strategy profile, units, currencies, statuses). `Flight.Revenue`/`TotalCost` are a read-time cache of what's actually been posted to `LedgerTransaction` for that flight - never the source of truth themselves (see [Append-only ledger](#append-only-ledger)) - and `Flight.RevenuePosted` is the idempotency gate that stops a retry or crash rehydration posting a flight's ledger lines twice. |
| `Economy/` | The economy engine: `EconomyConfig` (every tuning constant, loaded once from `economy-config.json`), `ReferenceFareCalculator`, `DemandCalculator` (city-pair passenger pool from catchment/distance/season/day/reputation), `FareDemandModel` (the anti-exploit fare/load-factor/revenue curve - see [Fare setting and demand response](guides/user-guide.md#the-economy-simulation) in the user guide), `FuelPricing` (per-airport, per-day fuel price), `FlightCostCalculator` (weight- and size-based landing/handling/parking/passenger/turnaround fees, maintenance accrual, crew cost), and `FlightEconomicsCalculator`, which ties all of the above into one itemised `FlightEconomicsResult` for a single flight. Pure - no database, no I/O. `FSOps.Server.Services.FlightEconomicsPoster` is the only place that result becomes real `LedgerTransaction` rows: fuel is posted once, at flight start (uplift, not burn); every other line posts on completion, gated on `Flight.RevenuePosted` for idempotency, and skipped entirely for a slew/position-jump-flagged sector rather than computed and discarded. **Not yet built:** a recurring monthly billing pass (`EconomyClockService`, referenced but not implemented) that would post lease, salary and insurance payments on a schedule - today those post once, as one-off lines at airline creation, so nothing recurs. Also not yet built: persistent per-aircraft fuel state - `FleetAircraft` has no stored tank quantity, so every flight is charged for the full trip+taxi+contingency fuel its own sector needs, even a return leg that in reality would still have fuel in the tanks from the outbound. |
| `Flights/` | Flight tracking's pure domain logic: `FlightPhase` (the ten-phase enum), `FlightPhaseThresholds` (the speed/altitude/timing constants that drive phase transitions), `FlightPhaseStateMachine` (advances phase-by-phase from telemetry samples, captures OOOI and touchdowns, and restores itself from a stored event history), `LandingQualityCalculator` (runway-centreline deviation), `FlightIntegrityMonitor` (elevated simulation rate, slew, and position-jump detection - see [Append-only ledger](#append-only-ledger)), and `AircraftTypeMatcher` (family-level, informational-only aircraft matching). See [Flight-phase detection and landing quality](#flight-phase-detection-and-landing-quality). |
| `Routes/` | `FlightNumberGenerator` — deterministic odd/even outbound/return flight-number pairing per airline, pure and side-effect free. |
| `Money/` | `CurrencyCatalogue` (the supported-currency list, each with a fixed display rate against the GBP-pegged base unit) and `MoneyFormatter` (base-unit → display-currency conversion and formatting). See [Money is stored in a single base unit](#money-is-stored-in-a-single-base-unit). |
| `Planning/` | Route preview math: `GreatCircle` (distance/bearing), `CruiseAltitudeSelector`, `BlockTimeEstimator`, `BlockFuelEstimator`, composed together by `RoutePreviewCalculator` into one preview result plus validation warnings - including the suggested fare, sourced from `Economy/ReferenceFareCalculator` (the same figure the demand engine anchors to - see docs/PLAN.md "Status after the fuel-honesty fix"). Pure, deterministic, no I/O. |
| `Finance/` | `LoanCalculator` — amortising loan monthly-payment math, used both when a startup loan is taken and for the review-step preview in the wizard. |
| `Airlines/` | `AircraftRegistrationGenerator` (country-appropriate tail number generation for a newly leased aircraft). Starting capital, lease deposit and starting pilot salary used to live here as `AirlineCreationDefaults`; they now live in `Economy/EconomyConfig.AirlineStartup` instead, alongside every other tunable economy figure, so airline-creation balance can be retuned the same way as everything else - see the `Economy/` row above. |
| `Airports/` | `AirportSearchRanking` and `AirportSizeCategoryMapper`, used by airport search and by the home-base-suitability check in airline creation. |
| `AppPaths.cs` | Runtime resolution of every path FSOps writes to. See [App paths](#app-paths-no-hardcoded-filesystem-paths). |

## API endpoint surface

All REST endpoints are versioned under `/api/v1`:

| Route group | Endpoints | Covers |
|---|---|---|
| `/airline` | `POST`, `GET`, `PUT`, `GET /summary`, `GET /strategy-profiles`, `DELETE` | Founding an airline (creates the airline, leases the starter fleet aircraft, hires the first pilot, and posts opening ledger entries in one transaction), fetching/updating its identity, accent colour and strategy profile (a strategy change is going-forward only — it never touches completed flights or existing routes' fares), a summary including derived cash balance and counts, the live per-profile fares/sensitivity/load-factor/cost figures read straight from `economy-config.json` (backs the profile picker in onboarding and Settings), and the "start over" soft-delete. |
| `/settings` | `GET`, `PUT`, `GET /currencies` | Per-user display settings — currency, units, theme, Community folder path — created lazily on first access, and the supported-currency catalogue. Accent colour and strategy profile live on the airline itself (`/airline`), not here, since they're airline-specific rather than account-wide. |
| `/routes` | `POST`, `GET`, `PUT /{id}`, `GET /{id}`, `DELETE /{id}`, `POST /{id}/return-leg`, `POST /preview` | Route creation, which always creates both directions of a round trip in one call (see [Round trips and where your aircraft actually is](guides/user-guide.md#round-trips-and-where-your-aircraft-actually-is)); listing (self-healing — backfills a missing return leg or flight number for legacy single-leg routes); updating a route's flight number, fare, or active flag; fetching or soft-deleting a route pair together; a manual return-leg repair tool for routes that couldn't be paired automatically; and the live, throw-free preview used while picking airports. |
| `/airports` | search / lookup endpoints | Backing the airport pickers in route building and airline creation, using the imported world airport/runway data. |
| `/worlddata` | `GET /status` | Reports whether world data import has finished, is in progress, and how many airports/runways were loaded — polled by the frontend on first launch. |
| `/sim` | `GET /status` | The simulator's current connection state, which source is active (`SimConnect` or `Fake`), the aircraft title last seen, and when the last telemetry sample arrived — backs the top bar's sim indicator and the Fly screen's readiness checks. |
| `/flights` | `POST /start`, `POST /{id}/abandon`, `POST /{id}/complete-manual`, `GET /active`, `GET /{id}`, `GET`, `GET /options` | Starting a tracked flight (validates one-flight-at-a-time, resolves fleet aircraft and pilot, snapshots the plan, flags an aircraft-type mismatch if the sim's loaded aircraft doesn't match, and posts the fuel-uplift ledger line for that sector's trip/taxi/contingency fuel via `FlightEconomicsPoster.PostFuelUplift`); abandoning or manually completing a flight that needs resolution (manual completion also posts the sector's non-fuel economics, using the planned block time since nothing was actually measured); the currently active/interrupted flight with its live snapshot; a single flight's full detail including its event history and posted ledger lines; flight history; and, for the Fly screen, every route annotated with whether a fleet aircraft is currently available to fly it right now. A live-tracked flight's own non-fuel economics post from `FlightLifecycleService` once it reaches Shutdown, not from this endpoint group. |

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

The frontend talks to the backend two ways: versioned REST endpoints under `/api/v1` for requests with a clear request/response shape (creating an airline, adding a route, and so on), and a SignalR hub at `/hubs/live` for anything that needs to push to the browser without being asked — a heartbeat, live flight telemetry, and flight-completion/needs-resolution notifications.

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

`FSOps.Sim` owns the SimConnect connection and turns raw sim variables into a telemetry stream. `SimTelemetryService` (`FSOps.Server`) pumps that stream at the sim's own sampling rate, feeding `FlightLifecycleService` full-rate for accurate phase detection while throttling what it broadcasts over `/hubs/live` to twice a second, so the browser gets smooth live updates without every sim frame crossing the wire. `FlightLifecycleService` is where telemetry becomes domain logic — advancing the flight-phase state machine, capturing landing quality, and persisting the append-only event log a flight is built from (see [Flight-phase detection and landing quality](#flight-phase-detection-and-landing-quality) and [The append-only flight event log and crash recovery](#the-append-only-flight-event-log-and-crash-recovery)) — before broadcasting live updates back out over the same hub the browser is already listening on.

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

`FlightEvent` (the phase-by-phase record of a tracked flight) follows the same append-only rule for the same reason: a historical record that's only ever added to is self-auditing — nothing is silently overwritten, and any derived value can be reconstructed or verified independently just by re-reading the log. See [The append-only flight event log and crash recovery](#the-append-only-flight-event-log-and-crash-recovery) for how this is put to use.

This costs a bit of query overhead — SQLite's EF provider can't translate `Sum()` over `decimal` into SQL, so the (small, per-airline) set of amounts is pulled into memory and summed there — in exchange for a system where the financial history can't drift out of sync with how it got there.

### App paths: no hardcoded filesystem paths

FSOps installs into `Program Files`, which is read-only for standard users, so nothing may ever be written next to the executable. Every path FSOps writes to — the SQLite database, log files, and any future config — is resolved at runtime through `AppPaths` (`FSOps.Core.AppPaths`), which resolves everything under the current Windows user's `%LOCALAPPDATA%\FSOps\` and creates directories on first access:

- `AppPaths.DataDirectory` → `%LOCALAPPDATA%\FSOps\`
- `AppPaths.DatabasePath` → `%LOCALAPPDATA%\FSOps\fsops.db`
- `AppPaths.LogsDirectory` → `%LOCALAPPDATA%\FSOps\logs\`

No file path is ever hardcoded elsewhere in the codebase — everything goes through `AppPaths`. That's what makes FSOps work correctly wherever it's installed and for whichever Windows account is running it, without an installer needing to set permissions on a shared location.

### Deterministic, testable planning logic

Route planning — distance and bearing, cruise altitude selection, block time and fuel estimation, fare suggestion — lives in `FSOps.Core.Planning` with no dependency on the database, the web framework, or the simulator. Given the same inputs, it produces the same outputs every time. That determinism is what makes it practical to unit test thoroughly in `FSOps.Core.Tests`: planning behaviour can be verified in isolation, without spinning up a server or a database, and without MSFS anywhere in the loop. The economy engine (`FSOps.Core.Economy`) follows the same pattern — demand, fares, fuel pricing and flight costs are all pure functions of their inputs, with even fuel price's day-to-day drift coming from a hand-rolled stable hash rather than a stateful random number generator, so the exact same (airport, date) always prices identically. The one input that isn't fully pinned down by the caller is *when*: `DemandCalculator` reads the real clock (`DateTimeOffset.UtcNow`) for season and day-of-week, so the same route genuinely does price differently depending on what day you fly it — that's intentional (see [The economy simulation](guides/user-guide.md#the-economy-simulation) in the user guide), not a break in determinism, since every test pins the date explicitly rather than relying on the clock.

### Sim abstraction

`FSOps.Sim` sits behind `ISimSource` — a small interface (connection state, the currently loaded aircraft, and a channel of telemetry samples) that the rest of the app depends on, not any concrete implementation directly. Deliberately, no SimConnect-specific type is allowed to leak across that interface. That means a fake or replay-based telemetry source can stand in for MSFS entirely, which is what makes it possible to develop and test flight tracking, phase detection, and landing scoring without the simulator running at all, and without a human re-flying the same approach over and over to test a bounce-detection edge case.

`FakeSimSource` is that replay source: it plays back a scripted flight from a JSON file (the bundled default is a roughly 93-minute EGKK→LEBL sector) at a configurable time-compression factor, optionally looping. `SimConnectSource` is the real adapter, wrapping the `CTrue.FsConnect` library. Which one runs is a plain startup switch — the `sim` command-line argument (or `Sim:Source` in configuration) selects `fake` for the replay source; anything else, including nothing at all, talks to a real, running copy of MSFS. This is what the whole test suite and most day-to-day development runs against, rather than requiring MSFS to be open.

## Simulator connection and the telemetry pipeline

**Connecting.** `SimConnectSource` runs a connection loop for the lifetime of the process: each attempt creates a fresh `FsConnect` instance, calls `Connect()`, and only then registers FSOps' telemetry and aircraft-identity data definitions, before waiting for either a connected or disconnected signal. That ordering is deliberate and was previously a real bug: `RegisterDataDefinition` forwards straight to the underlying SimConnect handle, which doesn't exist until `Connect()` has actually run — registering first silently failed to reach a live MSFS 2024 session. The fix (registering after connecting, not before) has been verified against live MSFS 2024 with a Fenix A320. If a connection attempt fails or drops, it's disposed and retried after a fixed interval (5 seconds). This is why FSOps doesn't need restarting just because MSFS wasn't ready the first time — it keeps trying on its own for as long as it's running.

**Sampling rate.** Telemetry is read at an adaptive rate rather than a fixed one: roughly 5 Hz in normal flight, stepping up to essentially every sim frame once the aircraft is below 2,000 ft AGL, where the extra fidelity matters for accurately catching the moment of touchdown. A small hysteresis band (300 ft) around that threshold stops the rate from flapping back and forth near the boundary.

**What's read.** Each sample carries position (latitude/longitude), altitude (both MSL and AGL), indicated airspeed, ground speed, vertical speed, true and magnetic heading, on-ground state, engine-running state, parking brake state, G-force, the instantaneous touchdown vertical velocity simvar (only meaningful at the moment of ground contact — this is what landing-rate scoring is built on), and total fuel weight. The aircraft's title and ATC model/type are fetched separately, once per connection or aircraft change rather than every sample, since they don't change mid-flight.

**Pushing it live.** `SimTelemetryService` (`FSOps.Server`) reads the full-rate sample stream and does two things with it: it hands every sample to `FlightLifecycleService` for phase detection (see below), and it broadcasts a throttled subset — at most once every 500 ms (2 Hz) — over the `/hubs/live` SignalR hub as a `telemetry` message, carrying timestamp, position, altitude, speeds, headings, on-ground state, and connection state. The throttling keeps the browser's live view smooth without pushing every sim frame across the wire; the full-rate stream still reaches the phase state machine so a fast transition (like the instant of touchdown) is never missed just because the broadcast rate is lower.

`GET /api/v1/sim/status` exposes the same connection state on demand — current state, which source is active, the last-seen aircraft title, and the last sample's timestamp — for anything that wants to poll rather than subscribe (the Fly screen's readiness checks use this, alongside the live hub).

## Flight-phase detection and landing quality

A tracked flight moves through ten phases, in order, with one deliberate exception: **Preflight → Taxi out → Takeoff roll → Climb → Cruise → Descent → Approach → Landed → Taxi in → Shutdown**, except that a go-around sends the state machine back from Landed to Climb rather than continuing forward. `FlightPhaseStateMachine.Advance()` runs this transition logic once per telemetry sample, driven entirely by thresholds in `FlightPhaseThresholds` — ground speed crossing 2 kt or 40 kt, vertical speed settling within a 300 fpm band for 20 seconds (levelling into cruise) or descending past -300 fpm for 15 seconds (starting a descent), altitude dropping below 3,000 ft AGL while descending (entering approach), and so on. All of this is timed from the samples' own timestamps, never wall-clock time, which is what makes the whole thing replayable and deterministic in tests.

OOOI times (out/off/on/in) are captured as by-products of specific transitions: **out** when taxi begins, **off** when the aircraft becomes airborne, **on** at the first ground-contact after the approach, **in** once engines are off and the parking brake is set at the end of taxi-in.

**Touchdown and landing quality.** Every ground-contact event while approaching or already landed registers a touchdown record: touchdown rate in feet per minute (converted from the sim's raw touchdown-velocity simvar), and G-force — tracked as a peak over the few seconds immediately following contact, not just the instantaneous value at the moment of contact, so a hard bounce a beat after touchdown is still captured. A landing that bounces (leaves the ground only briefly before settling) keeps accumulating touchdown records against the same landing; one that stays airborne for more than a few seconds is reclassified as a go-around instead, clearing the touchdown records and sending the phase machine back to Climb. Centreline deviation is computed separately, once a flight finalises: `LandingQualityCalculator` picks the runway at the arrival airport whose heading is closest to the touchdown's track, then measures the perpendicular distance from the touchdown point to that runway's centreline.

**Aircraft-type matching.** `AircraftTypeMatcher` checks the sim's reported aircraft title and ATC model against a route's expected aircraft type using a stored list of regular expressions per family (so a 737-800 and a 737-700 both match a "B737 family" pattern without special-cased logic — the permissiveness lives in the stored pattern, not the matcher). This check is deliberately incapable of ever blocking or costing anything: an unparsable pattern list or a broken regex is treated as "no match" rather than as an error, and the result is stored purely as an informational flag on the flight, never consulted by anything that touches money.

## The append-only flight event log and crash recovery

Every meaningful thing that happens during a tracked flight — a phase change, a touchdown, a periodic position snapshot (every 15 seconds, so a flight's track can be reconstructed even between phase changes), an aircraft-type mismatch — is written as its own row to the `FlightEvent` table, following the same append-only rule as the financial ledger (see [Append-only ledger](#append-only-ledger)): never updated, never deleted. `FlightLifecycleService` queues these events in memory and writes them off the telemetry hot path in small batches, so persisting to SQLite never competes with processing the next incoming sample.

This log is what makes a tracked flight survive FSOps or MSFS crashing partway through. On startup, if a flight is still marked in progress, `FlightLifecycleService` loads its full event history and replays it through `FlightPhaseStateMachine.RestoreFrom()` — reconstructing phase, OOOI times, and any touchdowns already recorded, purely from the stored events, with no reliance on any in-memory state that didn't survive the crash. It then waits for the simulator to reconnect near the flight's last known position (within about 5 nm) for up to 30 seconds. If that happens, tracking resumes as if nothing happened. If it doesn't, the flight is marked **Interrupted** rather than silently resumed or abandoned — surfaced to the user as a flight that needs resolving (see [Ending a flight, and what happens if it gets interrupted](guides/user-guide.md#ending-a-flight-and-what-happens-if-it-gets-interrupted) in the user guide), where they can wait for reconnection, complete the flight with estimated figures, or abandon it outright. Nothing about a flight's already-recorded history is ever lost by this process — it's exactly what the replay is built from.

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
