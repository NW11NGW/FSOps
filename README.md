# FSOps

**Build and fly your own virtual airline in Microsoft Flight Simulator 2024.**

FSOps is a Windows companion app for MSFS 2024. Found an airline, pick a home base, a playstyle and a strategy, build out a route network, and fly it — FSOps tracks every flight live against the simulator and runs a proper economy underneath it, itemising exactly what each sector earned and cost, and billing lease, salary, insurance and loan payments on a real-world monthly cycle. Hire virtual pilots and give them a standing weekly schedule, and your airline keeps flying — and earning and spending money — on the real-world clock even while FSOps is closed, catching up on everything it missed the next time you open it. See [Status](#status-in-development) below for what's real today.

## Status: in development

FSOps is being built in the open, in public view, one feature at a time. Founding an airline, planning a route network, flying a route with full live tracking through MSFS, running a fleet (buying, leasing, selling, used aircraft, loans, maintenance), a monthly billing cycle that keeps charging you whether or not you're flying, hiring virtual pilots to fly a standing weekly schedule on the real-world clock, and a Finances page for inspecting all of it, all work today. Statistics dashboards and the in-game panel are not available yet — see the [user guide](docs/guides/user-guide.md) for what's built and what's coming.

## Headline features

- **Found your airline** — a full-screen setup wizard walks you through naming your airline, picking a home base airport, a playstyle (Casual or True-life — permanent for that airline's life), a strategy (international, domestic, low-cost, premium, or the balanced all-rounder), an accent colour and starter aircraft, your currency and units, and an optional startup loan. Strategy can be changed any time from Settings → Airline; playstyle cannot — switching means deleting the airline and starting over.
- **Two playstyles, chosen once** — Casual keeps starter costs low (a £30,000/month starter lease, £6,000/month insurance, a one-month deposit, £2,000,000 starting capital) so one aircraft flown occasionally is still profitable. True-life charges realistic figures (roughly £380,000/month lease, £50,000/month insurance, a two-month deposit, £2,500,000 starting capital) and genuinely depends on hiring virtual pilots to fly standing schedules. Both run the same economy engine — only the numbers and a few behaviours (maintenance downtime, loan rate ceiling, and whether an unflyable scheduled flight is skipped or charged a cancellation fee) differ.
- **A real monthly billing cycle** — lease payments, pilot salaries, insurance and loan repayments post automatically every 30 days of real-world wall-clock time, with catch-up for however long FSOps was closed and a watermark that can't be tricked by winding your system clock forward. An idle airline still loses money every month, exactly like a real one.
- **Fleet finance, both ways** — buy an aircraft outright (new or used, at 55% of the new price but worn 70% of the way toward its next checks) or lease one, from the Fleet page. Loans are available too: the interest rate is computed automatically from how much of your airline's trailing 30-day cash flow the loan would consume, never chosen by you, capped at 5% (Casual) or 8% (True-life) APR, and a startup loan taken while founding your airline is capped outright at £250,000 (Casual) or £5,000,000 (True-life) so the wizard can't hand a new player an unpayable loan. Acquisition isn't one-way any more: **sell** an owned aircraft (at a depreciated value that always loses money on a same-day round trip) or **end a lease early** (a pro-rata charge for the part-period used plus a fee), and settle a loan **fully or partially** ahead of schedule. All three show a firm quote first, and refuse and re-quote rather than charge a different figure if it moved before you confirm.
- **Aircraft reservation is a hard rule, not a hint** — you can only fly an aircraft that's reserved to you, and a reserved aircraft is never offered to a virtual pilot's schedule. Reserving and releasing on the Fleet page is the one control that moves an aircraft between the two pools; reserving one that already has scheduled legs is refused, naming them, unless you choose to clear them.
- **Maintenance, on your terms as well as its own** — A-checks every 500 flight hours and C-checks every 4,000 ground an aircraft for a stated period (a few hours to a day under Casual, up to a fortnight for a True-life C-check) and restore its condition; it can never happen mid-flight, only at shutdown. A virtual pilot's schedule suspends itself automatically while its aircraft is grounded and resumes the moment it's released, with no cancellation fee — and you can trigger a check early yourself from the Fleet page, at full cost, trading the hours you'd have had left for choosing exactly when the downtime lands.
- **Persistent fuel and tankering** — fuel is a real quantity carried on each aircraft between flights, charged only when you actually uplift it. A return leg flown on fuel already in the tank costs nothing further; the Fly screen flags when it's worth uplifting extra here to skip a pricier refuel at the other end, weighed against the extra weight you'd burn carrying it.
- **A comprehensive aircraft catalogue** — 25 real airliner types from regional turboprops through widebodies (Embraer E-Jets, ATR, Dash 8, the A320 and 737 families, A330/A350/A380, 767/777/787/747), searchable by ICAO type, manufacturer or name and filterable by narrowbody/widebody/regional, each with a real lease rate and purchase price for both playstyles. Registrations are generated in the format your airline's home country actually uses (`G-EZBA` for a UK-hubbed airline, `N737FS` for a US one, and so on) or you can type your own to match a livery.
- **Plan a round-trip route network** — search the airport database, pick a departure and arrival, and see a live plan: great-circle distance and bearing on an interactive map, block time and cruise altitude, a full fuel breakdown, and a suggested fare you can override before creating the route. Creating a route always creates both directions, with paired flight numbers, so your aircraft is never stranded at the outstation. Every saved route draws as a curved great-circle line on your network map, with your hub marked distinctly.
- **Fly a tracked flight** — pick a route on the Fly screen, review the flight brief (distance, cruise altitude, block time, block fuel breakdown), then start flying. FSOps connects to MSFS 2024 via SimConnect, auto-reconnecting whenever the sim isn't running, and tracks your flight live: phase detection (preflight through shutdown), a moving map, and live readouts of altitude, speed and fuel.
- **Landing quality scoring and report cards** — every landing is graded on touchdown rate, peak G-force, bounce count, and deviation from the runway centreline. A post-flight report card compares your actual block time and fuel burn against the plan and shows the full phase timeline with captured OOOI times.
- **Plan in SimBrief** — one click opens SimBrief with your flight prefilled (origin, destination, aircraft type, airline, flight number, registration); nothing is sent anywhere except to SimBrief's own site in your browser.
- **Aircraft-type awareness** — FSOps checks the aircraft you're actually flying against the route's expected aircraft at family level (a 737-800 matches a 737-700 route); a mismatch is flagged for information only and never affects payment.
- **Settings that stay out of your way** — currency, distance/altitude/weight units, time display, theme and accent colour are all adjustable; changing currency only changes how numbers are displayed, never your actual balance.
- **A real airport database** — world airport and runway data imports automatically on first launch, backing route search, runway-length checks, and range validation against your fleet.
- **A deep economy simulation** — passenger demand and price elasticity decide who actually books your fare (load factor is capped around 92% no matter how cheap you go), and every flight posts itemised ticket revenue, fuel (charged on uplift, not burn), landing/handling/parking/passenger fees, maintenance accrual, and crew cost to your airline's append-only ledger. There's no fare cap — the simulation is the guardrail: price too high and passengers, and eventually the market itself, evaporate rather than rewarding you for gouging a captive handful of them. A slew or teleport during tracking voids that sector's revenue outright, never quietly discounts it.
- **Hire virtual pilots and build their week around an aircraft, not a leg** — from the Pilots page, hire as many pilots as you want at a standard monthly salary (no upfront cost), then give each one a repeating weekly schedule: pick the aircraft for a duty day first, then drop the legs it flies into a diary-style grid (time down, days across), with by-pilot and by-aircraft views plus a read-only, colour-coded overview of your whole fleet's week. Anything that can't fly shows disabled with one plain reason and a link that fixes it, rather than vanishing or dropping a wall of text. Their flights are full economic citizens: they earn ticket revenue and pay every real cost — fuel, landing/handling fees, maintenance accrual, crew cost, their own salary — the same way a flight you fly yourself does, and they advance the aircraft's hours, wear and position exactly as a player flight would. Your own flying hours now count too, on the Pilots page, the same as any virtual pilot's.
- **A wall-clock economy that runs while you're away — and explains itself when you get back** — virtual pilots' flights complete against the real-world clock, not the time you spend with FSOps open. Close the app for a few hours, a few days, or longer, and reopen it: every flight that was due, and every monthly bill, has already been resolved and posted, all the way up to the moment you closed it, capped so a very long gap catches up over a few passes rather than in one enormous burst. It can't be tricked by winding your system clock forward or back either. If enough happened while you were gone, a "while you were away" summary greets you on startup — what was charged, what your pilots flew and earned, maintenance that fell due, and anything skipped, cancelled or suspended, with its reason.
- **A Finances page** — cash balance and trend, income against expenditure for the current period, every lease with its real next-payment date (a rolling 30 days from your airline's own clock, not the calendar) and an end-lease action, every loan with full or partial early repayment, per-pilot revenue versus cost, a fixed-versus-variable cost split, a filterable itemised ledger, and profit and loss per route. Figures that are estimates (a pilot's salary prorated to the window shown) are labelled as such; everything else comes straight from the ledger.
- **A live operations map on the Dashboard** — see your whole route network plus every aircraft currently airborne, virtual pilots included (their position is calculated from the schedule and elapsed time, not stored), with your own tracked flight shown distinctly from theirs. Hover any aircraft for a flight card: flight number, route, pilot, aircraft, departure/arrival times, and percentage complete.
- **In-game panel** *(coming in a later update)* — see live airline stats without leaving MSFS.
- **Statistics dashboards** *(coming in a later update)* — routes flown, on-time and landing-quality trends, fleet utilisation and financial performance over time, in one place.

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
│   │   ├── Finance/      #   Loan, disposal (sale/lease-termination) and loan-settlement calculations
│   │   ├── Flights/      #   Flight-phase state machine, landing quality, aircraft-type matching
│   │   ├── Money/        #   Currency catalogue and base-unit formatting
│   │   ├── Planning/     #   Route preview: distance, bearing, block time, fuel, fare
│   │   ├── Routes/       #   Flight number generation
│   │   └── Scheduling/   #   Weekly schedule validation (aircraft-per-duty-day), occurrence timing, pilot performance
│   ├── FSOps.Data/       # EF Core + SQLite persistence, world data import
│   ├── FSOps.Sim/        # Sim abstraction: the SimConnect adapter and a replay-based fake source
│   ├── FSOps.Server/     # API endpoints (fleet, disposal, maintenance, finance, pilots, flights, ...), SignalR hubs, flight lifecycle/virtual-flight-resolver/economy-clock/reservation-reconciler services, and serves the built web UI
│   └── fsops-web/        # React + TypeScript + Vite + Tailwind + shadcn/ui frontend
│       └── src/components/
│           ├── flight/   #   Fly screen pieces: route selector, brief, live view, report card
│           ├── schedule/ #   A virtual pilot's weekly schedule builder (aircraft-per-duty-day, by-pilot/by-aircraft/overview views)
│           ├── finance/  #   Finances page sections: leases, loans, per-pilot P&L, cost split, routes P&L, ledger
│           ├── fleet/    #   Fleet page: buy/lease, reservation, sell/end-lease dialogs
│           ├── maintenance/ # Perform-maintenance-now and "while you were away" dialogs
│           └── map/      #   Route network, live-flight and live-operations maps (MapLibre GL)
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
