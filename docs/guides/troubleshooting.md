# Troubleshooting

Problems and solutions for running FSOps. If you don't find your issue here, see [how to report a problem](#how-to-report-a-problem) at the bottom.

## Table of contents

- [The UI won't load / port 5977 is already in use](#the-ui-wont-load--port-5977-is-already-in-use)
- [The UI shows "FSOps UI not built yet"](#the-ui-shows-fsops-ui-not-built-yet)
- [The map shows no background tiles](#the-map-shows-no-background-tiles)
- [The setup wizard keeps reappearing](#the-setup-wizard-keeps-reappearing)
- [Route creation is refused](#route-creation-is-refused)
- [My currency looks wrong](#my-currency-looks-wrong)
- [Strategy profile figures won't load](#strategy-profile-figures-wont-load)
- [My fare or revenue numbers look different than yesterday](#my-fare-or-revenue-numbers-look-different-than-yesterday)
- [My fuel charge doesn't match what I expected, or the return leg wasn't free](#my-fuel-charge-doesnt-match-what-i-expected-or-the-return-leg-wasnt-free)
- [Why did my passenger numbers drop](#why-did-my-passenger-numbers-drop)
- [An aircraft is grounded for maintenance](#an-aircraft-is-grounded-for-maintenance)
- [An aircraft can't be flown because it isn't reserved](#an-aircraft-cant-be-flown-because-it-isnt-reserved)
- [A restored save had an aircraft's reservation released](#a-restored-save-had-an-aircrafts-reservation-released)
- [Selling, ending a lease, or settling a loan was refused because the figure changed](#selling-ending-a-lease-or-settling-a-loan-was-refused-because-the-figure-changed)
- [A larger-than-usual charge appeared after leaving FSOps closed](#a-larger-than-usual-charge-appeared-after-leaving-fsops-closed)
- [A loan's interest rate is higher than I expected](#a-loans-interest-rate-is-higher-than-i-expected)
- [A starting loan was refused at founding](#a-starting-loan-was-refused-at-founding)
- [A virtual pilot's flight was skipped, cancelled or suspended instead of flown](#a-virtual-pilots-flight-was-skipped-cancelled-or-suspended-instead-of-flown)
- [A virtual pilot's aircraft isn't where I expected it](#a-virtual-pilots-aircraft-isnt-where-i-expected-it)
- [Why is my pilot worse than they were](#why-is-my-pilot-worse-than-they-were)
- [I can't release a pilot](#i-cant-release-a-pilot)
- [SimBrief import did nothing](#simbrief-import-did-nothing)
- [No controllers are showing](#no-controllers-are-showing)
- [A controller is online but never appears anywhere on the map](#a-controller-is-online-but-never-appears-anywhere-on-the-map)
- [En-route sectors never appear, only airport circles](#en-route-sectors-never-appear-only-airport-circles)
- [My flight doesn't show as "flown online"](#my-flight-doesnt-show-as-flown-online)
- [The "Flown online" history card is empty, or says I haven't set a CID](#the-flown-online-history-card-is-empty-or-says-i-havent-set-a-cid)
- [Other VATSIM traffic isn't showing on the map](#other-vatsim-traffic-isnt-showing-on-the-map)
- [The toolbar button isn't there](#the-toolbar-button-isnt-there)
- [The panel opens but shows nothing](#the-panel-opens-but-shows-nothing)
- [I moved my Community folder, or reinstalled MSFS](#i-moved-my-community-folder-or-reinstalled-msfs)
- [FSOps never tells me about updates](#fsops-never-tells-me-about-updates)
- [A downloaded update was rejected, or disappeared](#a-downloaded-update-was-rejected-or-disappeared)
- [Where a downloaded update goes, and why FSOps won't run it](#where-a-downloaded-update-goes-and-why-fsops-wont-run-it)
- [Where the database lives](#where-the-database-lives)
- [MSFS won't connect over SimConnect](#msfs-wont-connect-over-simconnect)
- [Flight tracking stopped mid-flight](#flight-tracking-stopped-mid-flight)
- [A flight is stuck needing attention](#a-flight-is-stuck-needing-attention)
- [Why did completing manually cost me reputation](#why-did-completing-manually-cost-me-reputation)
- [A route doesn't show as flyable](#a-route-doesnt-show-as-flyable)
- [Where to find log files](#where-to-find-log-files)
- [How to report a problem](#how-to-report-a-problem)

## The UI won't load / port 5977 is already in use

**Symptom:** Browsing to `http://localhost:5977` shows nothing, a connection-refused error, or the terminal running FSOps reports the address is already in use.

**Solutions:**

1. Make sure the backend is actually running — check the terminal window for `dotnet run --project src/FSOps.Server`. If it exited or errored, read the terminal output for the cause.
2. If the terminal shows an error that port 5977 is already in use, something else on your machine has claimed it. Find and close whatever's using it, or close any other running copy of FSOps (only one instance can bind the port at a time).
3. From PowerShell, you can check what's holding the port:
   ```
   Get-NetTCPConnection -LocalPort 5977 -ErrorAction SilentlyContinue
   ```
   If that returns a result, note the owning process ID and close that application (or restart your machine if you can't identify it safely).
4. Once the port is free, restart FSOps with `dotnet run --project src/FSOps.Server` and reload the browser tab.

## The UI shows "FSOps UI not built yet"

**Symptom:** The backend starts fine, but the browser shows a message saying the frontend hasn't been built.

**Cause:** `FSOps.Server` serves the compiled web UI from `src/fsops-web`, but that UI has to be built first — it isn't compiled automatically when you build the backend.

**Solution:**

```
cd src/fsops-web
npm install
npm run build
cd ../..
```

Then restart the server (`dotnet run --project src/FSOps.Server`) and reload the browser tab. See [Getting Started](getting-started.md#4-build-the-frontend) for the full walkthrough.

## The map shows no background tiles

**Symptom:** A map — either the route-planning/network map on the Routes page, or the live moving map on the Fly screen — shows an explicit **"Map tiles unavailable — showing offline view"** banner. Route lines, the great-circle path, your hub marker, airport markers, and (on the Fly screen) your live aircraft position all still show up; only the background imagery is missing.

**Cause:** The map's background tiles are fetched over the internet from a raster tile provider. Everything else on the map (route geometry, airport positions, your live position) comes from your local database and live telemetry, and works fully offline.

**Solution:** Check your internet connection. There's nothing to configure — once connectivity is back, reload the page and tiles will load normally. If you're intentionally offline, you can still plan and create routes and fly fully tracked flights; you just won't see the background imagery underneath them.

## The setup wizard keeps reappearing

**Symptom:** FSOps opens into the full-screen airline setup wizard every time you start it, even though you thought you'd already founded an airline.

**Cause:** FSOps decides whether to show the wizard purely by asking the backend whether an airline currently exists for you (`GET /api/v1/airline`). The wizard appears whenever that comes back empty — either no airline has been created yet, or one was deleted (deliberately, via the settings [danger zone](user-guide.md#settings), or by having the database wiped — see [Where the database lives](#where-the-database-lives) below).

**Solution:** If you meant to keep your airline, check whether the database file still exists and hasn't been reset. If it's genuinely gone, there's no way to recover it short of a backup (see [Where your data lives](user-guide.md#where-your-data-lives)) — otherwise, just go through the wizard again.

## Route creation is refused

**Symptom:** The plan panel shows a red "This route can't be created yet" message and the **Create route** button stays disabled.

**Cause:** Only two things actually block a route: departure and arrival being the same airport, or **nothing in your entire fleet** being able to fly the sector. Range is measured as **practical** operating range — roughly **0.85×** the published figure once fuel reserves are accounted for — so a sector just inside the catalogue number can still be out of reach.

Range on its own is rarely the blocker, and it's worth knowing the three outcomes apart:

| What you have | What happens |
| --- | --- |
| A reserved aircraft that can fly it | Nothing — the route is created normally. |
| Nothing reserved can, but something in the fleet can | **Not a refusal.** You get guidance to reserve a suitable aircraft. |
| Nothing in the fleet can fly it at all | The red message, and the route is genuinely blocked. |

**Solution:** If you're being pointed at reserving an aircraft, reserve one that can fly the sector — the route itself is fine. If the route is genuinely blocked, pick a different airport pair or add an aircraft with more range from the Fleet page (see [Range](user-guide.md#range) and [Buying, leasing and financing aircraft](user-guide.md#buying-leasing-and-financing-aircraft)). Amber advisory warnings (short runway, strategy mismatch) look similar but don't block creation — only the red message does.

## My currency looks wrong

**Symptom:** Fares, balances, or prices look off after changing currency in settings, or don't match what you expected.

**Cause:** FSOps stores every amount internally in a single base currency unit and only converts it for display using your selected currency's fixed rate (see [Settings — Currency](user-guide.md#currency) and [Architecture](../architecture.md#money-is-stored-in-a-single-base-unit)). Rates are fixed at build time, not live exchange rates, so they won't match real-world rates exactly — and changing currency never changes your actual stored balance, only how it's displayed.

**Solution:** If a number looks wrong, double check which currency is currently selected in settings. If it still looks wrong after that, it's worth reporting (see [How to report a problem](#how-to-report-a-problem)) — but a mismatch with real-world exchange rates is expected behaviour, not a bug.

## Strategy profile figures won't load

**Symptom:** On the Settings → Airline page, each strategy profile card shows a message reading "Couldn't load the figures for each strategy" with a **Try again** button, instead of the usual fares/sensitivity/load-factor/costs breakdown.

**Cause:** The profile cards fetch their figures live from `GET /api/v1/airline/strategy-profiles`, which reads straight out of `economy-config.json` at request time. This can fail to load right after FSOps itself was updated or restarted while the browser tab was still open, or if the backend genuinely can't be reached.

**Solution:** Select **Try again**. If that doesn't help, refresh the browser tab so it reconnects to a freshly started backend. Choosing and saving a strategy still works even while the figures panel is showing this error — you just won't see the numbers behind each option until it loads.

## My fare or revenue numbers look different than yesterday

**Symptom:** The same route's suggested fare stays the same, but the expected passengers, load factor, or expected revenue shown in the route planner (or the actual revenue posted after a flight) has changed from one day to the next, with nothing in your airline or route having changed.

**Cause:** This is expected, not a bug. Passenger demand for a route factors in the month (a seasonality curve — August, for example, is a stronger month than February) and the day of the week, and fuel prices drift day to day by a small, deterministic amount per airport (see [The economy simulation](user-guide.md#the-economy-simulation)). Flying the same route on a different real-world day can genuinely produce different numbers.

**Solution:** Nothing to fix — if you want to sanity-check a figure, note the date you're comparing against, since demand and fuel price are both date-dependent by design.

## My fuel charge doesn't match what I expected, or the return leg wasn't free

**Symptom:** The fuel line on a report card is bigger or smaller than the flight brief's planned block fuel, or a return leg you flew straight back on wasn't free even though the aircraft landed with fuel still in the tank.

**Cause:** Not a bug — this is how fuel billing works now. FSOps charges each sector for what it actually **burned**, at the departure airport's price, not for a fixed planned figure and not for what's physically in the tank. That means: a sector that burns more than the plan expected (a longer taxi, more holding) costs more; one that burns less costs less; and a return leg is billed on its own burn from its own departure airport, independent of whatever fuel happened to be left over from the leg before it — see [Fuel billing](user-guide.md#fuel-billing) for the full explanation.

**Solution:** Nothing to fix. If a figure looks wildly off rather than just different, check the report card's "Actual vs. planned" fuel figure against what the flight brief predicted — a large gap there (rather than a small, explainable one) is worth a second look, but day-to-day variation in what a sector actually burns is expected.

## Why did my passenger numbers drop

**Symptom:** The same route, at the same fare, is now selling noticeably fewer seats than it used to — not the small day-to-day drift covered above, but a sustained drop over several flights.

**Cause:** Your airline's **reputation** has genuinely fallen, and reputation scales demand on every route — see [Your airline's reputation](user-guide.md#your-airlines-reputation) in the user guide. It moves mainly from on-time performance and, to a lesser extent, landing quality (yours or a virtual pilot's); a cancelled or skipped sector costs more than any delay; and abandoning a flight or completing one manually both cost a fixed amount too (see [Why did completing manually cost me reputation](#why-did-completing-manually-cost-me-reputation) below). A run of late or cancelled sectors is the most common cause, and it applies equally whether you flew them yourself or a virtual pilot did.

**Solution:** Check the reputation card on the **Dashboard** — it names what's actually been driving the number over your last several sectors (on-time percentage, cancellations, landing quality), not just the score itself. There's no quick fix beyond flying (or scheduling) a consistent run of on-time, completed sectors — the score moves slowly by design, over roughly 40–60 sectors to climb from 50 to 75, so a single good flight won't undo a bad stretch overnight, but it also means a single bad flight won't sink you either.

## An aircraft is grounded for maintenance

**Symptom:** An aircraft shows **In maintenance** on the Fleet page, or a route shows as not flyable on the Fly screen with a reason like "Your aircraft at EGGD is in maintenance until 2026-08-09 14:00 UTC."

**Cause:** Every aircraft needs an A-check every 500 flight hours and a C-check every 4,000 (see [Maintenance](user-guide.md#maintenance) in the user guide). A check due grounds the aircraft for a stated period — a few hours to a day under Casual, up to a fortnight for a True-life C-check — while it's serviced. This is expected behaviour, not a bug; a used aircraft (bought at a discount, already worn) reaches its first check sooner than a fresh airframe would.

**Solution:** Wait it out — the aircraft is released automatically the moment its downtime elapses, no action needed, and the exact return time is always shown. If you need to keep flying that route in the meantime, use a different aircraft in your fleet if you have one, or add another from the Fleet page. A "perform maintenance now" from the Fleet page can also bring a check forward on your own schedule instead of waiting for it to fall due naturally — see [Maintenance](user-guide.md#maintenance).

## An aircraft can't be flown because it isn't reserved

**Symptom:** On the Fly screen, an aircraft that's clearly sitting at the right airport still shows as not flyable, with the reason "Not reserved to you - reserve it from the Fleet page to fly it." — or the aircraft doesn't show up as an option in the flight brief at all beyond a disabled, greyed-out chip.

**Cause:** This is expected, and it's the whole point of aircraft reservation (see [Reserving an aircraft for yourself](user-guide.md#reserving-an-aircraft-for-yourself)). Reservation is now a hard rule, not a hint — you can only fly an aircraft that's currently reserved to you, regardless of where it's parked or whether a virtual pilot happens to be using it. An aircraft you've released to virtual pilots is simply off-limits to you until you reserve it again.

**Solution:** Go to the Fleet page and toggle that aircraft's reservation back to **reserved for you**. If it's currently on a virtual pilot's standing schedule, FSOps tells you which pilot and which legs before it lets you reserve it, and offers to clear them for you — do that, or edit the pilot's schedule yourself first if you'd rather keep those legs and use a different aircraft for this flight instead.

## A restored save had an aircraft's reservation released

**Symptom:** After restoring an older backup of your `%LOCALAPPDATA%\FSOps\` folder (see [Where your data lives](user-guide.md#where-your-data-lives)) and starting FSOps, an aircraft you're sure you had reserved for yourself now shows as released, or a route you expect to be able to fly shows "Not reserved to you" instead.

**Cause:** Aircraft reservation used to be a soft preference, with nothing stopping an aircraft from ending up both marked reserved for you *and* scheduled onto a virtual pilot's standing schedule at the same time. Now that reservation is a hard, mutually exclusive rule, FSOps checks for exactly this contradiction once on every startup and resolves it automatically in favour of the schedule: if an aircraft is both reserved and has active scheduled legs, its **reservation is released** and its **schedule is kept untouched** — the reasoning being that a saved weekly schedule represents many deliberate decisions, while a reservation flag is one click and easy to have drifted under the old rules. This can only affect a save that predates aircraft reservation becoming a hard rule; a save created since then can never develop this contradiction in the first place.

**Solution:** Nothing is broken and nothing needs undoing — the schedule the aircraft was already flying is exactly as it was. If you'd rather keep that aircraft for yourself, reserve it again from the Fleet page; FSOps will walk you through clearing the conflicting legs if you confirm. If this happened on an airline with more than one aircraft and it left you with **no** reserved aircraft at all, FSOps automatically reserves a different aircraft that has no scheduled legs of its own (preferring one at your home base) rather than leave you with nothing to fly — check the Fleet page to see which one, and reserve a different one yourself if you'd prefer.

## Selling, ending a lease, or settling a loan was refused because the figure changed

**Symptom:** Confirming a sale, an early lease return, or a loan payoff shows an error like "The sale value has changed since you last checked (was X, now Y) - please confirm the new figure." instead of completing the action.

**Cause:** This is a safety feature working as intended, not an error. Every one of these three actions shows you a firm figure before you confirm it, then recomputes that exact figure again at the moment you actually click confirm and checks it matches what you were shown. If it's genuinely moved in between — most often because a virtual pilot's flight or the monthly billing cycle posted something in the background while your confirmation dialog was open — FSOps refuses to charge you a different number than the one on screen, rather than silently posting whatever the figure has become.

**Solution:** Just try the action again. The dialog will show you the fresh figure, and confirming that one will go through normally (unless, of course, it drifts again in the same way).

## A larger-than-usual charge appeared after leaving FSOps closed

**Symptom:** Opening FSOps after a break shows a bigger change in cash balance than you expected — more than a single month's lease/insurance/salary would explain, several lease/insurance/salary lines with the same or nearby dates in the ledger, or a batch of virtual-pilot flights all dated across the time you were away.

**Cause:** This is expected, and it's two separate things landing at once. Lease payments, salaries, insurance and loan repayments post automatically every 30 days of real-world wall-clock time (see [The monthly billing cycle](user-guide.md#the-monthly-billing-cycle)); separately, every virtual pilot's scheduled flights resolve against the real-world clock too (see [The wall-clock economy](user-guide.md#the-wall-clock-economy-flying-while-youre-away)). Neither waits for FSOps to be running. If FSOps was closed for, say, 90 days, the next time it starts it catches up on all three months' worth of billing *and* every flight any pilot was scheduled to fly in that window, all at once, rather than skipping the time that was missed — each catch-up is capped per pass (billing: 24 periods, about two years; virtual flights: 500 occurrences, looking about 400 days ahead), so an extremely long gap catches up over a few passes a minute apart rather than in one burst, but you'll still see it land as a lump rather than trickling in.

**Solution:** Nothing to fix — check your airline's ledger and flight history (or the log files) for the dated lines to confirm they line up with the length of the gap. This can't be avoided by changing your system clock either: both catch-up processes only ever count genuinely elapsed wall-clock time.

## A loan's interest rate is higher than I expected

**Symptom:** A loan — especially a startup loan taken while founding an airline — carries the highest rate your playstyle allows (5% APR for Casual, 8% for True-life) rather than something closer to the base rate.

**Cause:** The rate is never something you choose — it's computed automatically from how much of your airline's trailing 30-day net operating cash flow the loan's monthly payment would consume, scaling from your playstyle's base rate up to its cap the more of that capacity it uses (see [Buying, leasing and financing aircraft](user-guide.md#buying-leasing-and-financing-aircraft)). A **brand-new airline has no trading history yet — its cash flow is exactly zero at the moment it's founded — so a startup loan is always priced at the cap.** That's the correct price for an unproven borrower, not a bug.

**Solution:** Nothing to fix for a startup loan; it's always at the cap by design. For a loan taken later in the Fleet page, the rate falls as your airline's trailing cash flow grows from flying — if you want a lower rate, build up a few weeks of profitable flying first, or ask for a smaller amount or a longer term, both of which lower the payment relative to your capacity.

## A starting loan was refused at founding

**Symptom:** In the airline setup wizard's review step, requesting a startup loan shows an error like "A starting loan of X exceeds the maximum Y allowed for a new Casual airline" instead of letting you proceed.

**Cause:** A startup loan (taken while founding your airline, before any trading history exists) is capped at a flat figure per playstyle — **£250,000 for Casual, £5,000,000 for True-life** — separate from the cash-flow-based cap that applies to a loan taken later from the Fleet page. A brand-new airline has no ledger yet, so its trailing cash flow is exactly zero, which is why a flat ceiling is used here instead: without it, the wizard could hand a new player a loan priced at the rate cap with a monthly payment far beyond what a solo airline can actually earn. The wizard's own loan option is off by default and starts at zero for exactly this reason — you have to deliberately opt in and choose an amount.

**Solution:** Lower the requested amount to within your playstyle's cap, or found the airline without a startup loan and take one later from the Fleet page once you have some trading history — at that point the cap is based on your actual cash flow rather than a flat figure, and can grow well beyond the starting cap as your airline earns.

## A virtual pilot's flight was skipped, cancelled or suspended instead of flown

**Symptom:** A virtual pilot's flight history shows a leg marked **Skipped**, **Cancelled**, or **Suspended** rather than completed, sometimes with a cancellation fee posted to the ledger.

**Cause:** A scheduled occurrence only flies if the aircraft assigned to it is actually available and at the right airport when its departure time arrives — see [The wall-clock economy](../guides/user-guide.md#the-wall-clock-economy-flying-while-youre-away). If it's mid-flight, or sitting at a different airport (most often because an earlier leg in the chain didn't land where the schedule expected), FSOps records the occurrence rather than silently dropping it or teleporting the aircraft, and what happens next depends on your playstyle: **Casual** records it as **Skipped** with no charge; **True-life** records it as **Cancelled** with a real cancellation fee, since a badly-planned schedule should genuinely cost something under that playstyle. If the reason is specifically that the aircraft is **in maintenance**, it's recorded as **Suspended** instead, under either playstyle — no cancellation fee, since the aircraft needing a check isn't a mistake in the schedule, and the occurrence resumes on its own the next time it's due once the aircraft is released.

**Solution:** Check the flight record for the specific reason (it names the aircraft and where it actually is, or which check it's waiting on). For a **Skipped** or **Cancelled** occurrence, this usually means the pilot's weekly schedule has a gap — a leg that assumes the aircraft is somewhere it won't actually be that day, most often because a repositioning leg is missing from an earlier day. Adjust the schedule so each aircraft's chain of legs is geographically continuous. For a **Suspended** occurrence, there's nothing to fix — wait for the aircraft to come out of maintenance, or bring the check forward yourself with "Perform maintenance now" on the Fleet page if you'd rather control when the downtime lands.

## A virtual pilot's aircraft isn't where I expected it

**Symptom:** The Fleet page shows a virtual pilot's aircraft at a different airport than you expected, or a route you thought was flyable for that aircraft shows as not flyable.

**Cause:** A completed virtual-pilot flight moves its aircraft's recorded location to wherever it actually landed, exactly the same rule as a player flight — see [Round trips and where your aircraft actually is](user-guide.md#round-trips-and-where-your-aircraft-actually-is). If a pilot's schedule doesn't bring an aircraft back to where the next day's chain expects it to start, that next occurrence won't be flyable — see [above](#a-virtual-pilots-flight-was-skipped-cancelled-or-suspended-instead-of-flown).

**Solution:** Check the aircraft's current location on the Fleet page against what the pilot's schedule assumes for each day, and adjust the schedule so a day's chain always starts from wherever the aircraft's previous chain actually left it.

## Why is my pilot worse than they were

**Symptom:** A virtual pilot's skill rating on the Pilots page has gone down since you last checked, rather than up — or a pilot you haven't looked at in a while shows a lower skill rating than a newly-hired one that's since flown a handful of sectors.

**Cause:** This is expected, not a bug — **skill decays when a pilot goes unflown for too long.** A pilot's skill normally climbs with hours flown, but if a virtual pilot has no standing schedule assigning them any legs, they don't earn hours, and after a two-week grace period with nothing flown, their rating starts eroding gradually back down toward where they started (50). The line under their skill rating on the Pilots page explains it directly — a countdown once idle time is closing in on the grace period, or, once decay has actually started, both what their hours alone earned and what they're actually sitting at now. See [Skill, landing quality, and idle decay](user-guide.md#skill-landing-quality-and-idle-decay) in the user guide for the full mechanics.

The most common cause is a pilot who was hired but never given a schedule, or one whose schedule was cleared (for example, because their aircraft was reserved back to you or sold) and never rebuilt. **Your own skill rating never decays**, regardless of how long you go without flying — it's purely a record of your hours, never used to judge your own flights, so there's nothing to protect it from.

**Solution:** Give the pilot a standing weekly schedule (or restore the one they had) — see [Building a weekly schedule](user-guide.md#building-a-weekly-schedule). A pilot flying that schedule keeps flying on the real-world clock even while FSOps is closed, which is what keeps their skill from decaying at all. There's no way to instantly restore lost skill short of flying them again; it recovers the same way it was earned, gradually with hours flown.

## I can't release a pilot

**Symptom:** Selecting **Release** for a pilot on the Pilots page fails, or the release action isn't offered.

**Cause:** A pilot can't be released while they're actually mid-flight (status **Flying**) — releasing them out from under an in-progress flight would leave that flight with no pilot to resolve against.

**Solution:** Wait for their current flight to resolve (virtual pilots resolve automatically on the wall clock — see [The wall-clock economy](user-guide.md#the-wall-clock-economy-flying-while-youre-away)), then release them.

## SimBrief import did nothing

**Symptom:** The flight brief's SimBrief OFP panel reads "Using the built-in plan" instead of pulling in your OFP, even though you've set a Pilot ID and generated a plan.

**Cause:** One of several ordinary reasons, all handled by falling back to FSOps' own plan rather than failing the flight: no Pilot ID set yet in [Settings → SimBrief](user-guide.md#simbrief), an incorrect Pilot ID, SimBrief has no plan on file for that Pilot ID (SimBrief itself can't distinguish "wrong ID" from "no plan filed" — FSOps can't tell them apart either), SimBrief was unreachable or timed out, or — the single most common cause in practice — **your latest OFP is filed for a different city pair than the route you're about to fly.** FSOps refuses to substitute a mismatched plan rather than silently applying the wrong fuel and altitude figures; see [Importing your OFP back](user-guide.md#importing-your-ofp-back).

**Solution:** Read the panel's own message — it names the specific reason. Most often, this means filing a fresh OFP in SimBrief for the exact route (same origin and destination) you're about to fly, then clicking **Check for OFP** on the flight brief's SimBrief OFP panel — you don't need to leave the Fly screen and come back; the button re-checks immediately. If you only just added your Pilot ID, double-check it in Settings, or use the link in the panel if it hasn't picked one up yet. Either way, this never blocks flying — the built-in plan is used automatically and the flight brief still shows complete, usable figures.

## No controllers are showing

**Symptom:** The Dashboard's controller list reads "No controllers online in this part of the map," even though you know someone is controlling on VATSIM.

**Cause:** The list follows the map. It shows the controllers whose coverage is **currently on screen**, so that the list and the map can never disagree while you're looking at both — if you've panned to somewhere quiet, or zoomed in past the sector you were expecting, the list empties out even though plenty of people are online elsewhere.

**Solution:** Zoom out or pan back. The wording tells you which situation you're in:

| Message | What it means |
| --- | --- |
| "No controllers online in this part of the map" | Somebody is online somewhere — just not in this view. Zoom out. |
| "No controllers online in this area right now" | Nothing FSOps can place is online anywhere. |
| "ATC data unavailable right now" | VATSIM's feed couldn't be read at all. Not the same as nobody being online — try again shortly, as FSOps refreshes its cached copy periodically rather than on every request. |

Controllers covering one of **your own** airports are listed first and marked with a filled icon, so they stay easy to pick out now that the list isn't restricted to them.

## A controller is online but never appears anywhere on the map

**Symptom:** You can see someone controlling on VATSIM — often an approach position like `NY_APP` or `SCT_APP` — but no amount of panning makes them show up in FSOps.

**Cause:** FSOps only draws a controller when it can say honestly where their coverage is, and there are two ways it can know that:

- **Sectors** (Center and FSS) are drawn as their **real published FIR boundary**, from boundary data bundled with FSOps.
- **Terminal** positions (Tower, Ground, Delivery, and approach named after an airport like `EGLL_APP`) are drawn at the airport, with a **dashed circle showing approximate range** — that circle is the range the controller's client is set to see, not the shape of anything they control.

Anything else is left out on purpose. **Approach TRACONs that aren't named after an airport have no published boundary data available anywhere**, so FSOps has nothing truthful to draw and shows nothing rather than inventing a plausible circle. A wrong shape on a map reads as authoritative, which is worse than an absence.

Two related limits worth knowing, since neither is visible on screen: coverage is **lateral only** — the boundary data carries no altitude limits, so a sector polygon says nothing about which levels are actually being worked — and **top-down coverage is never inferred**, so a centre controller working an airport with nobody local won't show at that airport.

**Solution:** None needed; this is working as intended. The map legend states the same distinction, and the frequency is still available in the VATSIM client itself.

## En-route sectors never appear, only airport circles

**Symptom:** Tower and ground controllers show up fine, but no Center or FSS position ever appears, anywhere in the world.

**Cause:** The bundled boundary data is missing or unreadable. FSOps ships two files in `data/vatspy/` beside the application (`Boundaries.geojson.gz` and `VATSpy.dat.gz`); without them it can't resolve any en-route callsign to a shape, so it shows none — the same behaviour it had before boundary data existed. This never affects anything else: terminal controllers, the map, your flights and your airline are all unaffected.

**Solution:** Reinstall FSOps, which restores the data files. If you're running from a build you made yourself, confirm both files are present in the `data/vatspy` folder next to the executable.

## My flight doesn't show as "flown online"

**Symptom:** You were connected to VATSIM for the whole flight, but the report card doesn't show the **"Flown online"** badge, or the flight doesn't appear in the Dashboard's flown-online history card.

**Cause:** A few ordinary reasons, all handled by quietly leaving the badge off rather than guessing:

- **No VATSIM CID set.** Go to Settings and enter your CID — see [Your VATSIM CID](user-guide.md#vatsim). With nothing set, FSOps never even asks the network about a flight.
- **The flight was too short for a single check.** FSOps checks the network at most every ~20 seconds while a flight is tracked, matched to your own configured CID; a flight completed before the first check ran simply has nothing to show either way.
- **You weren't online for enough of the flight.** FSOps corroborates your position against the network, not just your presence on it — briefly logging on and then disconnecting doesn't qualify. The badge (and the small bonus that comes with it) needs a meaningful share of the tracked flight to have matched, not just a moment of it.
- **The flight was completed manually** ("Complete with estimates"). There's no reliable telemetry to corroborate against on that path at all — see [Why did completing manually cost me reputation](#why-did-completing-manually-cost-me-reputation).
- **VATSIM's public feed was unreachable** for the whole flight — no internet, or the feed itself was down. FSOps never shows an error for this; the flight just completes as an ordinary offline sector.

**Solution:** Nothing to fix if any of the above applies — this is expected. If you're confident none of them do (a real CID set, a flight of normal length, genuinely connected throughout, completed normally), that's worth reporting with the flight's completion time so the corroboration attempts around it can be checked in the [log files](#where-to-find-log-files).

## The "Flown online" history card is empty, or says I haven't set a CID

**Symptom:** The Dashboard's **Flown online** card either asks you to set a VATSIM CID, or shows no flights even though you've flown several while connected.

**Cause:** This card is built entirely from FSOps' **own record** of flights it corroborated while they were tracked (see the section above) — it is never a pull from VATSIM's own history, which FSOps has no way to read (VATSIM doesn't publish a keyless public history of a member's past sessions; the only such endpoint requires an API key FSOps doesn't have). If it's asking for a CID, none is set in Settings yet. If a CID is set but the list is empty, none of your flights *since setting it* have been corroborated yet — a flight flown before you added your CID was never checked, and can't retroactively be.

**Solution:** Set your CID in Settings if you haven't, then fly a tracked sector while connected to VATSIM. It'll appear here once that flight completes.

## Other VATSIM traffic isn't showing on the map

**Symptom:** The Dashboard's live operations map shows your own aircraft and any online controllers, but no other VATSIM pilots nearby, even though you can see them in a VATSIM client.

**Solutions, in order:**

1. **Check the toggle.** The **"Show/Hide VATSIM traffic"** button above the map switches this layer on and off; it's on by default. If it reads "Show VATSIM traffic", select it.
2. **Check they're actually near your network.** This layer only shows traffic within about 150 nm of one of your own airports or your active routes' flight paths — it's deliberately not a whole-network traffic display, which would be far more than a dashboard map needs. Someone controlling or flying well away from your network genuinely won't appear.
3. **Check the feed is reachable.** If VATSIM's public feed can't be read at all, other traffic (and the ATC layer alongside it) both quietly show nothing rather than an error — see [No controllers are showing](#no-controllers-are-showing) for the same underlying feed and how to tell "nobody nearby" apart from "feed unreachable".
4. **On the in-game panel, this is off by design.** The panel has no map at all, so there's nothing to toggle there — this layer only ever appears on the Dashboard's map in a browser.

Other traffic is drawn deliberately small, faint, and without the accent colour your own aircraft or a controller uses — that's intentional, so it reads as background context rather than competing with your own flight for attention.

## The toolbar button isn't there

**Symptom:** No FSOps icon appears on MSFS 2024's own toolbar, even though you've been through the "Connect your MSFS panel" step.

**Cause:** Almost always one of four ordinary things, and Settings → MSFS panel will tell you which. The panel is an ordinary MSFS package: it has to be present in the folder the sim actually reads from, and the sim only looks at that folder while it's starting.

**Solutions, in order:**

1. **Check the status badge in Settings → MSFS panel first.** It reads **Installed**, **Not installed**, **Not set up**, **Update available**, **Needs repair**, or **Needs attention**, and everything below depends on which one you're looking at. If it says anything other than **Installed**, use the **Install panel** or **Reinstall / repair** button right there and skip the rest of this list.
2. **Restart MSFS.** This is the single most common cause. MSFS scans the Community folder once, at startup — a package added while the sim was already running is invisible to it until the next launch. Quit the simulator completely (not just back to the main menu) and start it again.
3. **Check the Community folder is the one your sim actually uses.** A machine with more than one MSFS install — Steam and Microsoft Store, or a moved install — has more than one Community folder, and a package in the wrong one is completely inert. Settings → MSFS panel lists the folders it found on this PC; pick the one belonging to the copy of MSFS you actually launch, save it, and reinstall the panel into it.
4. **Check the package isn't older than your sim expects.** The panel declares a `minimum_game_version` of **1.7.35**. An MSFS 2024 install older than that will ignore the package entirely and give no visible sign of having done so. Update the simulator.
5. **If Settings says the toolbar button won't appear, believe it.** A normal install ships the compiled component that registers the button, and Settings confirms it with **"Appears in the MSFS toolbar"**. If it says otherwise, that file is genuinely missing from what was installed — not a limitation of this build. **Reinstall / repair** puts it back; if it says the same thing again straight afterwards, that's worth reporting (see [How to report a problem](#how-to-report-a-problem)).
6. **As a fallback, the panel's view works in a browser.** The same compact view is always available at `http://localhost:5977/panel` in an ordinary browser tab, whether or not the toolbar button is working.

## The panel opens but shows nothing

**Symptom:** The FSOps toolbar button is there and opens a panel window in MSFS, but the window is blank, stuck loading, or says it can't reach FSOps — while FSOps itself is running fine in your browser.

**Cause:** **Port drift.** The panel is a static package: when it's installed, the address of your FSOps server is baked into it. If FSOps later starts on a *different* port — most often because 5977 was already taken and it fell back to another, or because you set `FSOPS_PORT` yourself — the installed panel carries on calling the old address, which nothing is listening on any more. This is a genuinely nasty symptom because the panel looks perfectly installed from every angle: the files are all there, the version is right, the button works. Nothing about a blank window points at a port.

FSOps detects this specific case and shows the panel's status badge as **Needs repair** in Settings → MSFS panel, with the mismatch named explicitly.

**Solution:** Open Settings → MSFS panel and select **Reinstall / repair**. That rewrites the package against the port FSOps is actually on. Then restart MSFS so it picks the updated package up. If you'd rather this never happen again, keep FSOps on a fixed port — if 5977 is regularly claimed by something else on your machine, it's worth finding out what (see [The UI won't load / port 5977 is already in use](#the-ui-wont-load--port-5977-is-already-in-use)) rather than letting the port move around underneath the panel.

## I moved my Community folder, or reinstalled MSFS

**Symptom:** You moved your MSFS install (a different drive, a switch between Steam and Microsoft Store), reinstalled the simulator, or deleted the `fsops-panel` folder by hand — and you're unsure whether the panel followed, or Settings now shows it as **Not installed**.

**Cause:** The panel lives inside your Community folder, so anything that changes or replaces that folder leaves it behind. FSOps checks what's actually on disk each time you open Settings rather than trusting what it installed previously, so the status badge reflects reality even when the change happened entirely outside FSOps.

**Solution:** Open Settings → MSFS panel and set the Community folder to the new location — FSOps lists the folders it can find on this PC, or you can browse for it. When you change the folder and a panel is installed at the old one, FSOps **asks whether to move it**: it can install into the new folder and optionally remove the old copy, or just update the recorded path and leave everything alone. If you'd rather do it in steps, save the new folder first and then use **Install panel** (or **Reinstall / repair** if a stale copy is already there). Restart MSFS afterwards so it rescans. An old `fsops-panel` folder left behind somewhere the sim no longer reads is harmless, but **Remove the panel** will clean it up properly if you point the folder back at it first.

## FSOps never tells me about updates

**Symptom:** Settings → Updates always says you're on the latest version, or shows nothing at all, even though you know a newer release exists.

**Cause:** The update check is deliberately built so that *every* way it can fail looks exactly like "you're up to date". Having no internet isn't an error worth interrupting anyone over, and a flight-simulator companion app has no business putting a red banner in front of you because a website was slow. So there's no error state to find — which does mean a genuine problem looks identical to good news. The usual reasons, in rough order of likelihood:

1. **Update checks are switched off.** Settings → Updates has an On/Off control. Off means no request leaves your machine at all, for any reason — it isn't "check quietly and hide the answer".
2. **The check simply hasn't run yet.** It runs at most once a day, lazily, and never during startup. If you've only just opened FSOps for the first time, the first check may not have completed. Select **Check now**.
3. **GitHub couldn't be reached.** No internet, a captive-portal wifi, a corporate proxy, DNS trouble, or GitHub's API rate-limiting your address (which it does per-IP for unauthenticated requests, and shared/office addresses hit it more easily). When this happens the line beside **Check now** reads "could not reach the releases page, so nothing has changed", and the check retries on its own after a few hours rather than hammering away at it.
4. **You dismissed that version.** Dismissing the notice silences that exact version permanently; a later release starts talking again. Settings → Updates always shows the update even after you've dismissed it — only the app-wide notice is hidden.
5. **The release is a pre-release or a draft.** Neither is ever offered as an update, whether it's flagged as such on GitHub or simply carries a tag like `v0.3.0-rc.1`. This is intentional.
6. **The release's tag isn't a version FSOps can compare.** A tag like `nightly` or `release-2026-08` isn't something a semantic comparison can rank against your build, so it's ignored rather than guessed at.

**Solution:** Turn checks on if they're off, select **Check now**, and read the line beside it. If it says the releases page couldn't be reached, that's a network condition, not a broken install — try again later. Either way you can always download a new version yourself from the project's releases page; the in-app check is a convenience, never the only route.

## A downloaded update was rejected, or disappeared

**Symptom:** You selected **Download and verify** and got a message saying the download didn't match the release's checksum and was deleted — or an installer you'd already downloaded vanished when you went back for it.

**Cause:** This is the safety check doing its job, and it's the most important thing this feature does. FSOps ships **unsigned** — there's no code-signing certificate — so the SHA-256 checksum published alongside each release is the only thing that distinguishes the installer the author actually built from whatever happened to arrive over your network. FSOps downloads that checksum first, downloads the installer to a temporary name, hashes the bytes that actually landed on disk, and only gives the file its real name if the two match exactly. Anything else — a corrupted or truncated download, a proxy that rewrote the file, a mirror serving something else — is deleted rather than handed to you.

The same check runs *again* when you select **Show the installer**, because "verified twenty minutes ago" isn't the same statement as "these bytes are correct now". If the file changed on disk in between, it's deleted then instead.

You'll also see the download refused outright, before anything is fetched, if a release publishes an installer with **no** `.sha256` file alongside it. There'd be nothing to verify it against, and an unverifiable unsigned installer is exactly what this is meant to prevent — so FSOps tells you the new version exists, links you to the release page, and declines to fetch it for you.

**Solution:**

1. Try the download once more. A single interrupted or corrupted transfer is by far the most common cause, and a retry usually just works.
2. If it fails again, download the installer yourself from the release page. Then check it by hand before running it — download the `.sha256` file next to it and compare:
   ```
   Get-FileHash .\FSOps-Setup-x.y.z.exe -Algorithm SHA256
   ```
   The value it prints should match the one in the `.sha256` file, ignoring case. **If it doesn't match, don't run it** — that file is not what the author published, and where you got it from is worth being suspicious of.
3. If the checksum keeps failing on a network you don't control (an office, a hotel, a school), that's worth knowing about in itself: something between you and GitHub is modifying downloads. Try a different network before assuming the release is at fault.

## Where a downloaded update goes, and why FSOps won't run it

A verified installer is written to:

```
%LOCALAPPDATA%\FSOps\updates\
```

Never beside the FSOps program files — those live under Program Files, which is read-only for a standard user. **Show the installer** opens that folder; it does not launch anything. Nothing in FSOps runs the installer, and that's a deliberate refusal rather than a missing feature: FSOps is unsigned, so an app that quietly downloaded and executed a program on your behalf would be building the exact problem the checksum exists to prevent — and it'd be doing it using trust you'd extended to FSOps, not to whatever it fetched. Deciding to run an unsigned installer is yours to make, in Explorer, where you can see what you're launching.

To install an update: select **Show the installer**, close FSOps, then run the installer yourself from the folder that opens. If you'd rather not keep the downloaded file, deleting it is always safe — FSOps simply offers the download again next time, and turning update checks off deletes it for you.

## Where the database lives

FSOps stores its SQLite database at:

```
%LOCALAPPDATA%\FSOps\fsops.db
```

Logs live alongside it in `%LOCALAPPDATA%\FSOps\logs\`. This is separate from the repository/install folder, so it survives rebuilding or reinstalling FSOps. **Deleting `fsops.db` resets FSOps completely** — your airline, fleet, routes, pilots, and financial history are all gone, and you'll see the setup wizard again next launch. See [Where your data lives](user-guide.md#where-your-data-lives) for how to back it up first.

## MSFS won't connect over SimConnect

**Symptom:** FSOps shows the simulator as disconnected — the "Sim offline" pill in the top bar, or a failing "Simulator connection" readiness check on the Fly screen — even though MSFS is open.

**Solutions, in order:**

1. **Make sure MSFS is actually in a flight.** SimConnect only exposes live aircraft data once you're loaded into a flight — being at the main menu, the world map, or a loading screen isn't enough. Load into any aircraft, on the ground or in the air, and check again.
2. **Give it a few seconds.** FSOps retries the SimConnect connection automatically roughly every 5 seconds for as long as it's running — you don't need to restart FSOps just because the first attempt didn't land right as MSFS finished loading.
3. **Check for other SimConnect clients.** Only one application can hold certain SimConnect connections cleanly at a time; if you have another SimConnect-based tool running (another tracker, a panel add-on, etc.) alongside FSOps, try closing it and see if FSOps connects.
4. **Check your firewall.** SimConnect communicates locally between MSFS and FSOps. If Windows Firewall or third-party security software is blocking that local traffic, allow both Microsoft Flight Simulator and FSOps through it.
5. **Restart both.** As a last resort, closing and reopening FSOps after MSFS has finished loading a flight, or a full restart of the simulator, clears up most remaining SimConnect connection issues.
6. **If you're building from source, make sure you're on a current build.** An earlier version of FSOps had a real bug where SimConnect data definitions were registered before the connection was actually established, which silently prevented a connection to a live MSFS 2024 session no matter how long you waited — none of the steps above would have fixed it. This has been fixed and verified against live MSFS 2024. If you're running an old build (from before this fix), pull the latest source, rebuild (`dotnet build`), and restart FSOps.

## Flight tracking stopped mid-flight

**Symptom:** FSOps was tracking your flight, then stopped updating — the map freezes, the live readouts stop moving, or a banner on the live flight view says tracking is paused.

**Cause:** Usually either MSFS or FSOps crashed or was closed, or the SimConnect link between them dropped.

**Recovery:**

1. Check whether MSFS is still running. If MSFS itself crashed, you'll need to relaunch the simulator and resume or restart your flight there first.
2. Check whether the FSOps backend (the terminal window) is still running. If it closed unexpectedly, restart it with `dotnet run --project src/FSOps.Server` and reload the browser.
3. Once both MSFS (in an active flight) and FSOps are running again, FSOps attempts to re-establish the SimConnect connection automatically — a banner on the live flight view says as much ("Tracking paused — waiting for the simulator to reconnect. Nothing is lost; this will pick back up automatically."), and no button-press is needed for a brief drop.
4. If the sim doesn't reconnect quickly (within about 30 seconds, close to where the flight last reported its position), FSOps stops guessing and marks the flight as needing your attention instead of silently resuming or losing it — see [A flight is stuck needing attention](#a-flight-is-stuck-needing-attention) below.
5. If you were partway through a tracked flight when the disconnect happened, check the [log files](#where-to-find-log-files) for what state the flight was left in before reporting it as a problem.

## A flight is stuck needing attention

**Symptom:** Opening the Fly screen shows a card titled **"This flight needs your attention"** instead of the normal live view or route picker.

**Cause:** FSOps couldn't automatically resume tracking an in-progress flight — almost always because FSOps or MSFS was restarted mid-flight and the simulator didn't reconnect close enough to the flight's last known position within about 30 seconds. Rather than silently guess at what happened next, or lose the flight's data, FSOps stops and asks you what to do. Nothing about the flight so far is lost either way — everything already tracked is kept in its stored event history.

**Solutions, pick one:**

1. **Check again** — if MSFS just needed a moment longer to load and reconnect, this re-checks without doing anything else. Try this first if you're about to keep flying the same flight.
2. **Complete with estimates** — closes the flight out now, using your planned block time and fuel figures rather than measured ones. Use this if you don't intend to keep flying it. No landing quality (touchdown rate, G-force, bounces, centreline) will be recorded for a flight completed this way, since none of that could actually be measured.
3. **Abandon** — discards the flight entirely, with no report card, and frees your aircraft up to fly something else. Use this if the flight is a lost cause (for example, MSFS crashed and you don't want to resume that exact flight).

## Why did completing manually cost me reputation

**Symptom:** After choosing **Complete with estimates** on a flight that needed attention (or otherwise manually completing a flight), your reputation score on the Dashboard dropped slightly, even though the flight itself paid out normally.

**Cause:** This is expected, and it's deliberate. A manually-completed flight has no reliable telemetry — that's the entire reason the option exists — so FSOps has no honest way to judge whether it was actually on time or how it landed. Rather than guess (an earlier internal version tried scoring it from the wall-clock gap between starting and completing, which backfired badly — a flight completed moments after starting read as an enormous *early* arrival), FSOps applies a small, fixed penalty for the sector being **unverified**. It's deliberately smaller than the worst a properly-tracked sector could cost you, so flying a sector out for real is never the worse choice — but it's also never zero, so ending tracking early is never a free way to escape a flight that's going badly. See [Your airline's reputation](user-guide.md#your-airlines-reputation) in the user guide for the full picture, including that **abandoning** a flight costs more still (as much as a cancellation), on top of losing the ticket revenue and fuel already spent.

**Solution:** Nothing to fix — this is working as intended. If you'd rather avoid the penalty, let a flight resolve normally (or via SimConnect reconnecting) rather than completing it manually; reserve manual completion for when a flight genuinely can't continue.

## A route doesn't show as flyable

**Symptom:** A route you expect to be able to fly shows up under "Not flyable right now" on the Fly screen, with a reason like "No aircraft at {ICAO} — your fleet is currently at {other ICAO}."

**Cause:** FSOps only lets you fly a route whose departure airport matches where one of your fleet aircraft is actually recorded as being (see [Round trips and where your aircraft actually is](user-guide.md#round-trips-and-where-your-aircraft-actually-is)), **and** that aircraft must be reserved to you (see [An aircraft can't be flown because it isn't reserved](#an-aircraft-cant-be-flown-because-it-isnt-reserved) above). A completed flight moves its aircraft to wherever it actually landed, so this is expected the first time you look at a fresh airline (your only aircraft is still sitting at your home base, so only routes leaving from there show as flyable) or any time your aircraft is genuinely elsewhere — mid-flight, in maintenance, sitting at an airport you haven't flown a route back from yet, or released to virtual pilots rather than reserved to you.

**Solution:** Fly a route departing from wherever your aircraft actually is. If you expect a route to be flyable because you believe your aircraft already landed at its departure airport, but it still isn't showing up, that points to something worth reporting rather than normal behaviour — check the [log files](#where-to-find-log-files) for what actually happened at the end of that earlier flight (for example, whether it completed normally or was abandoned, since an abandoned flight leaves the aircraft where it was rather than moving it).

## Where to find log files

FSOps writes log output to `%LOCALAPPDATA%\FSOps\logs\` — the same data directory the database lives in (see [Where the database lives](#where-the-database-lives)), not a folder relative to wherever you happen to run FSOps from. Each run writes to a dated log file there — check the most recent one for errors around the time your issue occurred.

## How to report a problem

When reporting an issue, please include:

- What you were doing when the problem happened (as specific as possible — e.g. "mid-descent on a tracked flight" rather than "flying").
- What you expected to happen vs what actually happened.
- The relevant log file(s) from the `logs/` folder covering that time.
- Your FSOps version, MSFS version, and Windows version.
- Whether the problem is reproducible, and if so, the steps to reproduce it.

The more specific the report, the faster the underlying cause can be tracked down.
