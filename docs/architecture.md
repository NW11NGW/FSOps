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
- [Playstyles and EconomyConfigCatalog](#playstyles-and-economyconfigcatalog)
- [The monthly billing cycle: EconomyClockService](#the-monthly-billing-cycle-economyclockservice)
- [Persistent fuel and tankering](#persistent-fuel-and-tankering)
- [Maintenance scheduling](#maintenance-scheduling)
- [Layering diagram](#layering-diagram)

## Solution layout

FSOps is a single .NET solution (`FSOps.sln`) with a React frontend alongside it:

| Project | Responsibility |
|---|---|
| `src/FSOps.Core` | Domain model, money handling, route planning, and finance calculations. Airlines, routes, aircraft, flights, pilots, and the entities and pure logic that drive them — see [FSOps.Core areas](#fsopscore-areas). No dependency on ASP.NET Core, EF Core, or SimConnect — this project is plain C# so it can be unit tested in isolation. |
| `src/FSOps.Data` | Persistence. Entity Framework Core mapping of the domain model onto SQLite, entity configurations, world data import, and the `FsOpsDbContext` used by the server. |
| `src/FSOps.Sim` | The sim abstraction and its two implementations: `SimConnectSource`, which wraps `CTrue.FsConnect` to read live aircraft state from a running copy of MSFS, and `FakeSimSource`, which replays a recorded flight from a JSON script with no simulator needed. Both implement the same `ISimSource` interface (see [Sim abstraction](#sim-abstraction)). |
| `src/FSOps.Server` | The ASP.NET Core host. Exposes the REST API (see [API endpoint surface](#api-endpoint-surface)) and SignalR hubs, runs the background services that pump sim telemetry and drive flight tracking (see [Simulator connection and the telemetry pipeline](#simulator-connection-and-the-telemetry-pipeline)) and the monthly billing cycle (see [The monthly billing cycle](#the-monthly-billing-cycle-economyclockservice)), wires everything together via dependency injection, and serves the built frontend as static files. |
| `src/fsops-web` | The React + TypeScript frontend, built with Vite and styled with Tailwind CSS and shadcn/ui, with MapLibre GL for the route-network and live-flight maps. Runs entirely in the browser against the local server. |
| `tests/FSOps.Core.Tests` | xUnit tests, focused on `FSOps.Core`'s domain, planning, and flight-tracking logic (phase state machine, landing quality, aircraft-type matching, flight numbering). |
| `tests/FSOps.Server.Tests` | xUnit tests for server-level behaviour that needs a database, chiefly round-trip route pairing and airline-summary route counting. |

## FSOps.Core areas

`FSOps.Core` is organised into a handful of focused areas rather than one flat namespace:

| Area | Contents |
|---|---|
| `Entities/` | The domain model: `Airline`, `Route`, `FleetAircraft`, `Flight`, `FlightEvent`, `Pilot`, `Loan`, `Lease`, `LedgerTransaction`, `MaintenanceEvent`, `EconomyState`, `Airport`, `Runway`, `AircraftType`, `UserSettings`, and the shared enums (strategy profile, units, currencies, statuses). `Flight.Revenue`/`TotalCost` are a read-time cache of what's actually been posted to `LedgerTransaction` for that flight - never the source of truth themselves (see [Append-only ledger](#append-only-ledger)) - and `Flight.RevenuePosted` is the idempotency gate that stops a retry or crash rehydration posting a flight's ledger lines twice. |
| `Economy/` | The economy engine: `EconomyConfig` (every tuning constant for one resolved playstyle) and `EconomyConfigCatalog` (loads `economy-config.json` once and resolves the two playstyle-specific configs from it - see [Playstyles and EconomyConfigCatalog](#playstyles-and-economyconfigcatalog)), `ReferenceFareCalculator`, `DemandCalculator` (city-pair passenger pool from catchment/distance/season/day/reputation), `FareDemandModel` (the anti-exploit fare/load-factor/revenue curve - see [Fare setting and demand response](guides/user-guide.md#the-economy-simulation) in the user guide), `FuelPricing` (per-airport, per-day fuel price), `TankeringAdvisor` (whether uplifting extra fuel here to skip a pricier refuel later would pay off - see [Persistent fuel and tankering](#persistent-fuel-and-tankering)), `MaintenanceScheduler` (A/C-check triggering, downtime and used-aircraft starting wear - see [Maintenance scheduling](#maintenance-scheduling)), `FlightCostCalculator` (weight- and size-based landing/handling/parking/passenger/turnaround fees, maintenance accrual, crew cost), and `FlightEconomicsCalculator`, which ties all of the above into one itemised `FlightEconomicsResult` for a single flight. Pure - no database, no I/O. `FSOps.Server.Services.FlightEconomicsPoster` is the only place that result becomes real `LedgerTransaction` rows: fuel is posted at flight start, only for what's actually uplifted (see [Persistent fuel and tankering](#persistent-fuel-and-tankering)); every other line posts on completion, gated on `Flight.RevenuePosted` for idempotency, and skipped entirely for a slew/position-jump-flagged sector rather than computed and discarded. Recurring monthly billing (lease, salary, insurance, loan amortisation) is a separate background service, `FSOps.Server.Services.EconomyClockService` - see [The monthly billing cycle](#the-monthly-billing-cycle-economyclockservice). |
| `Flights/` | Flight tracking's pure domain logic: `FlightPhase` (the ten-phase enum), `FlightPhaseThresholds` (the speed/altitude/timing constants that drive phase transitions), `FlightPhaseStateMachine` (advances phase-by-phase from telemetry samples, captures OOOI and touchdowns, and restores itself from a stored event history), `LandingQualityCalculator` (runway-centreline deviation), `FlightIntegrityMonitor` (elevated simulation rate, slew, and position-jump detection - see [Append-only ledger](#append-only-ledger)), and `AircraftTypeMatcher` (family-level, informational-only aircraft matching). See [Flight-phase detection and landing quality](#flight-phase-detection-and-landing-quality). |
| `Routes/` | `FlightNumberGenerator` — deterministic odd/even outbound/return flight-number pairing per airline, pure and side-effect free. |
| `Money/` | `CurrencyCatalogue` (the supported-currency list, each with a fixed display rate against the GBP-pegged base unit) and `MoneyFormatter` (base-unit → display-currency conversion and formatting). See [Money is stored in a single base unit](#money-is-stored-in-a-single-base-unit). |
| `Planning/` | Route preview math: `GreatCircle` (distance/bearing), `CruiseAltitudeSelector`, `BlockTimeEstimator`, `BlockFuelEstimator` (also used by `TankeringAdvisor` to price the extra weight a tankered sector would burn), composed together by `RoutePreviewCalculator` into one preview result plus validation warnings - including the suggested fare, sourced from `Economy/ReferenceFareCalculator` (the same figure the demand engine anchors to - see docs/PLAN.md "Status after the fuel-honesty fix"). Pure, deterministic, no I/O. |
| `Finance/` | `LoanCalculator` (amortising loan monthly-payment math and the monthly interest/principal split `EconomyClockService` applies), `LoanRateCalculator` (the only code allowed to price a loan's interest rate - risk-based, scaling from the playstyle's base rate to its cap as the loan consumes more of the airline's borrowing capacity), and `LoanEligibilityCalculator` (borrowing capacity itself: 30% of trailing 30-day net operating cash flow). Used for both the startup loan at airline creation and a mid-game loan from the Fleet page. |
| `Airlines/` | `AircraftRegistrationGenerator` (country-appropriate tail number generation for a newly leased aircraft). Starting capital, lease deposit and starting pilot salary used to live here as `AirlineCreationDefaults`; they now live in `Economy/EconomyConfig.AirlineStartup` instead, alongside every other tunable economy figure, so airline-creation balance can be retuned the same way as everything else - see the `Economy/` row above. |
| `Airports/` | `AirportSearchRanking` and `AirportSizeCategoryMapper`, used by airport search and by the home-base-suitability check in airline creation. |
| `AppPaths.cs` | Runtime resolution of every path FSOps writes to. See [App paths](#app-paths-no-hardcoded-filesystem-paths). |

## API endpoint surface

All REST endpoints are versioned under `/api/v1`:

| Route group | Endpoints | Covers |
|---|---|---|
| `/airline` | `POST`, `GET`, `PUT`, `GET /summary`, `GET /strategy-profiles`, `GET /playstyles`, `GET /ledger`, `DELETE` | Founding an airline (creates the airline, leases the starter fleet aircraft, hires the first pilot, and posts opening ledger entries in one transaction, resolved against the chosen `AirlinePlaystyle` — see [Playstyles and EconomyConfigCatalog](#playstyles-and-economyconfigcatalog)), fetching/updating its identity, accent colour and strategy profile (a strategy change is going-forward only — it never touches completed flights or existing routes' fares; playstyle has no update path at all, by design), a summary including derived cash balance and counts, the live per-profile fares/sensitivity/load-factor/cost figures and per-playstyle starting-capital/lease/insurance/loan-cap figures read straight from `economy-config.json` (backs the profile and playstyle pickers in onboarding and Settings), the itemised ledger newest-first (backend visibility only - no Finances page consumes this yet), and the "start over" soft-delete. |
| `/settings` | `GET`, `PUT`, `GET /currencies` | Per-user display settings — currency, units, theme, Community folder path — created lazily on first access, and the supported-currency catalogue. Accent colour and strategy profile live on the airline itself (`/airline`), not here, since they're airline-specific rather than account-wide. |
| `/routes` | `POST`, `GET`, `PUT /{id}`, `GET /{id}`, `DELETE /{id}`, `POST /{id}/return-leg`, `POST /preview` | Route creation, which always creates both directions of a round trip in one call (see [Round trips and where your aircraft actually is](guides/user-guide.md#round-trips-and-where-your-aircraft-actually-is)); listing (self-healing — backfills a missing return leg or flight number for legacy single-leg routes); updating a route's flight number, fare, or active flag; fetching or soft-deleting a route pair together; a manual return-leg repair tool for routes that couldn't be paired automatically; and the live, throw-free preview used while picking airports, including the tankering advisory (see [Persistent fuel and tankering](#persistent-fuel-and-tankering)). |
| `/airports` | search / lookup endpoints | Backing the airport pickers in route building and airline creation, using the imported world airport/runway data. |
| `/worlddata` | `GET /status` | Reports whether world data import has finished, is in progress, and how many airports/runways were loaded — polled by the frontend on first launch. |
| `/sim` | `GET /status` | The simulator's current connection state, which source is active (`SimConnect` or `Fake`), the aircraft title last seen, and when the last telemetry sample arrived — backs the top bar's sim indicator and the Fly screen's readiness checks. |
| `/fleet` | `GET`, `GET /aircraft-types`, `POST /lease`, `POST /buy`, `GET /loans`, `GET /loan-eligibility`, `GET /loan-quote`, `POST /loans` | The Fleet page's backing data (see [Maintenance scheduling](#maintenance-scheduling) for the grounding/condition/hours fields it returns, releasing any aircraft whose downtime has elapsed first) and every way to grow the fleet beyond the founding lease: leasing or buying (new or used) an additional aircraft, and taking out a loan. `GET /loan-eligibility` and `GET /loan-quote` are read-only previews - the quote runs the exact same rate/eligibility pipeline `POST /loans` uses, so the dialog can never show a rate that disagrees with what taking the loan actually charges. |
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

## Playstyles and EconomyConfigCatalog

Every airline is created with a permanent `AirlinePlaystyle` (`Casual` or `TrueLife` - see [Playstyles](guides/user-guide.md#playstyles) in the user guide) chosen once at founding, with no update path afterwards. `EconomyConfigCatalog` is what makes this work without branching logic scattered through the economy engine: at startup it parses `economy-config.json` once, reads the file's shared base fields (fares, demand, fuel, the maintenance cycle, the used-aircraft market - identical for every airline) plus two small `casual`/`trueLife` override blocks (starting capital, lease deposit term, every aircraft type's lease rate, monthly insurance, maintenance downtime, and the loan rate base/cap), and resolves two fully-merged `EconomyConfig` instances, validating each the same way a single config would validate itself.

A playstyle is a **named set of overrides, not a code path** - nothing downstream ever asks "is this airline Casual or True-life?" Callers resolve the right `EconomyConfig` once (`economyConfigCatalog.Get(airline.Playstyle)`, typically right after loading the airline that owns the request) and hand it to the same economy engine every airline uses. This is also why `Program.cs` deliberately does **not** register a flat `EconomyConfig` singleton in DI - one existed briefly during development and was removed on purpose: every config-derived figure must be resolved per-airline through the catalogue, and a constructor that fails to resolve one is meant to fail loudly (inject `EconomyConfigCatalog` instead) rather than silently fall back to a config that might be the wrong playstyle for the airline in scope.

## The monthly billing cycle: EconomyClockService

`EconomyClockService` is a `BackgroundService` that posts the fixed monthly costs a flight-by-flight economy never touches: one lease payment per active `Lease`, one salary line per `Pilot`, one insurance line per fleet aircraft (looked up live from `EconomyConfigCatalog` against the owning airline's playstyle, since insurance is the one figure that isn't already baked into a stored row), and the amortised interest/principal split (`LoanCalculator.ApplyMonthlyPayment`) for every outstanding `Loan`. It runs once immediately at startup - so a catch-up after being closed happens right away rather than up to a minute later - and then every 60 seconds after that.

A "month" is exactly 30 days of wall-clock time from `EconomyState.LastProcessedUtc` (a single-row watermark), not a calendar month. Each pass advances the watermark one 30-day period at a time, posting that period's charges and moving the watermark past it in the *same* `SaveChangesAsync` call - so a period's ledger rows and the watermark that says "this period is done" either both land or neither does; a crash mid-catch-up can lose at most the not-yet-committed remainder of that pass, never post a half-charged or double-charged month. A `SemaphoreSlim` also serialises overlapping calls within the process, the only place two "ticks" could ever race.

Two integrity properties matter here, both deliberate:

- **Catch-up is bounded.** If FSOps was closed for a long time, one pass posts at most 24 periods (about two years) before yielding back to the next 60-second tick - an unbounded closure doesn't mint an unbounded burst of charges in a single pass, but it does still catch up fully within a handful of passes.
- **The watermark only ever moves forward, and never past the current clock reading.** A clock reading that comes back *before* the watermark (a wound-back system clock) is treated as a backwards jump: nothing is processed, the watermark doesn't move, and it's logged rather than silently absorbed. Winding the clock *forward* can't mint more than the 24-period cap per pass either. This is what makes the wall-clock model resistant to clock manipulation in either direction.

## Persistent fuel and tankering

`FleetAircraft.FuelOnBoardKg` is a real, persisted quantity - the aircraft's tank state carries forward between flights rather than resetting every sector. `FlightEndpoints.StartAsync` reconciles it against reality at the start of every flight:

- **With a recent telemetry sample** (the sim is connected and has reported within the reconciliation window), that reading is trusted as ground truth. `FuelUpliftDetector` classifies the change since the last tracked figure: a rise is charged as an uplift, at the departure airport's price; a fall is silently absorbed as consumed, never credited. This is what catches fuel that changed while FSOps wasn't watching - a sim restart, a menu fuel set, or the pilot topping off the tank before pressing Start flight.
- **With no recent sample** (sim not connected, or the flight will only ever be completed manually), FSOps falls back to a conservative assumption: top up to exactly this sector's own `ChargedFuelKg` (trip + taxi + contingency - not the alternate/reserve allowance) if the tank doesn't already hold that much.

Either way, a sector flown on fuel already in the tank - most commonly the return leg of a route just flown outbound - posts no `Fuel` ledger line at all. `FlightLifecycleService` keeps `FuelOnBoardKg` in sync with live telemetry for the rest of the flight and writes the final in-tank figure back on landing/abandonment.

`TankeringAdvisor` (`FSOps.Core.Economy`) is a pure, advisory-only calculator surfaced on the Fly screen's flight brief: it compares uplifting extra fuel now (at the departure price, but burning a little more of it per the `Fuel.CostOfCarryRatePerHour` config constant - roughly 3% of the *extra* mass carried, per hour airborne) against buying nothing extra and refuelling at the destination's price, and flags if the extra fuel would exceed the aircraft's MTOW. It never touches the ledger itself. One documented gap: `FlightCostCalculator.LandingFee` is keyed off the aircraft type's fixed `MtowTonnes`, not the aircraft's actual operating weight at landing, so carrying tankered fuel does not raise the landing fee it will pay - cost-of-carry burn is the only counterweight the model can currently apply.

## Maintenance scheduling

`MaintenanceScheduler` (`FSOps.Core.Economy`) is pure arithmetic: given a fleet aircraft's current hours/condition and the flight hours it has just flown, it decides whether an A-check (every 500 airframe hours) or a C-check (every 4,000) fires, what it costs, how long it grounds the aircraft, and the new hours/condition either way. Condition decays a small, configured amount per flight hour; an A-check partially restores it (35 points, capped at 100) while a C-check always resets it to 100 outright, since the shipped config makes the C-check interval a whole multiple of the A-check interval, so a C-check-due moment is always also an A-check-due moment and the two cycles reset together rather than scheduling a redundant A-check moments later. Downtime hours are the one maintenance figure that differs by playstyle (Casual: a handful of hours for an A-check, about a day for a C-check; True-life: about a day, and about a fortnight, respectively) - everything else about the cycle (interval hours, cost, condition decay/restore) is identical for both.

`MaintenancePoster` (`FSOps.Server.Services`) is the single call site both flight-completion paths (the live telemetry path in `FlightLifecycleService` and the manual-completion path in `FlightEndpoints`) use to apply the scheduler's result: it bumps `AirframeHours`, and if a check triggered, grounds the aircraft (`FleetAircraftStatus.InMaintenance`, with `GroundedUntilUtc` set so the UI can say not just "in maintenance" but until when), writes a `MaintenanceEvent` row, and posts the cost as a `LedgerTransaction`. `MaintenanceReleaser` is the wall-clock counterpart: rather than a dedicated background timer, it's called lazily at the top of every endpoint that reports or depends on fleet availability (`GET /fleet`, `GET /flights/options`, `POST /flights/start`) and releases any aircraft whose `GroundedUntilUtc` has already passed, so the very next request after a grounding period ends always sees it correctly released.

Buying a **used** aircraft (`FleetEndpoints.BuyAsync`) uses the same scheduler, via `MaintenanceScheduler.ResolveUsedAircraftState`: a used airframe costs 55% of the new price but starts 70% of the way into both maintenance cycles and at 70% condition rather than 100% - the discount is repaid through an earlier trip to maintenance, not given away for free.

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
