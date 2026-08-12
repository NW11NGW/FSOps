# User Guide

This guide covers how to use FSOps, feature by feature. FSOps is under active development: founding an airline, adjusting settings, building a route network, flying a fully tracked flight, running a fleet, the monthly billing cycle that keeps it all honest, hiring virtual pilots to keep your airline flying while you're away, a Statistics page, importing your SimBrief flight plan, seeing online VATSIM controllers on your live map, and an in-game panel for MSFS, all work today and are described below as they actually behave. Features that aren't built yet are clearly marked **Coming in a later update**.

## Table of contents

- [Current build](#current-build)
- [Creating your airline](#creating-your-airline)
  - [Playstyles](#playstyles)
  - [Strategy profiles](#strategy-profiles)
  - [Starter aircraft](#starter-aircraft)
- [Settings](#settings)
  - [Airline](#airline)
  - [MSFS in-game panel](#msfs-in-game-panel)
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
  - [Importing your OFP back](#importing-your-ofp-back)
- [Reading the post-flight report card](#reading-the-post-flight-report-card)
  - [Flight integrity](#flight-integrity)
- [The economy simulation](#the-economy-simulation)
  - [Your airline's reputation](#your-airlines-reputation)
- [The monthly billing cycle](#the-monthly-billing-cycle)
- [Fuel and tankering](#fuel-and-tankering)
- [Buying, leasing and financing aircraft](#buying-leasing-and-financing-aircraft)
- [Reserving an aircraft for yourself](#reserving-an-aircraft-for-yourself)
- [Selling an aircraft, ending a lease, and settling a loan early](#selling-an-aircraft-ending-a-lease-and-settling-a-loan-early)
- [The aircraft catalogue and registrations](#the-aircraft-catalogue-and-registrations)
- [Maintenance](#maintenance)
- [Hiring and assigning virtual pilots](#hiring-and-assigning-virtual-pilots)
  - [Building a weekly schedule](#building-a-weekly-schedule)
- [The wall-clock economy: flying while you're away](#the-wall-clock-economy-flying-while-youre-away)
- [The "while you were away" summary](#the-while-you-were-away-summary)
- [The Finances page](#the-finances-page)
- [The live operations map](#the-live-operations-map)
  - [Online VATSIM controllers](#online-vatsim-controllers)
- [Statistics dashboards](#statistics-dashboards)
- [In-game panel](#in-game-panel)
- [A worked example, start to finish](#a-worked-example-start-to-finish)

## Current build

Start the backend, open `http://localhost:5977` in your browser, and — the very first time, once world data has finished importing — you'll land in the airline setup wizard. From there you can found an airline, adjust settings, build out a route network, fly a route with full live tracking against MSFS ending in a post-flight report card, run a fleet — buying, leasing, selling, maintaining and financing aircraft — all billed on a real monthly cycle whether or not you're actively flying, and hire virtual pilots to fly a standing weekly schedule on the real-world clock, even while FSOps is closed, with a Finances page and a Statistics page to see exactly where the money went. You can also import your latest SimBrief flight plan, see online VATSIM controllers on your live map, and use a compact panel inside MSFS itself — see [Statistics dashboards](#statistics-dashboards), [Plan in SimBrief](#plan-in-simbrief) and [In-game panel](#in-game-panel) below for each.

## Creating your airline

FSOps opens straight into a full-screen setup wizard whenever no airline exists for your machine yet. See [Getting Started](getting-started.md#6-found-your-airline) for the step-by-step flow. This section covers what each choice actually means.

### Playstyles

Alongside strategy, the wizard asks you to choose a **playstyle** — Casual or True-life. Unlike strategy, this is **permanent for the life of your airline**: there's no setting to change it afterwards, only deleting the airline and starting over (going Casual → True-life would multiply your fixed costs roughly twelvefold overnight; the reverse would trivialise everything you'd already earned). Playstyle sets your starting capital, every aircraft type's lease rate, your lease deposit term, monthly insurance, maintenance downtime, and the ceiling on loan interest rates — it shapes how your airline is *run*, not just how fast it earns.

- **Casual** — forgiving fixed costs, so flying occasionally still runs a growing airline. Starting capital **£2,000,000**, a one-month lease deposit, and a deliberately game-balanced starter lease of **£30,000/month** for either starter type (A320 or 737-800) — see [Starter aircraft](#starter-aircraft) below for why that figure is low. Monthly insurance is **£6,000** per aircraft. A-check maintenance grounds an aircraft for **4 hours**, a C-check for **24 hours** — a nuisance and a bill, not a chunk of your evening lost. Loan interest is capped at **5% APR**. The honest choice for playing in short, occasional sessions.
- **True-life** — real-world figures throughout. Starting capital **£2,500,000**, a two-month lease deposit, and realistic starter lease rates (roughly **£380,000/month** for an A320, **£390,000/month** for a 737-800). Monthly insurance is **£50,000** per aircraft. A-check downtime is **24 hours**, C-check is **336 hours** (about a fortnight). Loan interest is capped at **8% APR**. At these rates, a single aircraft flown only occasionally runs at a genuine loss — True-life's progression is built to depend on hiring virtual pilots to fly standing schedules once that feature lands, not on flying it yourself alone. The honest choice if you want to run something closer to an actual carrier.

Every other figure in the economy — fares, demand, fuel prices, landing/handling fees, the maintenance cycle itself, and the used-aircraft discount — is identical between the two; only the numbers above differ. The onboarding cards and Settings → Airline both fetch these figures live from FSOps' own configuration, so they can't drift out of sync with what founding an airline (or leasing an aircraft later) actually charges.

### Strategy profiles

You choose a strategy when you found your airline, but it's never permanent — pick a different one at any time from **Settings → Airline**. Your strategy drives three things at once: the fare suggested for every route you plan (see [Building routes](#building-routes)), how sharply demand responds to your pricing (see [The economy simulation](#the-economy-simulation)), and which route-length advisories you'll see in the plan panel:

- **International** — a long-haul network carrier. Suggested fares carry a **1.15×** multiplier over the baseline. Demand is relatively insensitive to price (of the five profiles, only Premium is less price-sensitive), and the route planner warns if you add a short domestic hop — it doesn't match the strategy, though the warning is advisory and won't stop you.
- **Domestic** — a short-haul regional workhorse, priced at the baseline (**1.0×** multiplier). The route planner warns if you add an international route — again, advisory only.
- **Low-cost** — a high-frequency budget carrier. Suggested fares are discounted to **0.75×** the baseline — the lowest of the five profiles — but demand is the most price-sensitive of any profile, so a low-cost route depends on volume, not fare, to make money. Costs run lower too (service-level charges like handling and passenger fees are discounted 15%).
- **Premium** — a business-focused yield carrier. Suggested fares carry the highest multiplier, **1.6×** the baseline, reflecting a smaller, higher-paying passenger base with the lowest typical load factor of the five. Service-level costs run higher too (a 35% surcharge), reflecting a higher service standard.
- **Balanced** — a neutral all-rounder, priced at the baseline like Domestic. It's the only profile that raises no route-suitability advisories at all — an international route or a short domestic hop is fine either way. Its price sensitivity sits strictly between Domestic and Premium, and its typical load factor sits at the midpoint of the other four profiles' range, by design: not a hidden best choice, just the option with no directional bias.

Fuel and landing fees never change by strategy — they're physical, regulatory charges, identical for every airline flying the same aircraft into the same airport. Only service-level charges (handling, parking, passenger fees) and reference fares carry a strategy multiplier. The figures shown on each strategy's card during onboarding or in Settings are read live from FSOps' own configuration, not a separate hand-written description, so they can't drift out of sync with what the economy engine actually does.

### Starter aircraft

You choose between an Airbus A320 (180 seats, 3,300 nm range) and a Boeing 737-800 (189 seats, 3,115 nm range) as your airline's first aircraft. It's **leased**, not bought — founding your airline posts a lease deposit from your starting capital (or top-up loan) rather than the aircraft's full multi-million-pound price, which is what makes starting an airline affordable. The deposit and the ongoing monthly rate both depend on your [playstyle](#playstyles): under Casual it's a one-month deposit on a deliberately game-balanced **£30,000/month** rate for either starter type — a real A320 or 737-800 lease runs closer to £380,000–£390,000 a month, which is exactly what True-life charges instead, on a two-month deposit. Founding your airline is only the first lease payment; from then on, the same monthly rate posts automatically every 30 days as part of the [monthly billing cycle](#the-monthly-billing-cycle) — nothing about the starter aircraft is a one-off charge. Its range determines which routes you can create until you add more aircraft to your fleet: a route beyond this aircraft's practical range can't be created (see [Building routes](#building-routes)).

## Settings

Reachable from the main navigation once your airline exists. Most of Settings applies to your account (currency, units, theme, the MSFS in-game panel) rather than to a specific airline — the exception is the **Airline** section below, which edits your airline's own identity and strategy.

### Airline

Your airline's name, accent colour, strategy, and your own name as its founding pilot all live here and can be changed at any time (home base and playstyle are shown for reference but are fixed once you've founded your airline — see [Playstyles](#playstyles) for why):

- **Name** — 2 to 40 characters, the same limits as at founding.
- **Accent colour** — a preset swatch or a custom hex value, used throughout the UI for highlights and selected states.
- **Your name** — 1 to 40 characters. This is you, the player: it's shown on every flight you fly and alongside your entry on the [Pilots page](#hiring-and-assigning-virtual-pilots), the same as it was when you set it (or left it blank) at founding.
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

### SimBrief

Your SimBrief Pilot ID — find it on SimBrief under Account → Pilot ID. Set it and the Fly screen's flight brief pulls your latest OFP's fuel, cruise altitude, block time and filed route instead of FSOps' own estimate, but only when that plan matches the exact route you're about to fly — see [Plan in SimBrief](#plan-in-simbrief) below. Leave it blank (the default) and FSOps always uses its own built-in plan; nothing about your flight is sent anywhere without it, and it's entirely optional.

### MSFS in-game panel

One section covering both your MSFS 2024 Community folder and the [in-game panel](#in-game-panel) FSOps installs into it. The two are deliberately together: the folder only matters because of what's installed in it, and keeping them apart is what used to let the two quietly disagree.

Every time you open Settings, FSOps reads what is **actually on disk** rather than remembering what it once did, and tells you:

- **Panel files** — whether the package is really there in the folder you've configured.
- **Location** — the exact folder it's installed in, which is always a folder called `fsops-panel` directly inside your Community folder, and never anything else.
- **Version** — the installed version, and whether it matches the one this copy of FSOps expects. If you update FSOps and the panel is older, it says so and asks you to reinstall.
- **Connects to** — the port the installed panel calls FSOps on. This is written into the package when it's installed, so if FSOps later moves port, the panel goes on calling the old one and simply shows nothing in the sim. FSOps spots that mismatch and tells you to reinstall, because it's the kind of failure that otherwise looks like nothing is wrong at all.
- **Toolbar button** — whether the compiled component MSFS needs to draw the FSOps button is present.

Three actions sit underneath:

- **Install panel** — appears when a folder is set but nothing is installed there. This is how you add the panel if you skipped it during the setup wizard.
- **Reinstall / repair** — installs cleanly over whatever is there. Use it to update an older version, fix a package with files missing or edited, or repoint the panel after FSOps changes port. Safe to run as often as you like.
- **Remove panel** — deletes the `fsops-panel` folder and nothing else. Your other add-ons are untouched, and so is everything about your airline. It asks you to confirm first, and you can install it again from the same place at any time.

**Changing the folder.** If you edit the path and press **Save folder** while the panel is installed in the old one, FSOps asks what to do with the copy already sitting there rather than deciding for you — because both answers are reasonable, depending on whether you're pointing FSOps at a second MSFS install or correcting a mistake:

- **Move the panel** installs it into the new folder and then removes the old copy. The new one is always written first, so if anything goes wrong you're left with the panel you already had, never with none at all.
- **Just change the folder** saves the new path and leaves the old copy exactly where it is.

Clearing the path entirely asks the same question, offering to remove the panel first rather than abandoning it in your Community folder.

**Two refusals worth knowing about**, both deliberate:

- FSOps will not install into, or delete, a folder called `fsops-panel` that it can't tell it created itself. If something else is using that name, it says so and leaves it alone — deleting a folder you nominated by mistake isn't something you could undo.
- FSOps will not create a Community folder that isn't there. If the configured folder has been deleted or the sim has moved, it says the folder no longer exists and asks you to pick the new one, rather than helpfully building a folder MSFS will never read and reporting a successful install of a panel that can never appear.

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

- **Blocking (red)** — same departure and arrival airport, or the route is beyond the range of **every aircraft in your fleet**. Either of these disables route creation until you change your airport picks or add a longer-legged aircraft.
- **Advisory (amber)** — nothing you have reserved can fly this far but something else in your fleet can (see [Range](#range) below), a runway at either airport may be too short for the aircraft, or the route doesn't match your airline's strategy (an international route on a Domestic strategy, or a short domestic hop on an International strategy). These are shown for your attention but **do not block creating the route** — you can create it anyway if you're confident it'll work.

### Range

Range is always asked about your **whole airline**, never about one aircraft type. An aircraft's practical range is about **0.85×** its published range, once fuel reserves and payload are accounted for — an A320 published at 3,300 nm plans to about 2,805 nm.

The route planner gives one of three answers:

- **An aircraft reserved to you can fly it.** Nothing is said at all — plan the route and go.
- **Nothing reserved to you can fly it, but something in your fleet can.** An amber advisory names the aircraft, for example *"Nothing reserved to you can fly it, but G-VSIR (Boeing 737-700) has the range — reserve it on the Fleet page to fly it yourself, or roster it to a virtual pilot as it is."* The route is still created; this is a note about who can fly it, not a refusal.
- **Nothing in your fleet can fly it.** A red blocking message names your longest-legged aircraft and its practical range, and points you at the Fleet page. This is the only case where range stops a route being created.

Elsewhere, range is a hard limit on a specific airframe rather than guidance:

- On the **Fly** screen, an aircraft parked at the departure airport that can't reach the destination is still listed — never silently dropped — but shown as unflyable with the reason (*"G-DMRO (Airbus A320) can't reach KJFK — 2912 nm is beyond its practical range of about 2805 nm."*). It's reported ahead of "not reserved to you", because reserving it wouldn't help.
- In the **schedule builder**, a route beyond the duty day's aircraft is offered as an unavailable leg with the same kind of reason, and saving a schedule containing one is refused. An A320 is never rostered onto a sector it cannot reach.

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
- **FSOps copies your database before applying any change to its structure.** Updates occasionally need to alter the shape of the database, and a copy taken beforehand is what you would restore from if one ever went wrong. It's only taken when there's actually something to apply, and if the copy can't be made, FSOps stops rather than proceeding without it.
- **If the database is ever damaged, FSOps will not try to fix it by itself.** It stops and tells you which file is affected, and asks you to copy it somewhere safe first — because a damaged file is sometimes still repairable, and a well-meant automatic "repair" is how an airline gets lost for good.

### World data

**Settings → World data** shows how many airports and runways FSOps knows about and when that data last changed. A newer data set arrives with an FSOps update and is applied automatically the first time you start the app afterwards; the **Refresh** button does it sooner if you want.

A refresh only ever **adds and updates — it never deletes**. Airports do sometimes disappear from the upstream source, occasionally because they really closed but just as often for editorial reasons FSOps can't tell apart. Either way, anything you've built on stays: your routes, your flight history and any aircraft parked there keep pointing at somewhere real, even if the wider world data no longer lists it.

## Planning and flying a tracked flight

This is the core loop of FSOps: pick a route on the **Fly** screen (main navigation), review the brief, start the flight, then fly it in MSFS while FSOps tracks it live over SimConnect. No manual phase reporting is ever needed — every phase, timestamp, and landing measurement is read directly from simulator state.

### Connecting to the simulator

FSOps needs a live SimConnect connection to track anything, so make sure MSFS 2024 is running and you're loaded into a flight (on the ground or in the air — the main menu doesn't expose live data) before you expect tracking to start. See [Getting Started](getting-started.md#7-connect-to-msfs) for the full connection walkthrough. Two small pills in the top-right of the top bar, next to your cash balance, show connection state at all times: one for FSOps' own live-update link to its backend, one for the simulator itself ("Sim connected" in green, "Sim offline" otherwise). If the sim drops out mid-flight, FSOps keeps retrying the connection on its own — see [Ending a flight, and what happens if it gets interrupted](#ending-a-flight-and-what-happens-if-it-gets-interrupted) below for what happens to a flight that was in progress when that happens.

### Picking a route to fly

The Fly screen opens on a **"Choose a route"** card listing every route your airline has built (see [Building routes](#building-routes)). If you have more than a handful of routes, a search box lets you filter by airport code, airport name, or flight number. Routes are grouped by whether you can actually fly them right now:

- **Ready now** — routes with a fleet aircraft physically sitting at the departure airport, marked with a green badge.
- **Other routes** / **Not flyable right now** — routes with no aircraft currently at the departure airport, each showing the specific reason (for example, that your fleet's aircraft is at a different airport, currently in flight, in maintenance, or not reserved to you — see [Reserving an aircraft for yourself](#reserving-an-aircraft-for-yourself) below). See [Round trips and where your aircraft actually is](#round-trips-and-where-your-aircraft-actually-is) for how an aircraft's recorded location changes as you fly.

Each row shows the airport pair, flight number, distance, estimated block time, and which aircraft (by registration or type) is available. There's currently no "free flight" option for flying something outside your route network — you can only fly routes you've built on the Routes page.

### The flight brief

Selecting a flyable route opens the **"Flight brief"** card. Every fleet aircraft physically parked at the departure airport shows as its own chip — not just the ones you can actually take. A chip you can fly is clickable; one you can't is shown disabled, greyed out, with the reason on hover and repeated underneath the chip row in full (for example, *"G-VIRF is not reserved to you - reserve it from the Fleet page to fly it."*, with a link straight there). FSOps picks the first flyable aircraft for you by default, but you can switch between any that are actually available. Below the chips sit six figures at a glance: **distance**, **cruise altitude**, **block time**, **block fuel**, **passengers**, and **expected revenue** — the same demand-model figures shown while planning the route (see [The economy simulation](#the-economy-simulation)), evaluated fresh for today. Passengers and revenue here are still an estimate: the actual ticket revenue is only booked and posted once the flight completes, and if you started the flight but demand ticks over to a new day before you land, the posted figures come from whatever the market looks like at completion, not at the moment you checked the brief.

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

### Importing your OFP back

This is the other direction: once you've generated an OFP in SimBrief (whether through the button above or SimBrief's own site), FSOps can pull it back in and use its figures for the sector you're about to fly. Set your **Pilot ID** in [Settings → SimBrief](#simbrief) and, on the flight brief, a banner tells you which plan is actually in use:

- **A matching plan found** — "Plan imported from SimBrief," followed by its cruise altitude, block time, block fuel and filed route. These figures replace FSOps' own estimate as **what the flight is planned against** — the report card's "actual vs. planned" comparison uses them instead of the built-in estimate, and the pre-flight brief shows them directly.
- **No usable plan** — "Using the built-in plan," with a plain reason: no Pilot ID set, SimBrief unreachable or timed out, no OFP on file, or (the one case FSOps is careful never to get wrong) your latest OFP is filed for a different city pair than the route you're about to fly. Any of these falls back to FSOps' own built-in planner exactly as if you'd never set a Pilot ID at all — a flight always has a workable plan either way.

Two things worth being precise about. **Routes stay the source of truth** — importing a plan never creates, changes, or overrides a route you've built; it only supplies figures for the sector you're already flying. And **this never changes what fuel is actually charged**: what you're billed for still comes from measured burn or the built-in estimate (see [The economy simulation](#the-economy-simulation)), exactly the same as if SimBrief weren't involved — importing an OFP changes what the plan *says*, never what the sector actually costs. The fetch itself happens on FSOps' own server, never your browser, so nothing about your flight reaches SimBrief without your Pilot ID already being set.

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
- **Reputation** raises or lowers demand around a baseline of 50 — see [Your airline's reputation](#your-airlines-reputation) below for what actually moves it and by how much.

**How many of that pool actually book** depends on your fare relative to the route's *reference fare* (the same distance × strategy-multiplier figure the plan panel suggests) and your strategy's price sensitivity (see [Strategy profiles](#strategy-profiles)). Load factor is hard-capped at **92%** of seats — no route, at any price, ever sells more than that. Price at the reference fare and you'll typically see a load factor right at your strategy's usual level; price below it and more of the pool books, up to the 92% ceiling; price above it, revenue actually keeps climbing for a while, because you're losing nothing — the market pool doesn't shrink yet, only the theoretical maximum load factor does, and that ceiling hasn't caught up with real demand. Push the fare far enough past that crossover, though, and the model stops being forgiving: once you're pricing more than about 1.5× the reference fare, the passenger pool itself starts shrinking, faster than the fare is rising, so revenue turns down. That's the actual mechanism behind "there's no fare cap, but gouging doesn't pay" — a sky-high fare on a route with only a handful of willing passengers won't quietly keep making you money forever, because even a captive market eventually walks away.

### What a completed flight costs and earns

Every completed flight posts itemised lines to your airline's append-only ledger:

- **Ticket revenue** — passengers actually booked × your fare. Never anything to do with how fast, early, or smoothly you flew.
- **Fuel** — charged at flight start, at that airport's price on the day, but only for what's actually uplifted. Fuel is a real quantity carried on the aircraft between flights now (see [Fuel and tankering](#fuel-and-tankering)), so if enough is already in the tank from the last flight, no fuel line posts at all; otherwise you're charged to top up to this sector's own requirement (trip + taxi + contingency — not the alternate and final-reserve allowance, which normally stays in the tanks unburned). Fuel is charged on what's bought, never on what's actually burned, and never refunded if the flight is later abandoned.
- **Landing and handling/parking/passenger fees** — scaled by the arriving aircraft's weight (MTOW) and the arrival airport's size. Landing fees are identical for every airline (a regulatory charge); handling, parking and passenger fees also carry your strategy's cost multiplier (Low-cost runs 15% below the baseline, Premium 35% above it).
- **A flat turnaround/gate fee** — deliberately *not* scaled by aircraft size, so a trivial sector in a tiny aircraft can't dodge every other cost.
- **Maintenance accrual and crew cost** — both scale with block time, with crew paid for at least a one-hour minimum duty block regardless of how short the sector actually was.

A flight completed with estimates (after an interruption — see [Ending a flight](#ending-a-flight-and-what-happens-if-it-gets-interrupted)) posts ticket revenue and normal costs but never a landing-quality bonus, since nothing was actually measured on it, and there's no landing-quality bonus in the model in the first place — payment has never depended on how well you landed. A flight where slew or a position jump was detected posts no ticket revenue at all (see [Flight integrity](#flight-integrity)) — though fuel already bought before departure stays charged, exactly as it would in reality.

How to do it: no separate action — the economy runs automatically off the routes you fly and the fares you set. Virtual pilots plug into this exact same ledger — see [Hiring and assigning virtual pilots](#hiring-and-assigning-virtual-pilots) — earning and spending money the same way a player-flown sector does, with no separate "virtual economy."

### Your airline's reputation

Your airline has a reputation score, shown on the **Dashboard**, that starts at **50** and moves on its own as you fly — it's a genuine number, not a fixed label, and it feeds straight back into demand: see [The passenger pool](#how-demand-is-worked-out) above. The Dashboard card shows the current score, which way it's trending (**Improving**, **Declining**, **Steady**, or **No history yet** for a brand-new airline), and a short plain-language line naming what's actually driving it — for example "94% on time over your last 15 sectors" or "2 cancelled, 1 skipped in that window" — rather than leaving you to guess what a number going up or down actually means. Nothing here is invented: a driver that couldn't honestly be measured for a sector (see below) is simply left out of the sentence, never shown as a false zero.

**What moves it, and by how much:**

- **On-time performance is the main driver.** Arriving within a few minutes of planned block time scores full marks; arriving very late scores close to nothing. This applies identically whether you flew the sector yourself or a virtual pilot did — a large airline flying mostly on autopilot pilots can't dodge the metric.
- **Landing quality moves it too, by a smaller amount.** Your own real touchdown telemetry and a virtual pilot's simulated landing are scored on exactly the same scale, so a virtual pilot's landings count the same way yours do.
- **A cancelled or skipped sector costs more than any delay can.** From a passenger's point of view the flight simply didn't happen, and that's treated as a materially bigger hit to standing than even the latest possible arrival.
- **A sector grounded by maintenance never touches your reputation at all.** If a scheduled leg is recorded as **Suspended** because its aircraft is mid-check (see [Maintenance](#maintenance)), that's the app's own scheduling limitation, not a mistake in how you're running the airline, so it's deliberately excluded rather than counted as a cancellation.
- **Completing a flight manually costs a small, fixed penalty** — see [Ending a flight](#ending-a-flight-and-what-happens-if-it-gets-interrupted). This has nothing to do with timing or landing: a manual completion has no reliable telemetry to judge, so nothing about how early or well it "went" is scored either way. The penalty exists because the sector genuinely couldn't be verified, and it's deliberately smaller than the worst a properly-tracked sector could cost you — flying a sector out for real is never the worse choice reputation-wise, but ending tracking early is never a free way to escape a flight that's going badly, either.
- **Abandoning a flight costs as much as a cancellation** — the sector never happened from a passenger's point of view, the same as a cancelled or skipped one. This is on top of losing the ticket revenue and any fuel already bought for that sector, which are separate financial losses, not a substitute for the reputation cost.

**The scale of it:** flying consistently well moves reputation from 50 to roughly 75 over about **40 to 60 sectors** — noticeable, but not something one bad day swings dramatically. At a reputation of 100 you'll sell roughly **25% more seats** than at 50 on the same route and fare; at 0 you'll sell roughly **25% fewer**, and it's floored so a very bad stretch never chokes a route down to nothing. A single rough sector is always recoverable; a genuinely bad run of them is what actually costs you passengers.

## The monthly billing cycle

Founding your airline, or adding an aircraft to your fleet later, only posts the up-front cost (a lease deposit, or a purchase price). From then on, three kinds of cost post automatically every **30 days of real-world wall-clock time** — a "month" here is a rolling 30-day window from whenever the last one was billed, not a calendar month:

- **Lease payments** — the monthly rate for every leased aircraft in your fleet, itemised per aircraft.
- **Pilot salaries** — your own salary as the founding pilot, and every virtual pilot's salary (£9,000/month each) once you've hired any — see [Hiring and assigning virtual pilots](#hiring-and-assigning-virtual-pilots). A pilot is billed whether or not they've flown anything that period.
- **Insurance** — a flat monthly figure per aircraft in your fleet, whether leased or owned (Casual £6,000, True-life £50,000 — see [Playstyles](#playstyles)).
- **Loan repayments** — every outstanding loan (a startup loan, or one taken later from the Fleet page) amortises here: part interest, part principal, same as a real loan, until it's paid off.

This runs whether or not FSOps is open. If you close FSOps for a few days and reopen it, the billing cycle catches up on everything that fell due while it was closed, all at once, rather than skipping it — so **don't be surprised by a larger-than-usual charge the next time you open FSOps after a break**. It can't be tricked by winding your system clock forward either: the cycle tracks a strictly forward-moving watermark and only ever bills for time that has genuinely passed. Without any virtual pilots flying a schedule, an airline you aren't actively flying still loses money every month from these charges alone — hiring pilots and giving them a standing schedule (see [Hiring and assigning virtual pilots](#hiring-and-assigning-virtual-pilots)) is what turns that around, since their flights earn revenue against your fixed costs on the same wall clock.

## Fuel and tankering

Every aircraft in your fleet carries a real fuel quantity between flights, shown on the [Fleet page](#buying-leasing-and-financing-aircraft) as **fuel on board**. Fuel is only ever charged when it's actually bought (uplifted):

- Landing with fuel still in the tank and flying again later — including the return leg of a route you just flew outbound — costs nothing further for whatever's already on board. You're only charged to top up the difference if the tank doesn't already hold enough for the next sector.
- If FSOps is watching live telemetry when you start a flight, it reconciles against whatever the sim actually reports in the tank — so refuelling in the sim before pressing **Start flight** (or a fuel change the app didn't see, like a sim restart) is picked up and charged correctly at that airport's price.

**Tankering** — deliberately uplifting more fuel than this sector needs, to skip buying at a pricier airport later — is advisory only. When the Fly screen's flight brief detects that your destination's fuel is meaningfully more expensive than your departure airport's, it shows what uplifting extra here to cover the return leg would save, net of the extra fuel you'd burn just from carrying the extra weight (about 3% of the excess mass, per hour it stays on board — a real "cost of carry" rule of thumb). It also warns if the extra fuel would push you over the aircraft's maximum take-off weight. One honest limit: landing fees are charged on the aircraft type's certificated MTOW, exactly as in reality, so tankering doesn't raise the landing fee you'll pay at the other end — burning off the extra weight in the air is the only real counterweight to tankering, same as a real airline.

## Buying, leasing and financing aircraft

The **Fleet** page (main navigation) shows every aircraft you own or lease — registration, type, current location, status, airframe hours, hours to the next A- and C-check, condition, ownership, fuel on board, and, for a grounded aircraft, exactly why and until when. From here you can grow your fleet beyond your starter aircraft:

- **Lease** — a deposit (one month under Casual, two under True-life — see [Playstyles](#playstyles)) plus the type's monthly rate, billed going forward through the [monthly cycle](#the-monthly-billing-cycle). A leased aircraft always arrives fresh: 100% condition, zero hours.
- **Buy new** — the aircraft's full purchase price, paid once, with no ongoing lease payment. It's yours outright, at 100% condition with zero hours — but airline aircraft genuinely cost tens of millions, so this is a milestone funded by retained profit or a loan, not an opening move.
- **Buy used** — 55% of the new price, but it starts already worn: 70% of the way to both its next A-check and its next C-check, and at 70% condition rather than 100%. The saving is real, but you're buying it back through an earlier trip to maintenance, not for free.

Both the lease and buy dialogs let you search the catalogue and pick a **registration** for the aircraft before confirming — see [The aircraft catalogue and registrations](#the-aircraft-catalogue-and-registrations) below.

**Loans** are available from the same page (**Take out a loan**). You choose an amount and a term; the interest rate is never something you pick — it's computed automatically from how much of your airline's trailing 30-day net operating cash flow (excluding one-off injections like starting capital or another loan's own proceeds) the new loan's payment would consume, scaling from the playstyle's base rate up to its hard cap (5% Casual, 8% True-life) the more of your capacity it uses. A **brand-new airline has no trading history yet, so its cash flow is exactly zero at creation — which means a startup loan is always priced at the cap.** That's not a bug: it's the correct price for an unproven borrower with no track record. As your airline's cash flow grows from flying, a later loan can price in below the cap. The loan dialog always shows the real rate, monthly repayment and total interest before you commit — never an estimate that could disagree with what taking the loan actually charges.

### Reserving an aircraft for yourself

Every aircraft in your fleet shows a **reservation** toggle on the Fleet page: **reserved for you to fly**, or **released to virtual pilots**. This is a hard rule now, not a preference — it's the single control that decides who is allowed to fly each aircraft, and the two sides can never both claim the same one:

- **You can only fly an aircraft that's reserved to you.** The Fly screen and the flight brief only ever offer your reserved airframes; anything else shows disabled with **"Not reserved to you - reserve it from the Fleet page to fly it."**
- **A reserved aircraft is never offered to a virtual pilot's schedule.** It doesn't appear as an option when building or editing a schedule, so a virtual pilot can never end up assigned to the aircraft you're about to fly — the conflict simply can't be created.
- **Reserve and release are the only way an aircraft moves between the two pools.** Toggling the switch on the Fleet page is the whole mechanism; nothing else changes it behind your back once your fleet exists.
- **Reserving an aircraft that already has scheduled legs is refused, by default.** FSOps tells you exactly which pilot and which legs stand in the way rather than silently dropping them; you can confirm again to clear those legs and reserve it anyway, and FSOps confirms afterward exactly what was cleared.
- **Releasing an aircraft always works**, even one you're about to fly or currently flying — a reserved aircraft was never claimed by the scheduler in the first place, so there's nothing on the other side to conflict with. Releasing simply means you can no longer fly it until you reserve it again.
- **With exactly one aircraft, this choice is explicit rather than automatic:** a brand-new airline's founding aircraft starts out reserved to you, and the moment your fleet grows to a second aircraft FSOps makes sure at least one aircraft is still reserved to you (never forcing a specific one — see below). Releasing your only reserved aircraft is allowed, but FSOps warns you plainly first, since it means your whole fleet becomes fair game for virtual pilots and nothing is held back for you — you can reserve one again at any time.

If you're restoring an older save from before this rule existed, see [Troubleshooting](troubleshooting.md#a-restored-save-had-an-aircrafts-reservation-released) for what FSOps does automatically to resolve a database where an aircraft was marked both reserved and scheduled.

## Selling an aircraft, ending a lease, and settling a loan early

Acquiring aircraft was always possible; getting rid of one, or clearing debt early, now is too — all three actions live on the Fleet page (loan repayment is also on the [Finances page](#the-finances-page)), and all three follow the same pattern: **you're shown a firm figure before you commit, and if that figure has genuinely moved by the time you confirm, FSOps refuses the action and shows you the new one rather than silently charging something different.** That isn't a bug or an error to work around — it's a safety feature. The figures involved (an aircraft's condition, wall-clock time since your last lease payment, a loan's outstanding balance) can all move in the background while a confirmation dialog is sitting open, most often because a virtual pilot's flight or the monthly billing cycle landed in between, so FSOps re-checks rather than trusting a number that might already be stale. If you see this, just re-open the action — the fresh figure will be right there.

### Selling an owned aircraft

Available for any aircraft you **own** outright (not leased). The sale value is **depreciated** — it falls with the aircraft's airframe hours and its condition, and drops further if it's currently grounded for a check — so buying an aircraft and immediately selling it again always loses money, and flying it first and then selling costs you more still. FSOps shows the sale value, the new-aircraft price it was worked out from, the aircraft's current condition and hours, before you confirm. Selling is blocked while the aircraft is actually flying, and while it's on a virtual pilot's standing schedule (FSOps names the pilot and the leg count rather than silently unassigning it — remove it from their schedule first). A grounded-for-maintenance aircraft can still be sold, at a worse figure — exactly the situation where you'd want to get rid of one. **Selling does not pay off any loan against your airline** — loans are borrowed against your airline's cash flow, not secured on a specific aircraft, so your repayments continue exactly as scheduled; FSOps states this plainly on the quote. Selling your last aircraft is allowed, with a clear warning that it leaves your airline unable to fly.

### Ending a lease early

Available for any aircraft you **lease**. Ending it before the end of a billing period charges a **pro-rata amount** for the part of the current 30-day period you've actually had it, plus a separate **early-termination fee** — so timing the return around a payment doesn't dodge anything, and leasing stays a real commitment rather than a free rental you can hand back the moment it stops suiting you. The same blockers apply as selling (not while flying, not while on a standing schedule, loan unaffected), plus FSOps won't let the charge take your cash balance negative.

### Settling a loan early, fully or partially

From the Finances page's Loans tab (or the Fleet page): **pay off a loan in full**, or **overpay any amount** against the principal.

- **Full settlement** shows the outstanding balance, an **early-settlement fee**, the total payoff figure, and the interest you'll save by clearing it now, before you confirm.
- **Overpaying** reduces what you owe by exactly the amount you pay — it **shortens how long the loan runs**, not your monthly payment, which stays the same until the loan finishes early. There's no settlement fee on an overpayment; the fee only applies to closing a loan out completely.
- Either action is blocked if it would take your cash balance negative.

## The aircraft catalogue and registrations

FSOps' catalogue covers 25 real airliner types across three categories, each with a real seat count, range, cruise speed, and a lease rate and purchase price for both playstyles:

- **Narrowbody** — Airbus A319/A320/A321 and their neo variants, Boeing 737-700/-800/-900 and MAX 8, Boeing 757-200.
- **Widebody** — Airbus A330-200/-300, A350-900, A380-800, Boeing 767-300ER, 777-300ER, 787-9, 747-8.
- **Regional** — Embraer E170/E175/E190/E195, Bombardier CRJ-700/-900, ATR 42-600/72-600, Dash 8 Q400.

The buy/lease dialog has a search box (matches ICAO type, manufacturer, or name — try "A350", "Boeing", or "737") and a category filter, so finding the aircraft you actually fly doesn't mean scrolling a long list.

**Registrations** are generated in the format your airline's home country actually uses, derived from your hub airport at the moment you acquire each aircraft (an aircraft already in your fleet is never retroactively re-registered if you never change hub): a UK-hubbed airline gets tails like `G-EZBA`, Germany `D-AIMA`, France `F-GXXX`, Ireland `EI-XXX`, the Netherlands `PH-XXX`, Spain `EC-XXX`, and the US its own `N` + one-to-five-character format starting with a digit (`N737FS`). Every acquisition dialog pre-fills a fresh, correctly-formatted suggestion — hit the shuffle button for another, or type your own registration to match a specific livery (uppercase letters, digits and hyphens, unique within your fleet — FSOps doesn't enforce a country's format on a custom entry, since you know your own repaint better than a validator does). Renaming an aircraft already in your fleet works the same way, from the Fleet page.

## Maintenance

Every aircraft needs an **A-check every 500 flight hours** and a **C-check every 4,000** — the same cycle, and the same cost, for both playstyles: an A-check runs **£45,000**, a C-check **£320,000**, posted straight to your ledger when it triggers. An A-check is a routine inspection: it restores 35 percentage points of condition (capped at 100) and grounds the aircraft for a stated period. A C-check is a full overhaul: it restores condition to 100 outright, and grounds the aircraft for longer. Condition decays by a small amount for every flight hour flown in between, so an aircraft flown hard between checks arrives at its next one more worn than one flown lightly.

How long a check grounds the aircraft depends on your [playstyle](#playstyles):

| | Casual | True-life |
|---|---|---|
| A-check downtime | 4 hours | 24 hours |
| C-check downtime | 24 hours | 336 hours (about 14 days) |

While grounded, the aircraft shows **In maintenance** on the Fleet page with the exact time it'll be back, and any route that would need it shows as not flyable on the Fly screen with the same reason and return time — see [Troubleshooting](troubleshooting.md#an-aircraft-is-grounded-for-maintenance) if you weren't expecting it. It's released automatically the moment its downtime elapses; nothing needs to be pressed. A used aircraft (see [above](#buying-leasing-and-financing-aircraft)) starts 70% of the way into both cycles, so its first check comes around much sooner than a fresh airframe's would.

**A check never grounds an aircraft mid-flight.** Hours only accrue, and a check only actually triggers, at the moment a flight completes — for a flight you're tracking yourself, one you complete manually, or a virtual pilot's flight alike. If a check would have become due partway through a sector, it simply waits until shutdown; nothing about maintenance can pull an aircraft out from under a flight already in progress. This holds regardless of playstyle and is a permanent rule, not a setting.

**A grounded aircraft suspends a virtual pilot's schedule instead of cancelling it — a choice you control per pilot.** Above the calendar in the schedule builder, a **Pause during maintenance** switch (on by default) decides what happens if that pilot's assigned aircraft is grounded when one of their scheduled legs comes due. It's a property of the pilot's whole weekly schedule, not any single day or leg.

- **On (the default):** the occurrence is recorded as **Suspended** rather than skipped or cancelled — no cancellation fee under either playstyle, since the aircraft needing a check isn't a mistake in the schedule. It resumes on its own the moment the aircraft is released; nothing needs rebuilding.
- **Off:** every occurrence due while the aircraft is grounded is recorded normally instead — **Skipped** with no charge under Casual, or **Cancelled** with a real fee under True-life, exactly as for any other unflyable occurrence (see below). The builder shows a warning the moment you switch it off: under True-life, a two-week C-check against a daily schedule means **fourteen separate cancellation fees** for something the pilot couldn't have avoided, so think carefully before turning it off on that playstyle.

Switch it back on at any time — it applies to occurrences from that point on, and doesn't retroactively change flights already recorded.

**Bringing a check forward yourself.** The Fleet page has a **Perform maintenance now** action on any aircraft that isn't already grounded or airborne. It shows, for both an A-check and a C-check: the cost, the downtime, how many hours remain until that check would fall due naturally, and which pilots' schedules (pilot, day, route) would be affected — all before you confirm. The trade-off is real and stated plainly: you pay the **full cost** of the check and **forfeit whatever hours were left** on the current cycle, in exchange for choosing exactly when the downtime lands rather than having it ambush you mid-week. It's blocked while the aircraft is flying, for the same reason maintenance never interrupts a flight in progress above.

## Hiring and assigning virtual pilots

As your airline grows, you won't want to fly every route yourself. The **Pilots** page (main navigation) is where virtual pilots — hires who fly a standing weekly schedule for you, on the real-world clock, including while FSOps itself is closed — are hired, released, and given their schedules.

### Hiring and releasing

Select **Hire pilot**, optionally give them a name (leave it blank and they're named "First Officer" plus a number), and confirm. There's no upfront hiring cost — a new pilot costs you nothing until the next [monthly billing cycle](#the-monthly-billing-cycle) tick, which charges their salary (the same **£9,000/month** every pilot earns, including you) whether or not they've flown anything yet. You can hire as many pilots as you want; each one needs their own schedule to actually earn their keep.

The Pilots table shows every pilot you employ — your own entry alongside every virtual pilot you've hired — with their status (**Available**, **Flying**, or **Inactive**), skill rating, hours flown, monthly salary, and (for virtual pilots) a weekly summary of sectors flown and expected revenue once they have a schedule. **Your own hours flown accrue from your tracked flights** the same way a virtual pilot's do from theirs, so your entry keeps up to date with how much you've actually flown. **Release** removes a pilot; it can't be undone, and it's blocked while they're actually mid-flight — release them once they land, or wait it out.

### Skill, landing quality, and idle decay

Every pilot — you included — has a skill rating, starting at **50** for a brand-new hire (and for you, at founding). It drives the delay variance and simulated landing quality the seeded economy generates for a virtual pilot's flights, since there's no live telemetry to measure a real landing from; it's the one thing that varies between virtual pilots' flights beyond the schedule itself. Your own entry tracks the same number, purely for the record — your real flights are always judged by real telemetry, never by this rating, so it has no effect on how you personally are scored.

**Skill grows with hours flown, but never quite reaches perfect.** Every hour a pilot flies nudges their rating up, with the gains getting smaller the more experienced they already are — the same shape as real proficiency, where the first few hundred hours matter far more than the next few hundred. It approaches a ceiling well short of 100 and never actually gets there, so even your most experienced hire's flights keep a little natural variance. This applies to your own hours too, so your entry on the Pilots page keeps climbing the more you fly, even though it never changes how your own flights are paid or scored.

**A virtual pilot left with no standing schedule for a while starts losing some of what they earned.** There's a two-week grace period after a pilot's last flight before any decay begins at all — a normal gap between scheduled duty days never looks like neglect — and past that, skill erodes gradually back toward the starting rating the longer they go unflown, roughly halving what they'd earned above 50 for every further month left idle. **A pilot on a standing weekly schedule keeps flying on the real-world clock even while FSOps is closed**, so their `LastFlewUtc` stays fresh and decay never reaches them — it only ever catches a pilot you've deliberately left with no schedule at all. **Your own record never decays**, under any circumstances: it exists purely to mirror your hours flown, and there's nothing to protect by eroding a number that was never used to judge you in the first place.

The Pilots page tells you exactly where each virtual pilot stands — a plain line under their skill rating reads one of: flew on a given date (nothing to note), a countdown once idle time is closing in on the two-week grace period ("decay starts in 3 days if still idle"), or, once decay has actually begun, both figures side by side — what their hours alone earned versus what they're actually sitting at now — so a lower number always comes with an explanation rather than reading as a bug.

### Building a weekly schedule

Open a pilot's schedule from the Pilots page. Each virtual pilot gets **one week-long calendar that repeats indefinitely** — set it once and they fly that pattern every week until you change it. The grid is laid out like a diary: **time runs down, days run across**, and each leg occupies a block sized to its full gate-to-gate duration (preflight, taxi, climb, cruise, descent, taxi-in — not just airborne time), so a day that's genuinely too full to fit is visibly over-stuffed rather than failing with an error later.

**An aircraft belongs to the whole duty day, not to one leg.** Click an empty day (or its "+" button) and FSOps asks you to **pick an aircraft first** — every fleet aircraft is listed, with anything that can't be used for that day shown disabled and why (in maintenance until a stated time, or reserved to you — with a link straight to the Fleet page to release it). Only once an aircraft is chosen does FSOps offer the legs *that aircraft* can actually fly from wherever it will be. This is deliberate: because one aircraft covers the whole day, "does the next leg depart from where the last one landed" holds automatically rather than needing to be checked leg by leg, and a gap shown between two legs always genuinely means a turnaround on that one airframe. Changing the aircraft assigned to a day that already has legs on it asks you to confirm first, since it clears them.

**Two views of the same week, plus a read-only overview.** Toggle between **by pilot** (this pilot's whole week) and **by aircraft** (where each of your aircraft is and what it's doing) while editing. On the Pilots page, a separate, read-only **schedule overview** shows every aircraft in your fleet as a row across the week, legs colour-coded by pilot — the place to answer "is my fleet actually being used", at a glance, across every pilot at once. Editing always happens in the per-pilot view; the overview is for seeing the whole picture.

**Anything that can't fly is shown, never hidden.** An aircraft or a leg you can't currently pick appears disabled with **one short reason** and, wherever there's a fix, a link straight to the place that applies it — reserved aircraft link to the Fleet page, a missing repositioning leg tells you to schedule one rather than sending you to create a route you already have. Saving a schedule with a genuine conflict (two same-origin legs on different aircraft in one day, an aircraft double-booked across pilots, not enough turnaround or rest) is refused with the conflicts spelled out in plain language, naming the aircraft, the day, and what would fix it.

**Rest, duty length and turnaround are enforced, cyclically across the week** (so Saturday's last leg connects to the following Monday's first, and a pilot's last flown day still gets its rest before their week starts again): at least **10 hours' rest** between a pilot's duty days, a maximum **13-hour duty day**, and at least **45 minutes'** turnaround between two legs on the same aircraft.

**A blank week is the hardest part of any planner**, so an empty schedule offers a **Suggest a starter schedule** button: FSOps proposes a same-day out-and-back on weekday mornings for whichever aircraft and routes are actually available, checking every leg against the real rules as it builds it, so what you get is always something you could save immediately and adjust from there — never a proposal that would fail its own validation.

Once a pilot has a schedule, their flights resolve automatically — see [The wall-clock economy](#the-wall-clock-economy-flying-while-youre-away) below for exactly how and when.

## The wall-clock economy: flying while you're away

A virtual pilot's scheduled flights don't complete while you watch — they complete against the **real-world clock**, whether or not FSOps is even running. Every 60 seconds (and once immediately on startup, so a long-closed app catches up right away rather than waiting a minute), FSOps checks every virtual pilot's schedule for legs whose departure time has passed and resolves them:

- **A flyable occurrence is flown as a full economic citizen** — it goes through exactly the same economics as a flight you fly yourself: ticket revenue, fuel, landing/handling/parking/passenger fees, maintenance accrual, and crew cost all post to your ledger, the aircraft's hours and position update, its condition wears, and it can trigger a maintenance grounding just as a player flight can.
- **An occurrence that isn't flyable — the assigned aircraft is still elsewhere, in maintenance, or already airborne — is recorded rather than silently skipped or dropped.** What happens next depends on your [playstyle](#playstyles): under **Casual**, it's recorded as **Skipped** with no cost — the airline forgives a schedule you haven't perfected yet. Under **True-life**, it's recorded as **Cancelled**, with a real cancellation fee posted to your ledger — a badly-planned schedule genuinely costs you, which is what gives the schedule builder's warnings weight. Either way, the specific reason is stored on the flight record, the same way it would be explained while you were building the schedule.

**Closing FSOps for a while and reopening it catches up on everything that was due, all at once**, exactly like the monthly billing cycle does — don't be surprised to see a batch of completed (or skipped/cancelled/suspended) flights and their ledger lines dated across the time you were away, rather than trickling in one at a time. Catch-up is capped per pass (at most 500 occurrences, looking no further than about 400 days ahead of where it left off), so an extremely long gap resolves over a few passes a minute apart instead of in one unbounded burst, but it still resolves in full. It can't be tricked by winding your system clock forward or back either — resolution only ever advances for time that has genuinely, provably passed.

## The "while you were away" summary

Because billing and virtual flights both resolve against the real-world clock rather than the time FSOps happens to be open, closing the app for a while and reopening it can land a materially different cash balance with no warning — several months of lease payments arriving at once looks exactly like a bug unless something explains it. So on startup, if enough happened while you were gone, FSOps shows a **"While you were away"** dialog before anything else: how long the app was closed, everything charged broken down by category, what your virtual pilots flew and earned (sectors, revenue, cost, net), any maintenance that fell due, and any flight that was skipped, cancelled or suspended, with the reason for each. It links straight through to the [Finances page's](#the-finances-page) ledger for the full detail, and a **Got it** button dismisses it — it won't reappear for the same window once acknowledged, only for whatever happens next. A very short gap (a normal restart, a quick reload) never triggers it; there has to be something genuine to report.

## The Finances page

The **Finances** page (main navigation) is where you actually run the airline from, rather than piece its state together from the Fleet and Pilots pages. Four figures sit at the top always: your **cash balance** and its change over the last 30 days, and your **income**, **expenditure** and **net profit or loss** for the current 30-day billing period. Below that, tabs cover each area in turn:

- **Leases** — every active lease: aircraft, type, monthly rate, and the real date its next payment falls due (a rolling 30 days from your airline's own clock, never "the 1st of the month" — see [The monthly billing cycle](#the-monthly-billing-cycle)), plus your total monthly lease commitment across the fleet and an **End lease** action for each — see [above](#selling-an-aircraft-ending-a-lease-and-settling-a-loan-early).
- **Loans** — every loan: principal, outstanding balance, interest rate, monthly payment, remaining term, and interest still to pay, with **Repay** taking you to full settlement or an overpayment (see [above](#selling-an-aircraft-ending-a-lease-and-settling-a-loan-early)).
- **Pilots** — every pilot's monthly salary, sectors flown, ledger-derived revenue and operating cost, and an estimated total cost and net contribution for the window shown. The estimate columns are labelled **"Est."** and carry a tooltip explaining why: they add the pilot's monthly salary prorated to the period you're looking at, and a prorated figure is never itself a posted ledger line — the real salary line posts in full on its own monthly cycle, wherever that falls. Everything else on this tab is real, ledger-derived money.
- **Costs** — your operating costs split into **fixed** (leases, salaries, insurance, loan repayments) and **variable** (fuel, landing, handling, parking, passenger charges, turnaround, maintenance, crew), because they behave completely differently: fixed costs are owed whether or not you fly, variable ones only bite when you do.
- **Routes** — profit and loss per route: sectors flown, ledger-derived revenue, cost and profit for the window, so you can see which routes in your network are actually worth flying rather than guessing from the fare alone.
- **Ledger** — every transaction, filterable by category, newest first, each drillable to the flight that produced it where one exists.

Every figure that isn't explicitly marked as an estimate comes straight from posted ledger transactions, the same rule the rest of the app follows — nothing shown here can disagree with what actually moved your cash balance.

## The live operations map

The **Dashboard**'s "Live operations" card shows your whole route network plus every aircraft currently in the air — your own tracked flight if you're flying, and every virtual pilot's currently-airborne scheduled leg, all on the same map. A virtual pilot's aircraft isn't a stored position: it's calculated fresh each time from their schedule and how much of that leg's block time has elapsed, so it's always consistent with how that flight will actually resolve once its time comes.

Hover any aircraft for a flight card: flight number, route, pilot name, aircraft registration and type, scheduled departure and estimated arrival times (in UTC), elapsed versus remaining time, percentage complete, and current phase (taxi out, climb, cruise, descent, taxi in). Your own aircraft is badged **You**; every other aircraft is badged **Virtual**, so it's always clear which one you're actually flying. When nothing is airborne, the map simply shows your network with no aircraft on it — no error, no placeholder text needed.

### Online VATSIM controllers

The same map shows who's actually controlling the airspace you fly in — no setting to turn on, no account or Pilot ID required, since this only reads VATSIM's public status feed. Each controller shows a callsign, position (Tower, Approach, Center, and so on), frequency, and how long they've been logged on.

**The list follows the map.** It names what's visible in the current view, so panning and zooming changes both together — look at the UK and you get UK controllers, not a global list you have to scroll. Controllers covering one of your own airports are listed first and marked with a filled icon, so your network stands out without hiding everyone else on screen.

**Coverage is drawn two ways, and the difference is real.** **Sectors** (Center and Flight Service) are drawn as their actual published FIR boundary, from data bundled with FSOps. **Terminal** positions (Tower, Ground, Delivery, and approach named after an airport) are drawn at the airport with a dashed circle showing approximate range — that circle is how far the controller's client is set to see, not the shape of anything they control. The map legend says which is which, so you never have to remember.

**What isn't shown, and why.** Approach TRACONs that aren't named after an airport have no published boundary data available, so FSOps shows nothing rather than inventing a shape — a wrong boundary on a map reads as authoritative. There are **no altitude limits** in this data either, so a sector polygon says nothing about which levels are being worked, and top-down coverage is never inferred. An empty area means "FSOps can't say", not necessarily "nobody is there".

**Other pilots' traffic is deliberately left off entirely** — this is about airspace coverage, not a multiplayer traffic display. If VATSIM's feed is temporarily unreachable the list says so plainly ("ATC data unavailable right now — the map and your flight are unaffected"), and everything else keeps working exactly as normal. Nothing about your flight or the economy depends on any of this.

FIR boundaries © VAT-Spy Data Project, CC BY-SA 4.0.

## Statistics dashboards

The **Stats** page (main navigation) is where your airline's history turns into trends rather than a single current snapshot. A period selector at the top switches the whole page between the trailing **7, 30 or 90 days** — every figure below is measured fresh from your completed flights and ledger over exactly that window, never a running total that ignores the period.

- **Performance** — a chart of on-time performance and load factor, one point per day that had at least one completed sector (a quiet day is simply absent, never a fabricated 0%). On-time performance here uses the identical rule your [reputation card](#your-airlines-reputation) does, so the two can never disagree about whether a given day counted as punctual.
- **Finance** — the same revenue/cost split and per-route profit-and-loss figures shown on the [Finances page](#the-finances-page), charted rather than tabulated. These aren't recalculated separately — they're the exact same numbers, so the two pages can never show you conflicting totals.
- **Fleet** — every aircraft's sectors flown, hours flown and idle in the period, a utilisation percentage, hours to its next A- or C-check, and current condition — the same "how close to its next check" figures the Fleet page itself shows, so they can't drift apart either.
- **Pilots** — a logbook covering every pilot who's flown in the window, you included: sectors, hours, on-time percentage, and average landing rate. A figure that genuinely couldn't be measured (for example, a pilot whose every sector in the window was a manual completion) reads "Not measured," never a misleading zero.

Every one of the four tabs has its own **Export CSV** button, exporting exactly the rows currently on screen for the period selected — handy for keeping your own record outside FSOps, or just looking at the raw numbers in a spreadsheet.

## In-game panel

A compact, read-only panel that shows your live flight and airline status inside MSFS itself, so you don't need to alt-tab out to a browser mid-flight. It's the same `/panel` page whether you reach it inside MSFS or by browsing to `http://localhost:5977/panel` yourself — large type, high contrast, built to stay readable at a small panel size.

**In the air**, it shows your flight number, phase, progress, ETA and block time remaining, fuel and passengers on board, and your airline's cash balance. **On the ground after landing**, it switches automatically to a short debrief — landing rate, block time variance against the plan, and what the sector earned — for a little while after touchdown. It survives FSOps restarting mid-flight: if the connection drops, it says so and keeps retrying on its own, then catches up automatically the moment it reconnects, exactly the way the main Fly screen does.

### Getting the panel into MSFS

There are two ways in, and they install exactly the same thing:

- **The setup wizard's "Connect your MSFS panel" step** looks for your Community folder automatically, lets you confirm or type a different path, and installs the panel when you finish. It's genuinely optional and skippable.
- **Settings → [MSFS in-game panel](#msfs-in-game-panel)** does everything afterwards: it shows what's actually installed, and installs, reinstalls, repairs, moves or removes it. If you skipped the wizard step, this is where you add the panel later; if you move MSFS to another drive, this is where you move the panel with it.

After installing, **restart MSFS if it's already running** — the sim reads its Community folder at startup, so a package added underneath a running sim won't appear until the next launch.

**One thing to be aware of.** The compiled component that registers the FSOps button with MSFS's toolbar is built with the Microsoft Flight Simulator SDK and ships with FSOps, so a normal install includes it and Settings reports **"Appears in the MSFS toolbar"**. If Settings ever says the toolbar button won't appear, that's FSOps telling you the file is genuinely missing from the install rather than reassuring you — use **Reinstall / repair**, and see [Troubleshooting](troubleshooting.md#the-toolbar-button-isnt-there) if it persists.

Everything else about FSOps works the same with or without the in-game panel — the flight brief, live tracking, the report card and the full web UI at `http://localhost:5977` are unaffected either way.

## A worked example, start to finish

This walks through founding an airline, building a route, and flying it, with real figures — not invented ones. The plan-panel numbers below (distance, block time, fuel, fare) are fixed by the aircraft and the two airports, so they'll always come out the same. The demand and revenue numbers came from actually running FSOps and creating this exact airline and route on **8 August 2026** — your own numbers for the same route will differ, because demand factors in the month and day of the week (see [The economy simulation](#the-economy-simulation)), and fuel price drifts by a small amount day to day. Treat the shape of the example as reliable and the exact pounds-and-pence as illustrative.

**Founding the airline.** Through the setup wizard: name it "Avon Air", ICAO code `AVN`, home base Bristol Airport (EGGD), playstyle **Casual**, strategy **Domestic**, starter aircraft a Boeing 737-800 (189 seats, service ceiling 41,000 ft), currency GBP. No startup loan. Founding the airline leases the 737-800 (a one-month deposit of **£30,000** comes straight out of your starting capital — Casual's rate for either starter type; see [Playstyles](#playstyles)) and hires you as its first pilot — you land on the Dashboard with a fleet of one at EGGD and a cash balance of **£1,970,000** (£2,000,000 starting capital minus that deposit). From here on, the same £30,000 lease, plus £6,000 monthly insurance on the aircraft and your own salary, will post again automatically every 30 days through the [monthly billing cycle](#the-monthly-billing-cycle) — it isn't a one-off charge.

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

**Financial outcome** shows every line this sector actually posted: **ticket revenue +£11,310.00** (174 pax × £65.00 — booked passengers come from the demand model, not from how the flight actually went), **fuel** already charged at departure (not repeated here), **landing fee -£750.50** (£9.50/tonne × the 737-800's 79-tonne MTOW at Large-airport Edinburgh), **handling -£513.50**, **parking -£94.80**, **passenger charges -£2,088.00** (£12.00 × 174 pax), a flat **turnaround/gate fee -£450.00**, **maintenance accrual -£217.00** (£210/hour × the 62 minutes you actually flew, not the planned 65), and **crew cost -£351.33** (£340/hour, same actual duration). Net for the sector comes out to roughly **£4,850**, give or take about £120 either way depending on that day's exact fuel price — a genuinely profitable leg. That covers the per-sector economics; the £30,000 monthly lease and other fixed costs above are billed separately on their own 30-day cycle, not out of this sector's revenue directly. Fly Edinburgh back to Bristol next to complete the round trip — expect similar passenger and revenue figures, since the two Large airports and the distance are symmetric either direction, but not necessarily the same fuel charge: fuel now persists on the aircraft between flights (see [Fuel and tankering](#fuel-and-tankering)), so the return leg is only charged for whatever's needed to top up beyond what's still physically in the tank after landing at Edinburgh, which depends on exactly how much was uplifted for the outbound sector.
