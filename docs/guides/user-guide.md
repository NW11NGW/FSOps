# User Guide

This guide covers how to use FSOps, feature by feature. FSOps is under active development: founding an airline, adjusting settings, and planning routes all work today and are described below as they actually behave. Features that aren't built yet are clearly marked **Coming in a later update**.

## Table of contents

- [Current build](#current-build)
- [Creating your airline](#creating-your-airline)
- [Settings](#settings)
- [Building routes](#building-routes)
- [Where your data lives](#where-your-data-lives)
- [Planning and flying a tracked flight](#planning-and-flying-a-tracked-flight)
- [Reading the post-flight report card](#reading-the-post-flight-report-card)
- [The economy simulation](#the-economy-simulation)
- [Hiring and assigning virtual pilots](#hiring-and-assigning-virtual-pilots)
- [Buying vs leasing aircraft](#buying-vs-leasing-aircraft)
- [Maintenance](#maintenance)
- [Statistics dashboards](#statistics-dashboards)
- [In-game panel](#in-game-panel)

## Current build

Start the backend, open `http://localhost:5977` in your browser, and — the very first time, once world data has finished importing — you'll land in the airline setup wizard. From there you can found an airline, adjust settings, and plan routes on a live map. Flying, the economy engine, virtual pilots, fleet purchasing, maintenance, and statistics are still being built; each is marked below.

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

## Where your data lives

FSOps stores everything — your airline, fleet, routes, pilots, flights, and financial ledger — in a SQLite database under `%LOCALAPPDATA%\FSOps\` on your machine, alongside FSOps' log files. See [Architecture](../architecture.md#app-paths-no-hardcoded-filesystem-paths) for why it lives there rather than next to the application files.

This means:

- Your data survives FSOps updates — reinstalling or upgrading the app doesn't touch it.
- Nothing is stored anywhere else; there's no account or server involved.
- **Backing up** your airline is as simple as copying the `%LOCALAPPDATA%\FSOps\` folder somewhere safe. Restoring is copying it back.
- Deleting that folder (or using the settings [danger zone](#settings)) resets FSOps to a blank slate — see [Troubleshooting](troubleshooting.md) if that happens unexpectedly.

## Planning and flying a tracked flight

**Coming in a later update.**

What this is: the core loop of FSOps. You pick a route, plan the flight, then fly it in MSFS while FSOps tracks it live via SimConnect.

How to do it: select a route and aircraft, then start the flight in FSOps before or after loading into the sim. Once MSFS reports you're airborne on a matching flight, FSOps begins tracking automatically: an interactive map follows your aircraft's position, and FSOps detects flight phases (taxi, take-off, climb, cruise, descent, approach, landing, taxi-in) as you fly. No manual phase reporting is needed — it's read directly from simulator state.

## Reading the post-flight report card

**Coming in a later update.**

What this is: once a tracked flight ends, FSOps produces a report card summarising how the flight went, both operationally and financially.

The report card will cover:

- **Landing quality** — scored on:
  - **Touchdown rate** (vertical speed at touchdown). As a rule of thumb, roughly **-100 to -200 feet per minute** is a smooth, well-judged touchdown. Beyond **-400 fpm**, the landing is considered hard and may flag for inspection or maintenance implications.
  - **G-force** at touchdown, as a secondary smoothness signal alongside touchdown rate.
  - **Centerline accuracy** — how closely you tracked the runway centreline through the flare and rollout.
- **Block time comparison** — how your actual gate-to-gate time compared with the planned block time for the route.

How to do it: no action needed to generate one — a report card is produced automatically at the end of every tracked flight and will be viewable from your flight history.

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
