# User Guide

This guide covers how to use FSOps, feature by feature. FSOps is under active development: founding an airline, adjusting settings, building a route network, and flying a fully tracked flight all work today and are described below as they actually behave. Features that aren't built yet are clearly marked **Coming in a later update**.

## Table of contents

- [Current build](#current-build)
- [Creating your airline](#creating-your-airline)
- [Settings](#settings)
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
- [The economy simulation](#the-economy-simulation)
- [Hiring and assigning virtual pilots](#hiring-and-assigning-virtual-pilots)
- [Buying vs leasing aircraft](#buying-vs-leasing-aircraft)
- [Maintenance](#maintenance)
- [Statistics dashboards](#statistics-dashboards)
- [In-game panel](#in-game-panel)
- [A worked example, start to finish](#a-worked-example-start-to-finish)

## Current build

Start the backend, open `http://localhost:5977` in your browser, and — the very first time, once world data has finished importing — you'll land in the airline setup wizard. From there you can found an airline, adjust settings, build out a route network, and fly a route with full live tracking against MSFS, ending in a post-flight report card. The economy engine, virtual pilots, fleet purchasing, maintenance, and statistics are still being built; each is marked below.

## Creating your airline

FSOps opens straight into a full-screen setup wizard whenever no airline exists for your machine yet. See [Getting Started](getting-started.md#6-found-your-airline) for the step-by-step flow. This section covers what each choice actually means.

### Strategy profiles

Your strategy is fixed at creation and currently drives the fare suggested for every route you plan (see [Building routes](#building-routes)):

- **International** — a long-haul network carrier. Suggested fares carry a **1.15×** multiplier over the baseline, and the route planner warns if you add a short domestic hop under about 200 nm — it doesn't match the strategy, though the warning is advisory and won't stop you.
- **Domestic** — a short-haul regional workhorse, priced at the baseline (**1.0×** multiplier). The route planner warns if you add an international route — again, advisory only.
- **Low-cost** — a high-frequency budget carrier. Suggested fares are discounted to **0.75×** the baseline — the lowest of the four profiles.
- **Premium** — a business-focused yield carrier. Suggested fares carry the highest multiplier, **1.6×** the baseline, reflecting a smaller, higher-paying passenger base.

Strategy also determines which route-length warnings you'll see in the plan panel, described in [Building routes](#building-routes). The deeper effects on passenger demand and route opportunities arrive with the economy simulation — see the note under [Planning and flying a tracked flight](#planning-and-flying-a-tracked-flight) below.

### Starter aircraft

You choose between an Airbus A320 (~180 seats, ~3,300 nm range) and a Boeing 737-800 (~189 seats, ~2,935 nm range) as your airline's first aircraft. It's purchased outright as part of founding your airline — its price is deducted from your starting capital (or top-up loan) in the financial ledger — and it determines which routes you can create until you add more aircraft to your fleet: a route beyond this aircraft's practical range can't be created (see [Building routes](#building-routes)).

## Settings

Reachable from the main navigation once your airline exists. Settings apply to your account, not to a specific airline.

### Currency

FSOps stores every amount — cash balance, fares, purchase prices, loan payments — in a single base currency unit (GBP-pegged) inside the database. The currency you pick here is a **display conversion only**: every screen multiplies the stored amount by that currency's fixed display rate and formats it with the right symbol and decimal places. Changing your currency in settings changes how numbers look everywhere in the app; it never changes your actual balance or rewrites any stored figures. FSOps isn't a forex simulator — display rates are fixed, not fetched or refreshed.

### Units

- **Distance** — nautical miles or kilometres, used for route distances.
- **Altitude** — feet or metres, used for cruise altitude.
- **Weight** — kilograms or pounds, used for fuel weights.
- **Time display** — UTC or local time, plus a 24-hour/12-hour clock toggle.

### Theme and accent colour

Switch between light and dark theme, and change your accent colour (the same palette offered in the setup wizard) at any time — it's used throughout the UI for highlights and selected states.

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
- **Suggested fare** — a distance-based fare adjusted by your airline's strategy multiplier (see [Strategy profiles](#strategy-profiles) above). This becomes the fare override field's starting value.

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

One thing worth knowing about the current build: an aircraft's recorded location is set when it joins your fleet, but doesn't yet update automatically once a flight lands — moving your fleet's recorded position to wherever a flight actually ends is still being wired up. In practice this means that, for now, only the route(s) departing from your aircraft's fleet location will show as flyable; flying the outbound leg of a round trip doesn't yet make the return leg show as flyable from the far end the way the pairing is ultimately designed to support. See [Troubleshooting](troubleshooting.md#a-route-doesnt-show-as-flyable) if a route you expect to be flyable isn't.

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
- **Other routes** / **Not flyable right now** — routes with no aircraft currently at the departure airport, each showing the specific reason (for example, that your fleet's aircraft is at a different airport, currently in flight, or in maintenance). See [Round trips and where your aircraft actually is](#round-trips-and-where-your-aircraft-actually-is) for why this happens and its current limitation.

Each row shows the airport pair, flight number, distance, estimated block time, and which aircraft (by registration or type) is available. There's currently no "free flight" option for flying something outside your route network — you can only fly routes you've built on the Routes page.

### The flight brief

Selecting a flyable route opens the **"Flight brief"** card. If more than one aircraft is available for the route, you can choose which one to fly as a set of chips; otherwise FSOps uses your fleet's first available aircraft automatically. Below that sit six figures at a glance: **distance**, **cruise altitude**, **block time**, **block fuel**, **passengers**, and **expected revenue** (the last is an estimate only — actual pricing arrives with the economy engine, so don't expect it to be paid out yet).

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
- **Aircraft type noted, not penalised** — shown only when the aircraft you flew didn't match the route's expected family. This explains what was flown versus what was expected, and states plainly that **it does not affect payment** — a type mismatch in FSOps is purely informational, never a financial penalty, by deliberate design.
- A footer note that revenue and costs for the flight aren't shown yet, since the economy engine that would calculate them hasn't landed — see [The economy simulation](#the-economy-simulation) below.

## The economy simulation

**Coming in a later update.**

What this is: today, route fares are a simple distance-and-strategy suggestion (see [Building routes](#building-routes)) and nothing consumes them. The full economy simulation will add passenger demand modelling, ticket pricing that responds to it, fuel and airport fees, and the loan/lease payments already tracked in your financial ledger — so flying (or scheduling) a route actually earns and spends money against your airline's finances over time.

How to do it: no separate action — once built, the economy runs automatically off the routes you fly and the fleet/pilots you assign to them.

## Hiring and assigning virtual pilots

**Coming in a later update.**

What this is: as your airline grows, you won't want to fly every route yourself. Virtual pilots are hires who fly your scheduled routes automatically, on the real-world clock — including while FSOps itself is closed.

How to do it: from a pilots screen, you'll hire a pilot and assign them to one or more routes with a schedule. From that point, their flights complete in the background over real time and feed into your airline's economy and statistics the same way a flight you flew yourself would, just without the landing-quality detail that only comes from a flight tracked live through the sim.

## Buying vs leasing aircraft

**Coming in a later update.**

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

This walks through founding an airline, building a route, and flying it, with figures computed the same way FSOps itself computes them — a concrete sense of the numbers you'll actually see, not just where to click.

**Founding the airline.** Through the setup wizard: name it "Avon Air", ICAO code `AVN`, home base Bristol Airport (EGGD), strategy **Domestic**, starter aircraft a Boeing 737-800 (189 seats, service ceiling 41,000 ft), currency GBP. No startup loan. Founding the airline buys the 737-800 outright and hires you as its first pilot, and you land on the Dashboard with a fleet of one at EGGD.

**Building a route.** On the Routes page, departure EGGD (Bristol), arrival EGPH (Edinburgh) — a short domestic hop of roughly **270 nm**. The plan panel shows:

- **Block time** — 10 min taxi-out, 15 min climb, 17 min cruise, 15 min descent, 8 min taxi-in, for a **65-minute total**. Climb and descent are fixed-duration phases in FSOps' model; only cruise scales with distance, so a hop this short spends most of its time climbing and descending rather than at level cruise.
- **Cruise altitude** — **FL240** (24,000 ft), chosen from the route's distance band and a simplified semicircular rule (this route's initial bearing, close to due north, falls on the "even flight levels" side of the rule).
- **Block fuel** — roughly **4,840 kg**: about 2,040 kg of trip fuel (47 minutes airborne at the 737-800's burn rate), 200 kg taxi, 102 kg contingency (5% of trip fuel), 1,200 kg alternate allowance, and 1,300 kg final reserve (30 minutes' worth).
- **Suggested fare** — **£35.00**. At Domestic's baseline multiplier this route's distance-based fare would actually work out under £33, but every suggested fare has a £35 floor, so short hops like this one hit that floor rather than pricing near-nothing.

Selecting **Create route** creates both EGGD→EGPH and EGPH→EGGD in one action, paired as flight numbers (an odd outbound and the next even return, in AVN's own number series), sharing the £35 fare. The map now shows Bristol as your hub (the larger, ringed marker) with a curved line out to Edinburgh.

**Flying it.** With MSFS running and loaded in at EGGD, open the Fly screen. EGGD→EGPH shows under **Ready now** — your only aircraft is sitting right there. The flight brief repeats the plan above, adds **189 passengers** and **expected revenue of £6,615** (189 seats × £35 — an estimate only, since nothing is actually charged yet), and readiness shows the simulator connected, the loaded aircraft matching the 737 family, and you parked at EGGD. Selecting **Start flight** begins tracking.

As you fly, the phase timeline advances through Taxi out, Takeoff, Climb, Cruise, Descent, and Approach, with live altitude/speed/fuel readouts and a moving map. Suppose the flight actually runs a little quick and you grease the landing: block time comes in at **62 minutes** (3 minutes ahead of the 65-minute plan) and fuel used at **4,650 kg** (about 190 kg under plan).

**Reading the report card.** Landing quality shows a touchdown rate of around **-180 fpm** — inside the -200 fpm smooth-touchdown range — with a peak of **1.2 g**, **0 bounces**, and a centreline deviation of about **14 metres**. The phase timeline now shows OOOI clock times instead of a progress indicator. "Actual vs. planned" shows the 3-minutes-ahead and roughly-190-kg-under-plan deltas described above. Since the 737-800 you flew matched the route's expected family, there's no aircraft-type card. A footer note reminds you that revenue and costs aren't shown yet — Avon Air's books haven't moved, because the economy engine that would post that £6,615 (or whatever it actually turns out to be) to your ledger hasn't landed. Fly Edinburgh back to Bristol next to complete the round trip.
