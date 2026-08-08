# User Guide

This guide covers how to use FSOps, feature by feature. FSOps is under active development: founding an airline, adjusting settings, building a route network, and flying a fully tracked flight all work today and are described below as they actually behave. Features that aren't built yet are clearly marked **Coming in a later update**.

## Table of contents

- [Current build](#current-build)
- [Creating your airline](#creating-your-airline)
- [Settings](#settings)
  - [Airline](#airline)
- [Building routes](#building-routes)
  - [Your route network on the map](#your-route-network-on-the-map)
  - [Round trips and where your aircraft actually is](#round-trips-and-where-your-aircraft-actually-is)
- [Where your data lives](#where-your-data-lives)
- [Planning and flying a tracked flight](#planning-and-flying-a-tracked-flight)
  - [Connecting to the simulator](#connecting-to-the-simulator)
  - [Picking a route to fly](#picking-a-route-to-fly)
  - [The flight brief](#the-flight-brief)
  - [Readiness checks](#readiness-checks)
  - [Starting the flight and the live view](#starting-the-flight-and-the-live-view)
  - [Ending a flight, and what happens if it gets interrupted](#ending-a-flight-and-what-happens-if-it-gets-interrupted)
- [Plan in SimBrief](#plan-in-simbrief)
- [Reading the post-flight report card](#reading-the-post-flight-report-card)
  - [Flight integrity](#flight-integrity)
- [The economy simulation](#the-economy-simulation)
- [Hiring and assigning virtual pilots](#hiring-and-assigning-virtual-pilots)
- [Buying vs leasing aircraft](#buying-vs-leasing-aircraft)
- [Maintenance](#maintenance)
- [Statistics dashboards](#statistics-dashboards)
- [In-game panel](#in-game-panel)
- [A worked example, start to finish](#a-worked-example-start-to-finish)

## Current build

Start the backend, open `http://localhost:5977` in your browser, and — the very first time, once world data has finished importing — you'll land in the airline setup wizard. From there you can found an airline, adjust settings, build out a route network, and fly a route with full live tracking against MSFS, ending in a post-flight report card that shows exactly what it earned and cost. Virtual pilots, fleet purchasing, maintenance, and statistics are still being built; each is marked below.

## Creating your airline

FSOps opens straight into a full-screen setup wizard whenever no airline exists for your machine yet. See [Getting Started](getting-started.md#6-found-your-airline) for the step-by-step flow. This section covers what each choice actually means.

### Strategy profiles

You choose a strategy when you found your airline, but it's never permanent — pick a different one at any time from **Settings → Airline**. Your strategy drives three things at once: the fare suggested for every route you plan (see [Building routes](#building-routes)), how sharply demand responds to your pricing (see [The economy simulation](#the-economy-simulation)), and which route-length advisories you'll see in the plan panel:

- **International** — a long-haul network carrier. Suggested fares carry a **1.15×** multiplier over the baseline. Demand is relatively insensitive to price (of the five profiles, only Premium is less price-sensitive), and the route planner warns if you add a short domestic hop — it doesn't match the strategy, though the warning is advisory and won't stop you.
- **Domestic** — a short-haul regional workhorse, priced at the baseline (**1.0×** multiplier). The route planner warns if you add an international route — again, advisory only.
- **Low-cost** — a high-frequency budget carrier. Suggested fares are discounted to **0.75×** the baseline — the lowest of the five profiles — but demand is the most price-sensitive of any profile, so a low-cost route depends on volume, not fare, to make money. Costs run lower too (service-level charges like handling and passenger fees are discounted 15%).
- **Premium** — a business-focused yield carrier. Suggested fares carry the highest multiplier, **1.6×** the baseline, reflecting a smaller, higher-paying passenger base with the lowest typical load factor of the five. Service-level costs run higher too (a 35% surcharge), reflecting a higher service standard.
- **Balanced** — a neutral all-rounder, priced at the baseline like Domestic. It's the only profile that raises no route-suitability advisories at all — an international route or a short domestic hop is fine either way. Its price sensitivity sits strictly between Domestic and Premium, and its typical load factor sits at the midpoint of the other four profiles' range, by design: not a hidden best choice, just the option with no directional bias.

Fuel and landing fees never change by strategy — they're physical, regulatory charges, identical for every airline flying the same aircraft into the same airport. Only service-level charges (handling, parking, passenger fees) and reference fares carry a strategy multiplier. The figures shown on each strategy's card during onboarding or in Settings are read live from FSOps' own configuration, not a separate hand-written description, so they can't drift out of sync with what the economy engine actually does.

### Starter aircraft

You choose between an Airbus A320 (180 seats, 3,300 nm range) and a Boeing 737-800 (189 seats, 3,115 nm range) as your airline's first aircraft. It's **leased**, not bought — founding your airline posts a one-month lease deposit (**£30,000** for either type) from your starting capital (or top-up loan) rather than the aircraft's full multi-million-pound price, which is what makes starting an airline affordable. This starter lease rate is a deliberate game-balance figure rather than a realistic one (a real A320 or 737-800 lease runs closer to £350,000–£420,000 a month) — see [The economy simulation](#the-economy-simulation) for why. Recurring lease payments aren't billed on a schedule yet, so beyond that first deposit nothing further is charged for it today. It determines which routes you can create until you add more aircraft to your fleet: a route beyond this aircraft's practical range can't be created (see [Building routes](#building-routes)).

## Settings

Reachable from the main navigation once your airline exists. Most of Settings applies to your account (currency, units, theme, Community folder) rather than to a specific airline — the exception is the **Airline** section below, which edits your airline's own identity and strategy.

### Airline

Your airline's name, accent colour, and strategy all live here and can be changed at any time (home base is shown for reference but is fixed once you've founded your airline):

- **Name** — 2 to 40 characters, the same limits as at founding.
- **Accent colour** — a preset swatch or a custom hex value, used throughout the UI for highlights and selected states.
- **Strategy** — see [Strategy profiles](#strategy-profiles) above for what each one means. Changing it here is **going forward only**: it changes the fares and demand suggested for new routes and which routes get an advisory note from here on, and never touches completed flights, posted ledger entries, or the fares already set on your existing routes. Each profile's card shows its real fares, price sensitivity, typical load factor, cost and route-advice figures, fetched live — if that fetch fails (for example, right after FSOps was updated while running), the cards say so and offer a retry, and choosing a profile still works even without the figures loaded.

### Currency

FSOps stores every amount — cash balance, fares, purchase prices, loan payments — in a single base currency unit (GBP-pegged) inside the database. The currency you pick here is a **display conversion only**: every screen multiplies the stored amount by that currency's fixed display rate and formats it with the right symbol and decimal places. Changing your currency in settings changes how numbers look everywhere in the app; it never changes your actual balance or rewrites any stored figures. FSOps isn't a forex simulator — display rates are fixed, not fetched or refreshed.

### Units

- **Distance** — nautical miles or kilometres, used for route distances.
- **Altitude** — feet or metres, used for cruise altitude.
- **Weight** — kilograms or pounds, used for fuel weights.
- **Time display** — UTC or local time, plus a 24-hour/12-hour clock toggle.

### Theme

Switch between light and dark theme at any time.

### Community folder

An optional path to your MSFS Community folder, stored for future features that read installed aircraft/liveries from it. Setting it doesn't change anything else about the app yet.

### Danger zone: start over

Settings includes a **Delete airline** action that permanently removes your airline, fleet, routes, pilots, and financial history — there is no undo. After confirming, you're returned to the setup wizard as if FSOps had never had an airline on this machine. The append-only financial ledger itself isn't purged (it's a historical record, harmless once its airline is gone) but nothing in the UI will reference it any more.

## Building routes

Reachable from the main navigation once your airline exists. This is where your strategy and home base turn into an actual flying network.

### Picking departure and arrival

Search the airport database (imported on first launch) for a departure and arrival airport. Once both are picked, an interactive map shows the great-circle path between them, and a live plan panel updates as you change either airport.

### Reading the plan panel

The plan panel shows, live, as soon as both airports are picked:

- **Distance** — great-circle distance between the two airports, in your chosen unit.
- **Initial bearing** — the compass heading out of the departure airport.
- **Block time** — estimated gate-to-gate time, broken down into taxi-out, climb, cruise, descent, and taxi-in phases (viewable in a tab under the main stats).
- **Cruise altitude** — the altitude the planner selects for this route, based on distance, bearing, and the aircraft's service ceiling.
- **Block fuel** — total estimated fuel burn, broken down into trip, taxi, contingency, alternate, and final-reserve fuel (viewable in the second tab).
- **Suggested fare** — distance × £0.12/nm × your strategy's fare multiplier (see [Strategy profiles](#strategy-profiles) above), with a **£65 floor** — no suggested fare ever comes in under that, however short the hop, so a short domestic sector doesn't get suggested a fare too small to cover its fixed per-sector costs. This becomes the fare override field's starting value.

### Warnings

Two kinds of message can appear under the stat tiles:

- **Blocking (red)** — same departure and arrival airport, or the route is beyond the aircraft's practical operating range (about **0.85×** its published range once fuel reserves are accounted for). Either of these disables route creation until you change your airport picks.
- **Advisory (amber)** — a runway at either airport may be too short for the aircraft, or the route doesn't match your airline's strategy (an international route on a Domestic strategy, or a short domestic hop on an International strategy). These are shown for your attention but **do not block creating the route** — you can create it anyway if you're confident it'll work.

### Creating a route with a fare override

The fare field under the plan panel starts pre-filled with the suggested fare. Edit it to set your own fare instead — enter a value that's a sane multiple of the suggested fare (very roughly, no less than a tenth and no more than ten times the suggestion); wildly out-of-range values like zero or a fare thousands of times the suggestion are rejected with an explanation. Leave the field untouched and the route is priced at the suggested fare automatically. Select **Create route** once the plan looks right and no blocking warning is showing.

### Managing the route list

Every route you've created is listed below the plan panel and map, showing both airports, distance, estimated block time, fare, and status. Selecting a row loads that route's airports back into the picker and map so you can review it. Routes can be deleted from the list; deleting is a soft delete — the route stops appearing and can't be flown, but its record isn't destroyed.

### Your route network on the map

The same map used to preview a new route also draws your whole network at once, so the Routes page doubles as a route-network view. Your home base airport is marked distinctly from every other airport on the map — a larger marker with a highlighted ring around it — so you can always pick out your hub at a glance. Every route you've saved draws as its own curved great-circle line between its two airports, in your airline's accent colour; because a route is always a there-and-back pair (see below), the outbound and return legs of the same city pair are drawn as a single line rather than two overlapping ones.

Selecting a route — either by clicking its line on the map or its row in the list below — highlights that route with a glow and brings the camera in to fit it, so you can see exactly which two airports it connects even in a large network. The map's background imagery needs an internet connection to load; if you're offline, route lines, airport markers, and everything else drawn from your own data still work fine — see [Troubleshooting](troubleshooting.md#the-map-shows-no-background-tiles).

### Round trips and where your aircraft actually is

Every route you create in FSOps is a **round trip**. Creating a route from, say, EGGD to EGPH automatically creates the EGPH-to-EGGD return leg alongside it in the same action — you'll never end up with only an outbound route and no way back. The two legs get paired flight numbers following the common real-world convention: the outbound leg gets the next free **odd** number in your airline's series, and the return leg gets the next **even** number after it (an outbound 101 pairs with a return 102). Both legs share the same fare unless you override one. Deleting a route removes both legs together, for the same reason — leaving one leg behind would strand your aircraft at the outstation with no route home. Because of this pairing, your route count everywhere in FSOps (your airline summary, the Dashboard) counts round trips, not individual legs — three round trips show as 3, even though there are 6 directional route rows underneath.

This pairing exists because of how FSOps thinks about your fleet: an aircraft is always sitting at a specific airport, and you can only fly a route whose departure airport matches where your aircraft actually is. This is what the [Fly screen's "Ready now" grouping](#picking-a-route-to-fly) is checking — a route with no aircraft currently at its departure airport shows as not flyable, with the reason stated plainly (for example, "No aircraft at EGPH — your fleet is currently at EGGD"). A newly founded airline's starter aircraft begins at your home base, which is why your first flight is always one of the routes leaving your hub.

A completed flight moves its aircraft's recorded location to wherever it actually landed — not just to the route's expected arrival airport, but to whichever airport the touchdown was actually closest to (within about 5 nm), so a diversion is reflected honestly. That's what makes the pairing above actually useful in practice: fly the outbound leg of a round trip and the return leg immediately shows as flyable from the far end, because your aircraft is now really there. An abandoned flight, by contrast, leaves the aircraft exactly where it was — nothing moves for a flight that never completed. See [Troubleshooting](troubleshooting.md#a-route-doesnt-show-as-flyable) if a route you expect to be flyable isn't.

## Where your data lives

FSOps stores everything — your airline, fleet, routes, pilots, flights, and financial ledger — in a SQLite database under `%LOCALAPPDATA%\FSOps\` on your machine, alongside FSOps' log files. See [Architecture](../architecture.md#app-paths-no-hardcoded-filesystem-paths) for why it lives there rather than next to the application files.

This means:

- Your data survives FSOps updates — reinstalling or upgrading the app doesn't touch it.
- Nothing is stored anywhere else; there's no account or server involved.
- **Backing up** your airline is as simple as copying the `%LOCALAPPDATA%\FSOps\` folder somewhere safe. Restoring is copying it back.
- Deleting that folder (or using the settings [danger zone](#settings)) resets FSOps to a blank slate — see [Troubleshooting](troubleshooting.md) if that happens unexpectedly.

## Planning and flying a tracked flight

This is the core loop of FSOps: pick a route on the **Fly** screen (main navigation), review the brief, start the flight, then fly it in MSFS while FSOps tracks it live over SimConnect. No manual phase reporting is ever needed — every phase, timestamp, and landing measurement is read directly from simulator state.

### Connecting to the simulator

FSOps needs a live SimConnect connection to track anything, so make sure MSFS 2024 is running and you're loaded into a flight (on the ground or in the air — the main menu doesn't expose live data) before you expect tracking to start. See [Getting Started](getting-started.md#7-connect-to-msfs) for the full connection walkthrough. Two small pills in the top-right of the top bar, next to your cash balance, show connection state at all times: one for FSOps' own live-update link to its backend, one for the simulator itself ("Sim connected" in green, "Sim offline" otherwise). If the sim drops out mid-flight, FSOps keeps retrying the connection on its own — see [Ending a flight, and what happens if it gets interrupted](#ending-a-flight-and-what-happens-if-it-gets-interrupted) below for what happens to a flight that was in progress when that happens.

### Picking a route to fly

The Fly screen opens on a **"Choose a route"** card listing every route your airline has built (see [Building routes](#building-routes)). If you have more than a handful of routes, a search box lets you filter by airport code, airport name, or flight number. Routes are grouped by whether you can actually fly them right now:

- **Ready now** — routes with a fleet aircraft physically sitting at the departure airport, marked with a green badge.
- **Other routes** / **Not flyable right now** — routes with no aircraft currently at the departure airport, each showing the specific reason (for example, that your fleet's aircraft is at a different airport, currently in flight, or in maintenance). See [Round trips and where your aircraft actually is](#round-trips-and-where-your-aircraft-actually-is) for how an aircraft's recorded location changes as you fly.

Each row shows the airport pair, flight number, distance, estimated block time, and which aircraft (by registration or type) is available. There's currently no "free flight" option for flying something outside your route network — you can only fly routes you've built on the Routes page.

### The flight brief

Selecting a flyable route opens the **"Flight brief"** card. If more than one aircraft is available for the route, you can choose which one to fly as a set of chips; otherwise FSOps uses your fleet's first available aircraft automatically. Below that sit six figures at a glance: **distance**, **cruise altitude**, **block time**, **block fuel**, **passengers**, and **expected revenue** — the same demand-model figures shown while planning the route (see [The economy simulation](#the-economy-simulation)), evaluated fresh for today. Passengers and revenue here are still an estimate: the actual ticket revenue is only booked and posted once the flight completes, and if you started the flight but demand ticks over to a new day before you land, the posted figures come from whatever the market looks like at completion, not at the moment you checked the brief.

A tabbed panel breaks two of those figures down further:

- **Block time** — taxi-out, climb, cruise, descent, and taxi-in, plus the total.
- **Fuel** — trip fuel, taxi fuel, contingency, alternate, and final reserve, plus the total. This is the same breakdown described under [Building routes](#building-routes) — the flight brief is just that route's plan, applied to the aircraft you're about to fly.

### Readiness checks

Below the brief, a **Readiness** section runs three checks before you fly. All three are purely informational — none of them block **Start flight**, so you're free to launch anyway and see what happens:

- **Simulator connection** — whether MSFS is currently reachable, and which source FSOps is reading from.
- **Aircraft loaded in sim** — whether the aircraft you're actually sitting in matches the route's expected type. This is the same family-level check used in the report card's aircraft-type badge (see [Reading the post-flight report card](#reading-the-post-flight-report-card)) — a 737-800 loaded on a 737-700 route reads as a match, since the check works at the aircraft family level, not the exact variant.
- **Parked at departure** — whether your aircraft's live position is on the ground and within about 3 nautical miles of the route's departure airport.

Alongside readiness, this is also where the **Plan in SimBrief** hand-off lives — see [Plan in SimBrief](#plan-in-simbrief) below. A prominent **Start flight** button begins tracking.

### Starting the flight and the live view

Once started, the page becomes a live view: a header showing the route and an **Abandon flight** button, a horizontal timeline of all ten flight phases (**Preflight, Taxi out, Takeoff, Climb, Cruise, Descent, Approach, Landed, Taxi in, Shutdown**) that fills in as you progress, a moving map with your aircraft's position and heading, and a side panel showing:

- **Block time** — elapsed time against the planned block time, with a progress bar and a running "on schedule" / "N min ahead of plan" / "N min behind plan" readout.
- Live readouts of **altitude, indicated airspeed, ground speed, vertical speed, heading**, and **fuel remaining**.

If FSOps' live connection or the simulator link drops while you're flying, a banner explains that tracking is paused and will resume automatically — nothing you've flown so far is lost.

### Ending a flight, and what happens if it gets interrupted

A flight ends on its own once FSOps detects engines off and the parking brake set at the end of taxi-in — no button needed. From there, the report card is generated automatically (see below).

If you need to stop early, **Abandon flight** asks you to confirm ("Abandon this flight? This can't be undone") before discarding it — no report card is produced for an abandoned flight, and your aircraft is freed up to fly something else again.

If FSOps or MSFS is closed mid-flight and reopened, FSOps rebuilds the flight's state from its stored event history (see [Architecture](../architecture.md#the-append-only-flight-event-log-and-crash-recovery)) and waits briefly for the simulator to reconnect near where the flight left off. If that reconnection doesn't happen quickly enough, the flight is marked as needing your attention rather than silently guessed at or lost, and you're offered three ways to resolve it: **check again** (if MSFS just needed a moment to catch up), **complete with estimates** (closes the flight out using your planned figures — no landing quality is recorded for it), or **abandon it** outright.

## Plan in SimBrief

If you use SimBrief for flight planning, the **Plan in SimBrief** button (found in the flight brief's readiness area) opens SimBrief's dispatch page in a new browser tab with your flight already filled in — origin, destination, aircraft type, your airline's code, flight number, and aircraft registration, wherever FSOps has each of those. Anything FSOps doesn't have falls back to your own SimBrief defaults. This is a plain link to SimBrief's own site — nothing about your flight is sent anywhere else, and FSOps performs no authentication of its own; you just need to already be signed in to SimBrief in whichever browser tab it opens for the plan to actually generate.

A second button, **Copy summary**, copies a short plain-text summary of the flight (route, distance, cruise altitude, block time, block fuel, aircraft) to your clipboard — handy for pasting into an aircraft's FMS or a paper plan if you're not using SimBrief.

## Reading the post-flight report card

Once a tracked flight ends, FSOps generates a report card automatically — no action needed, and it opens straight from the Fly screen when the flight completes, or from your flight history afterwards.

The report card's header shows the route, the airports' names, and a status badge, followed by:

- **Landing quality**, headed by the **touchdown rate** — vertical speed at the moment of touchdown, in feet per minute. As a rule of thumb: roughly **0 to -200 fpm** reads as a smooth, well-judged touchdown; **-200 to -400 fpm** is graded a firmer landing; beyond **-400 fpm**, it's graded a hard landing. Alongside it: **peak G-force** (the highest G reading in the few seconds around touchdown, a secondary smoothness signal), **bounces** (how many times the aircraft touched down before settling — 0 for a clean landing), and **centreline deviation** — how far off the runway's centreline, in metres, you touched down. If a flight had no captured touchdown (for example, one completed with estimates after an interruption), this section says so plainly instead of showing invented numbers.
- **Phase timeline** — the same ten-phase timeline from the live view, now showing the actual clock time each OOOI milestone (out, off, on, in) was captured.
- **Actual vs. planned** — your actual block time and fuel burn set directly against what the flight brief predicted, each with a plain-language delta ("N minutes ahead of schedule", "N kg over plan", and so on).

### Flight integrity

This card only appears when it has something to say — most flights won't show it at all. It covers two independent things, neither of which is an accusation:

- **Time acceleration** — if you sped up simulation time during the flight, the card says so (and how far, e.g. "up to 4.0x") and marks block time and on-time performance **not measured** rather than scored: elapsed wall time means nothing once the sim clock runs faster than real time. Landing quality is unaffected, since it's read from the sim's own instantaneous touchdown telemetry, not from elapsed time — accelerating through a long cruise is normal single-player behaviour and doesn't cost you anything.
- **Slew or a position jump** — if slew mode was active, or telemetry showed a position change no real aircraft could have made between two samples, the card flags it and states plainly that **this sector isn't valid for payment**. This isn't a penalty bolted on afterwards — the ledger-posting code simply never reaches the step that would add ticket revenue for a flagged sector (see [The economy simulation](#the-economy-simulation) below). Fuel already bought before departure stays charged either way, exactly as it would in reality.

- **Aircraft type noted, not penalised** — shown only when the aircraft you flew didn't match the route's expected family. This explains what was flown versus what was expected, and states plainly that **it does not affect payment** — a type mismatch in FSOps is purely informational, never a financial penalty, by deliberate design.
- **Financial outcome** — every ledger line the flight posted (ticket revenue, fuel uplift, landing/handling/parking/passenger fees, maintenance accrual, crew cost), each signed and itemised exactly as it hit your airline's ledger, followed by the sector's net. If the flight was flagged for slew or a position jump during tracking (see [Flight integrity](#flight-integrity) above), no ticket revenue line appears at all — only whatever was already spent, such as fuel bought before departure.

## The economy simulation

Route fares aren't just a distance-and-strategy suggestion — they decide what happens next. When you set a fare, FSOps works out how many passengers actually book at that price against the route's real market, and how full the aircraft ends up. There's no fare validation beyond requiring a positive number (and, at route creation, a sane multiple of the suggested fare) — the simulation is the guardrail, not a hard cap.

### How demand is worked out

For a given route and day, FSOps first decides how many people would ever want to fly it (independent of price), then decides how many of them actually book at your fare.

**The passenger pool** comes from both airports' size (a Large airport like Heathrow or Edinburgh pulls in far more of a catchment score than a small regional field — Large scores 10, Medium 3, Small 0.6), the route's distance, the month, the day of the week, and your airline's reputation:

- **Distance** matters a lot. Below 50 nm there's essentially no scheduled-passenger market at all (driving wins) — demand there collapses toward nil rather than granting a meaningful market at, say, half that threshold. From there it ramps up through a short-hop band, holds at its fullest through a **300–2,500 nm "sweet spot"** (long enough to beat driving, short enough that a connection isn't more convenient), then decays gradually beyond it — long-haul demand shrinks but never fully evaporates.
- **Season and day of week** both apply real multipliers — August, for example, is a noticeably stronger month than February, and Saturdays are stronger than Tuesdays.
- **Reputation** would raise or lower demand around a baseline of 50, but right now every airline starts at exactly 50 and nothing in the app currently moves it, so this factor sits neutral for everyone until reputation gameplay lands.

**How many of that pool actually book** depends on your fare relative to the route's *reference fare* (the same distance × strategy-multiplier figure the plan panel suggests) and your strategy's price sensitivity (see [Strategy profiles](#strategy-profiles)). Load factor is hard-capped at **92%** of seats — no route, at any price, ever sells more than that. Price at the reference fare and you'll typically see a load factor right at your strategy's usual level; price below it and more of the pool books, up to the 92% ceiling; price above it, revenue actually keeps climbing for a while, because you're losing nothing — the market pool doesn't shrink yet, only the theoretical maximum load factor does, and that ceiling hasn't caught up with real demand. Push the fare far enough past that crossover, though, and the model stops being forgiving: once you're pricing more than about 1.5× the reference fare, the passenger pool itself starts shrinking, faster than the fare is rising, so revenue turns down. That's the actual mechanism behind "there's no fare cap, but gouging doesn't pay" — a sky-high fare on a route with only a handful of willing passengers won't quietly keep making you money forever, because even a captive market eventually walks away.

### What a completed flight costs and earns

Every completed flight posts itemised lines to your airline's append-only ledger:

- **Ticket revenue** — passengers actually booked × your fare. Never anything to do with how fast, early, or smoothly you flew.
- **Fuel** — charged once, at flight start, for the fuel your route's plan says this sector needs (trip + taxi + contingency fuel — not the alternate and final-reserve allowance, which normally stays in the tanks unburned), at that airport's price on the day. Fuel is charged on what's bought, never on what's actually burned, and never refunded if the flight is later abandoned.
- **Landing and handling/parking/passenger fees** — scaled by the arriving aircraft's weight (MTOW) and the arrival airport's size. Landing fees are identical for every airline (a regulatory charge); handling, parking and passenger fees also carry your strategy's cost multiplier (Low-cost runs 15% below the baseline, Premium 35% above it).
- **A flat turnaround/gate fee** — deliberately *not* scaled by aircraft size, so a trivial sector in a tiny aircraft can't dodge every other cost.
- **Maintenance accrual and crew cost** — both scale with block time, with crew paid for at least a one-hour minimum duty block regardless of how short the sector actually was.

A flight completed with estimates (after an interruption — see [Ending a flight](#ending-a-flight-and-what-happens-if-it-gets-interrupted)) posts ticket revenue and normal costs but never a landing-quality bonus, since nothing was actually measured on it, and there's no landing-quality bonus in the model in the first place — payment has never depended on how well you landed. A flight where slew or a position jump was detected posts no ticket revenue at all (see [Flight integrity](#flight-integrity)) — though fuel already bought before departure stays charged, exactly as it would in reality.

### What's honest to say about the current balance

Two things are worth knowing plainly rather than discovering by surprise:

- **There's no recurring billing yet.** Your lease deposit, starting capital, and any startup loan post once, at the moment you found your airline. Lease payments, salaries and insurance aren't posted on a schedule — nothing bills you again automatically as time passes. This means the game is currently easier than its balance is actually tuned for; recurring billing is planned but not yet built.
- **Fuel isn't tracked between flights.** Aircraft don't have a stored fuel quantity, so every flight — including the return leg of a route you only just flew outbound — is charged for the fuel its own sector needs from scratch. A round trip doesn't yet get a cheaper return leg from fuel already sitting in the tanks, and there's no fuel tankering (carrying extra fuel from a cheap airport to skip buying at an expensive one) yet either.

How to do it: no separate action — the economy runs automatically off the routes you fly and the fares you set. Virtual pilots, aircraft purchasing, maintenance scheduling, and recurring monthly billing will plug into the same ledger as they land (see below) — each will earn and spend money the same way a player-flown sector does, no separate "virtual economy."

## Hiring and assigning virtual pilots

**Coming in a later update.**

What this is: as your airline grows, you won't want to fly every route yourself. Virtual pilots are hires who fly your scheduled routes automatically, on the real-world clock — including while FSOps itself is closed.

How to do it: from a pilots screen, you'll hire a pilot and assign them to one or more routes with a schedule. From that point, their flights complete in the background over real time and feed into your airline's economy and statistics the same way a flight you flew yourself would, just without the landing-quality detail that only comes from a flight tracked live through the sim.

## Buying vs leasing aircraft

**Coming in a later update**, beyond your starter aircraft — which is already leased today, as part of founding your airline (see [Starter aircraft](#starter-aircraft)). What's missing is growing your fleet beyond that one aircraft.

What this is: aircraft are the other half of running routes — you'll need them assigned to a route before it can be flown, by you or by a virtual pilot. FSOps is expected to support two ways to acquire one:

- **Buying** — a larger upfront cost, but the aircraft is yours outright with no ongoing payment.
- **Leasing** — lower upfront cost, spread as an ongoing expense against your airline's cash flow, but without ownership.

How to do it: from a fleet screen, you'll browse available aircraft, compare buy vs lease terms, and add the one you choose to your fleet before assigning it to a route.

## Maintenance

**Coming in a later update.**

What this is: aircraft accumulate wear through use — and harder landings are expected to accelerate it. Unmaintained aircraft are expected to become less reliable and more expensive to operate over time.

How to do it: from a fleet or maintenance screen, you'll be able to see each aircraft's condition and schedule maintenance, which costs money and takes the aircraft out of service for a period, in exchange for restoring its condition.

## Statistics dashboards

**Coming in a later update.**

What this is: airline-wide dashboards summarising how your virtual airline is performing — routes flown, on-time and landing-quality trends, fleet utilisation, and financial performance over time.

How to do it: a statistics section will be reachable from the main navigation, with your airline's key numbers laid out at a glance and the ability to drill into a specific route, aircraft, or pilot.

## In-game panel

**Coming in a later update.**

What this is: a lightweight panel visible inside MSFS itself, showing key airline stats — cash balance, active routes, current flight status — without alt-tabbing out to the browser.

How to do it: no action needed once built; the panel will be available from MSFS's own add-on menu while FSOps is running and connected.

## A worked example, start to finish

This walks through founding an airline, building a route, and flying it, with real figures — not invented ones. The plan-panel numbers below (distance, block time, fuel, fare) are fixed by the aircraft and the two airports, so they'll always come out the same. The demand and revenue numbers came from actually running FSOps and creating this exact airline and route on **8 August 2026** — your own numbers for the same route will differ, because demand factors in the month and day of the week (see [The economy simulation](#the-economy-simulation)), and fuel price drifts by a small amount day to day. Treat the shape of the example as reliable and the exact pounds-and-pence as illustrative.

**Founding the airline.** Through the setup wizard: name it "Avon Air", ICAO code `AVN`, home base Bristol Airport (EGGD), strategy **Domestic**, starter aircraft a Boeing 737-800 (189 seats, service ceiling 41,000 ft), currency GBP. No startup loan. Founding the airline leases the 737-800 (a one-month deposit of **£30,000** comes straight out of your starting capital) and hires you as its first pilot — you land on the Dashboard with a fleet of one at EGGD and a cash balance of **£1,970,000** (£2,000,000 starting capital minus that deposit).

**Building a route.** On the Routes page, departure EGGD (Bristol), arrival EGPH (Edinburgh) — a short domestic hop of **275.2 nm**. The plan panel shows:

- **Block time** — 10 min taxi-out, 15 min climb, 17 min cruise, 15 min descent, 8 min taxi-in, for a **65-minute total**. Climb and descent are fixed-duration phases in FSOps' model; only cruise scales with distance, so a hop this short spends most of its time climbing and descending rather than at level cruise.
- **Cruise altitude** — **FL240** (24,000 ft), chosen from the route's distance band and a simplified semicircular rule (this route's initial bearing, close to due north, falls on the "even flight levels" side of the rule).
- **Block fuel** — **4,838 kg** total: about 2,037 kg of trip fuel, 200 kg taxi, 102 kg contingency (5% of trip fuel), 1,200 kg alternate allowance, and 1,300 kg final reserve (30 minutes' worth). Only the first three of those — **2,338.5 kg** — actually get charged for at flight start (see [The economy simulation](#the-economy-simulation)); the alternate and reserve fuel are carried but not billed, since they're not meant to be burned.
- **Suggested fare** — **£65.00**. At Domestic's baseline multiplier this route's distance-based fare (275.2 nm × £0.12/nm × 1.0) would actually work out under £33, but every suggested fare has a £65 floor, so a short hop like this one hits that floor rather than pricing near-nothing.

Below the plan panel, the live economics readout (at the £65 suggested fare) shows **174 expected passengers**, a **92.1% load factor**, and **£11,310 expected revenue per sector** — Bristol and Edinburgh are both Large airports, so this city pair has a strong passenger pool, and at exactly the reference fare the model fills the aircraft right up to its 92% hard ceiling.

Selecting **Create route** creates both EGGD→EGPH and EGPH→EGGD in one action, paired as flight numbers (an odd outbound and the next even return, in AVN's own number series — shown in the UI as `AVN101`-style callsigns), sharing the £65 fare. The map now shows Bristol as your hub (the larger, ringed marker) with a curved line out to Edinburgh.

**Flying it.** With MSFS running and loaded in at EGGD, open the Fly screen. EGGD→EGPH shows under **Ready now** — your only aircraft is sitting right there. The flight brief repeats the plan above, adds the **174 passengers** and **£11,310 expected revenue** figures from the live demand model, and readiness shows the simulator connected, the loaded aircraft matching the 737 family, and you parked at EGGD. Selecting **Start flight** begins tracking — and immediately posts the fuel-uplift charge for the 2,338.5 kg of trip/taxi/contingency fuel, at Bristol's fuel price that day (around £0.85/kg for a UK airport before that day's drift, so roughly **£1,988**).

As you fly, the phase timeline advances through Taxi out, Takeoff, Climb, Cruise, Descent, and Approach, with live altitude/speed/fuel readouts and a moving map. Suppose the flight actually runs a little quick and you grease the landing: block time comes in at **62 minutes** (3 minutes ahead of the 65-minute plan) and fuel used at **4,650 kg** (about 190 kg under plan).

**Reading the report card.** Landing quality shows a touchdown rate of around **-180 fpm** — inside the -200 fpm smooth-touchdown range — with a peak of **1.2 g**, **0 bounces**, and a centreline deviation of about **14 metres**. The phase timeline now shows OOOI clock times instead of a progress indicator. "Actual vs. planned" shows the 3-minutes-ahead and roughly-190-kg-under-plan deltas described above. No flight integrity card appears — nothing was flagged. Since the 737-800 you flew matched the route's expected family, there's no aircraft-type card either.

**Financial outcome** shows every line this sector actually posted: **ticket revenue +£11,310.00** (174 pax × £65.00 — booked passengers come from the demand model, not from how the flight actually went), **fuel** already charged at departure (not repeated here), **landing fee -£750.50** (£9.50/tonne × the 737-800's 79-tonne MTOW at Large-airport Edinburgh), **handling -£513.50**, **parking -£94.80**, **passenger charges -£2,088.00** (£12.00 × 174 pax), a flat **turnaround/gate fee -£450.00**, **maintenance accrual -£217.00** (£210/hour × the 62 minutes you actually flew, not the planned 65), and **crew cost -£351.33** (£340/hour, same actual duration). Net for the sector comes out to roughly **£4,850**, give or take about £120 either way depending on that day's exact fuel price — a genuinely profitable leg, which is the point: at a sensible fare, one round trip like this a day comfortably covers a single leased 737-800, with plenty left over (the whole first month's lease deposit, £30,000, back inside about six or seven such flights). Fly Edinburgh back to Bristol next to complete the round trip — expect very similar numbers, since the two Large airports and the distance are symmetric either direction.
