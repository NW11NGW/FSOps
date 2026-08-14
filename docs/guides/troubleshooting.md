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
- [The "Move" button on the Fleet page is greyed out](#the-move-button-on-the-fleet-page-is-greyed-out)
- [The reposition picker doesn't list the airport I want](#the-reposition-picker-doesnt-list-the-airport-i-want)
- [Why is my pilot worse than they were](#why-is-my-pilot-worse-than-they-were)
- [I can't release a pilot](#i-cant-release-a-pilot)
- [SimBrief import did nothing](#simbrief-import-did-nothing)
- [No controllers are showing](#no-controllers-are-showing)
- [A controller is online but never appears anywhere on the map](#a-controller-is-online-but-never-appears-anywhere-on-the-map)
- [En-route sectors never appear, only airport circles](#en-route-sectors-never-appear-only-airport-circles)
- [My flight doesn't show as "flown online"](#my-flight-doesnt-show-as-flown-online)
- [The "Flown online" history card is empty, or says I haven't set a CID](#the-flown-online-history-card-is-empty-or-says-i-havent-set-a-cid)
- [Other VATSIM traffic isn't showing on the map](#other-vatsim-traffic-isnt-showing-on-the-map)
- [FSOps never tells me about updates](#fsops-never-tells-me-about-updates)
- [FSOps says I'm "ahead of the stable channel"](#fsops-says-im-ahead-of-the-stable-channel)
- [A downloaded update was rejected, or disappeared](#a-downloaded-update-was-rejected-or-disappeared)
- [Where a downloaded update goes, and why FSOps won't run it](#where-a-downloaded-update-goes-and-why-fsops-wont-run-it)
- [Where the database lives](#where-the-database-lives)
- [MSFS won't connect over SimConnect](#msfs-wont-connect-over-simconnect)
- [Flight tracking stopped mid-flight](#flight-tracking-stopped-mid-flight)
- [A flight is stuck needing attention](#a-flight-is-stuck-needing-attention)
- [A landing shows as "not measured"](#a-landing-shows-as-not-measured)
- [A sector wasn't valid for payment (slew or a position jump)](#a-sector-wasnt-valid-for-payment-slew-or-a-position-jump)
- [Why did completing manually cost me reputation](#why-did-completing-manually-cost-me-reputation)
- [A route doesn't show as flyable](#a-route-doesnt-show-as-flyable)
- [A saved schedule says its aircraft isn't where the pattern starts](#a-saved-schedule-says-its-aircraft-isnt-where-the-pattern-starts)
- ["FSOps couldn't fit a legal starter schedule together"](#fsops-couldnt-fit-a-legal-starter-schedule-together)
- [Where to find log files](#where-to-find-log-files)
- [How to report a problem](#how-to-report-a-problem)

## The UI won't load / port 5977 is already in use

**Symptom:** Browsing to `http://localhost:5977` shows nothing, a connection-refused error, or the terminal running FSOps reports the address is already in use.

**First, which way did you launch it?** The two behave differently on a busy port, and only one of them is a problem:

- **The installed app** (the FSOps window from your Start menu) doesn't mind. If 5977 is taken by something that isn't FSOps, it steps up to the next free port and tells you in its title bar; if 5977 is taken by a copy of FSOps that's already running, it attaches to that one rather than starting a second server. Either way there's nothing to fix — but it does mean the app may not be at `http://localhost:5977`, so check the window title before assuming it failed.
- **Running the server directly** (`dotnet run --project src/FSOps.Server`, the build-from-source path) has no such fallback: it binds 5977 or fails. That's the case the steps below are for. You can point it elsewhere by setting `FSOPS_PORT` rather than freeing the port.

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

**If you've already built the frontend and still see this (or the browser shows an old version of a change you just made), the backend needs rebuilding too.** `FSOps.Server` serves `wwwroot` from beside its own built assembly, not straight out of `src/FSOps.Server/wwwroot` — that folder only gets copied into place when the backend itself is built. Running `npm run build` again updates the source folder but not the copy the running server is actually reading from. `dotnet run --project src/FSOps.Server` rebuilds first and picks up the change; a plain `dotnet build` from the repository root works too.

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

**Cause:** Exactly three things block a route, and the red message says which one you've hit:

- **Departure and arrival are the same airport.**
- **Nothing in your entire fleet has the range** for the sector. Range is measured as **practical** operating range — roughly **0.85×** the published figure once fuel reserves are accounted for — so a sector just inside the catalogue number can still be out of reach.
- **Nothing in your entire fleet can physically use one of the two runways** — too short for anything you own, or (for a heavy aircraft) an unpaved surface no length of which will do.

Neither range nor runway is a blocker on its own, though, and it's worth knowing the three outcomes apart. They work identically:

| What you have | What happens |
| --- | --- |
| A reserved aircraft that can do it | Nothing — the route is created normally. |
| Nothing reserved can, but something in the fleet can | **Not a refusal.** You get an amber advisory pointing you at reserving that aircraft, and the route is still created. |
| Nothing in the fleet can do it at all | The red message, and the route is genuinely blocked. |

**Solution:** If you're being pointed at reserving an aircraft, reserve one that can fly the sector — the route itself is fine. If the route is genuinely blocked, pick a different airport pair or add a suitable aircraft from the Fleet page (see [Range](user-guide.md#range), [Runway suitability](user-guide.md#runway-suitability) and [Buying, leasing and financing aircraft](user-guide.md#buying-leasing-and-financing-aircraft)). Amber advisory messages — a strategy mismatch, or "reserve this one instead" — look similar but never block creation; only the red banner does.

## My currency looks wrong

**Symptom:** Fares, balances, or prices look off after changing currency in settings, or don't match what you expected.

**Cause:** FSOps stores every amount internally in a single base currency unit and only converts it for display using your selected currency's fixed rate (see [Settings → Display](user-guide.md#display) and [Architecture](../architecture.md#money-is-stored-in-a-single-base-unit)). Rates are fixed at build time, not live exchange rates, so they won't match real-world rates exactly — and changing currency never changes your actual stored balance, only how it's displayed.

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

**Cause:** A scheduled occurrence only flies if the aircraft assigned to it is actually available and at the right airport when its departure time arrives — see [The wall-clock economy](user-guide.md#the-wall-clock-economy-flying-while-youre-away). If it's mid-flight, or sitting at a different airport (most often because an earlier leg in the chain didn't land where the schedule expected), FSOps records the occurrence rather than silently dropping it or teleporting the aircraft, and what happens next depends on your playstyle: **Casual** records it as **Skipped** with no charge; **True-life** records it as **Cancelled** with a real cancellation fee, since a badly-planned schedule should genuinely cost something under that playstyle. If the reason is specifically that the aircraft is **in maintenance**, it's recorded as **Suspended** instead, under either playstyle — no cancellation fee, since the aircraft needing a check isn't a mistake in the schedule, and the occurrence resumes on its own the next time it's due once the aircraft is released.

**Solution:** Check the flight record for the specific reason (it names the aircraft and where it actually is, or which check it's waiting on). For a **Skipped** or **Cancelled** occurrence, this usually means the pilot's weekly schedule has a gap — a leg that assumes the aircraft is somewhere it won't actually be that day, most often because a repositioning leg is missing from an earlier day. Adjust the schedule so each aircraft's chain of legs is geographically continuous. For a **Suspended** occurrence, there's nothing to fix — wait for the aircraft to come out of maintenance, or bring the check forward yourself with "Perform maintenance now" on the Fleet page if you'd rather control when the downtime lands.

## A virtual pilot's aircraft isn't where I expected it

**Symptom:** The Fleet page shows a virtual pilot's aircraft at a different airport than you expected, or a route you thought was flyable for that aircraft shows as not flyable.

**Cause:** A completed virtual-pilot flight moves its aircraft's recorded location to wherever it actually landed, exactly the same rule as a player flight — see [Round trips and where your aircraft actually is](user-guide.md#round-trips-and-where-your-aircraft-actually-is). If a pilot's schedule doesn't bring an aircraft back to where the next day's chain expects it to start, that next occurrence won't be flyable — see [above](#a-virtual-pilots-flight-was-skipped-cancelled-or-suspended-instead-of-flown).

**Solution:** Check the aircraft's current location on the Fleet page against what the pilot's schedule assumes for each day, and adjust the schedule so a day's chain always starts from wherever the aircraft's previous chain actually left it.

## The "Move" button on the Fleet page is greyed out

**Symptom:** An aircraft is clearly in the wrong place, but its **Move** button on the Fleet page won't click.

**Cause:** One of three things, and hovering the button says which:

- **It's available to virtual pilots.** Repositioning is a player-only action — an aircraft released to virtual pilots is theirs to fly, so it can't be moved out from under them.
- **It's in flight.** The sector decides where it ends up.
- **It's grounded for a maintenance check.** An aircraft that can't fly can't be moved either.

**Solution:** For the first, press **Reserve for you** on that aircraft's row and the Move button becomes available immediately. For the second, finish or abandon the flight — wherever it lands becomes its new location anyway, which may be all you needed. For the third, wait for the check to finish; the Fleet page shows the date it comes back.

## The reposition picker doesn't list the airport I want

**Symptom:** You open **Move** and the airport you had in mind isn't among the choices, or there are no choices at all.

**Cause:** Destinations are restricted to airports your airline has an **active route** to or from — not every airport in the world. Both directions count, so an outstation you only ever fly *into* is still offered. Two things narrow the list further: the airport the aircraft is already at is never listed (there is nothing to move), and a route you've **deactivated** stops offering its airports.

**Solution:** Create a route touching the airport you want (or reactivate the one you deactivated), then re-open the dialog. If your airline has no routes at all, FSOps says so plainly rather than showing an empty picker.

## Why is my pilot worse than they were

**Symptom:** A virtual pilot's skill rating on the Pilots page has gone down since you last checked, rather than up — or a pilot you haven't looked at in a while shows a lower skill rating than a newly-hired one that's since flown a handful of sectors.

**Cause:** This is expected, not a bug — **skill decays when a pilot goes unflown for too long.** A pilot's skill normally climbs with hours flown, but if a virtual pilot has no standing schedule assigning them any legs, they don't earn hours, and after a two-week grace period with nothing flown, their rating starts eroding gradually back down toward where they started (50). The line under their skill rating on the Pilots page explains it directly — a countdown once idle time is closing in on the grace period, or, once decay has actually started, both what their hours alone earned and what they're actually sitting at now. See [Skill, landing quality, and idle decay](user-guide.md#skill-landing-quality-and-idle-decay) in the user guide for the full mechanics.

The most common cause is a pilot who was hired but never given a schedule, or one whose schedule was cleared (for example, because their aircraft was reserved back to you or sold) and never rebuilt. **Your own skill rating never decays**, regardless of how long you go without flying — it's purely a record of your hours, never used to judge your own flights, so there's nothing to protect it from.

**Solution:** Give the pilot a standing weekly schedule (or restore the one they had) — see [Building a weekly schedule](user-guide.md#building-a-weekly-schedule). A pilot flying that schedule keeps flying on the real-world clock even while FSOps is closed, which is what keeps their skill from decaying at all. There's no way to instantly restore lost skill short of flying them again; it recovers the same way it was earned, gradually with hours flown.

## I can't release a pilot

**Symptom:** Selecting **Release** for a pilot on the Pilots page fails, or the release action isn't offered.

**Cause:** There are two, and the message tells you which one you've hit:

- **"The player pilot cannot be released."** — you're trying to release **yourself**. Your own entry on the Pilots page is your airline's founding pilot rather than a hire, so there's no releasing it; an airline always has you. If what you actually want is to be rid of the airline entirely, that's [Settings → Data → start over](user-guide.md#data), not this.
- **"… is in the air right now. Wait for the flight to finish before releasing them."** — that virtual pilot has a sector in progress. Releasing them out from under an in-progress flight would leave that flight with no pilot to resolve against, so it's refused outright rather than half-applied.

The **Status** column tells you which of the two you're about to hit before you try: a pilot with a sector in the air reads **Flying**, and that is the same fact the release check itself reads, so the column and the refusal can never disagree. To see where that flight actually is, the Dashboard's [live operations map](user-guide.md#the-live-operations-map) draws every airborne sector, yours and your virtual pilots'.

**Solution:** For the first, nothing to do — you can't release yourself, by design. For the second, wait for that flight to resolve; a virtual pilot's flights complete on their own against the wall clock (see [The wall-clock economy](user-guide.md#the-wall-clock-economy-flying-while-youre-away)), so this is usually a short wait rather than something you have to act on. Then release them.

**Before you do release anybody, note what goes with them:** releasing a pilot deletes their whole weekly schedule too, and that can't be undone. If you're only trying to free up an aircraft or change what they fly, edit their schedule instead — releasing and re-hiring means rebuilding the week from an empty calendar.

## SimBrief import did nothing

**Symptom:** The flight brief's SimBrief OFP panel reads "Using the built-in plan" instead of pulling in your OFP, even though you've set a Pilot ID and generated a plan.

**Cause:** One of several ordinary reasons, all handled by falling back to FSOps' own plan rather than failing the flight: no Pilot ID set yet in [Settings → SimBrief](user-guide.md#simbrief), an incorrect Pilot ID, SimBrief has no plan on file for that Pilot ID (SimBrief itself can't distinguish "wrong ID" from "no plan filed" — FSOps can't tell them apart either), SimBrief was unreachable or timed out, or — the single most common cause in practice — **your latest OFP is filed for a different city pair than the route you're about to fly.** FSOps refuses to substitute a mismatched plan rather than silently applying the wrong fuel and altitude figures; see [Importing your OFP back](user-guide.md#importing-your-ofp-back).

**Solution:** Read the panel's own message — it names the specific reason. Most often, this means filing a fresh OFP in SimBrief for the exact route (same origin and destination) you're about to fly, then clicking **Check for OFP** on the flight brief's SimBrief OFP panel — you don't need to leave the Fly screen and come back; the button re-checks immediately. If you only just added your Pilot ID, double-check it in Settings, or use the link in the panel if it hasn't picked one up yet. Either way, this never blocks flying — the built-in plan is used automatically and the flight brief still shows complete, usable figures.

## No controllers are showing

**Symptom:** The Dashboard shows no controllers — either there's no **ATC coverage** card at all, or the controller list reads "No controllers online in this part of the map," even though you know someone is controlling on VATSIM.

**First, check the layer is switched on — it starts off.** VATSIM ATC is **hidden by default**, and until you turn it on FSOps doesn't request it at all. If you can't see an **ATC coverage** card beneath the map, that's why: the card and the map's controller shapes are one feature and appear together. The button above the map reads **"Show VATSIM ATC"** while it's off; select it and it becomes "Hide VATSIM ATC", the card returns, and FSOps remembers the choice so you only do this once. This is by far the most likely answer if you've never seen controllers at all.

**If it's on and the list is still empty:** the list follows the map. It shows the controllers whose coverage is **currently on screen**, so that the list and the map can never disagree while you're looking at both — if you've panned to somewhere quiet, or zoomed in past the sector you were expecting, the list empties out even though plenty of people are online elsewhere.

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

- **No VATSIM CID set.** Go to Settings and enter your CID — see [Your VATSIM CID](user-guide.md#your-vatsim-cid). With nothing set, FSOps never even asks the network about a flight.
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

**Symptom:** The Dashboard's live operations map shows your own aircraft, but no other VATSIM pilots nearby, even though you can see them in a VATSIM client.

**Solutions, in order:**

1. **Switch the layer on — it starts off.** Other pilots' traffic is **hidden by default**, and until you turn it on FSOps doesn't request it at all, so this is by far the most likely answer. The button above the map reads **"Show VATSIM traffic"** while it's off; select it and it becomes "Hide VATSIM traffic". FSOps remembers the choice, so you only do this once. Note that this affects *other people's* aircraft only — your own tracked flight and your virtual pilots are shown regardless of it. It's also **independent of the "Show VATSIM ATC" button next to it**: turning traffic on does not turn controllers on, and vice versa, so check the one you actually want.
2. **Check they're actually near your network.** This layer only shows traffic within about 150 nm of one of your own airports or your active routes' flight paths — it's deliberately not a whole-network traffic display, which would be far more than a dashboard map needs. Someone controlling or flying well away from your network genuinely won't appear.
3. **Check the feed is reachable.** If VATSIM's public feed can't be read at all, other traffic (and the ATC layer, when you have it switched on) both quietly show nothing rather than an error — see [No controllers are showing](#no-controllers-are-showing) for the same underlying feed and how to tell "nobody nearby" apart from "feed unreachable".
Other traffic is drawn deliberately small, faint, and without the accent colour your own aircraft or a controller uses — that's intentional, so it reads as background context rather than competing with your own flight for attention.

## FSOps never tells me about updates

**Symptom:** Settings → Updates always says you're on the latest version, or shows nothing at all, even though you know a newer release exists.

**Cause:** The update check is deliberately built so that *every* way it can fail looks exactly like "you're up to date". Having no internet isn't an error worth interrupting anyone over, and a flight-simulator companion app has no business putting a red banner in front of you because a website was slow. So there's no error state to find — which does mean a genuine problem looks identical to good news. The usual reasons, in rough order of likelihood:

1. **Update checks are switched off.** Settings → Updates has an On/Off control. Off means no request leaves your machine at all, for any reason — it isn't "check quietly and hide the answer".
2. **The check simply hasn't run yet.** It runs at most once a day, lazily, and never during startup. If you've only just opened FSOps for the first time, the first check may not have completed. Select **Check now**.
3. **GitHub couldn't be reached.** No internet, a captive-portal wifi, a corporate proxy, DNS trouble, or GitHub's API rate-limiting your address (which it does per-IP for unauthenticated requests, and shared/office addresses hit it more easily). When this happens the line beside **Check now** reads "could not reach the releases page, so nothing has changed", and the check retries on its own after a few hours rather than hammering away at it.
4. **You dismissed that version.** Dismissing the notice silences that exact version permanently; a later release starts talking again. Settings → Updates always shows the update even after you've dismissed it — only the app-wide notice is hidden.
5. **The release is a draft, or a pre-release and you're on the Stable channel.** A draft is never offered on any channel. A pre-release is never offered on Stable — whether it's flagged as one on GitHub or simply carries a tag like `v0.3.0-rc.1` — and that's the point of the channel rather than a fault. Settings → Updates → **Which builds to offer** decides this.
6. **The release's tag isn't a version FSOps can compare.** A tag like `nightly` or `release-2026-08` isn't something a semantic comparison can rank against your build, so it's ignored rather than guessed at.
7. **You're ahead of the channel.** If you've been running development builds and switched back to Stable, you're running something *newer* than the newest stable release. FSOps says so explicitly — "You are ahead of the stable channel" — rather than claiming you're up to date, and it won't offer you anything until stable overtakes your build. See the next section.

**Solution:** Turn checks on if they're off, select **Check now**, and read the line beside it. If it says the releases page couldn't be reached, that's a network condition, not a broken install — try again later. Either way you can always download a new version yourself from the project's releases page; the in-app check is a convenience, never the only route.

## FSOps says I'm "ahead of the stable channel"

**Symptom:** Settings → Updates says you are ahead of the channel and names a version older than the one you're running. Nothing is offered, and **Check now** doesn't change that.

**Cause:** You're running a development build — a pre-release — and the channel you're on doesn't have anything newer. Most often this is because you tried a development build and then switched back to **Stable**. It is not a fault, and nothing is stuck.

FSOps will only ever offer a build that is **strictly newer** than the one you're running, on either channel. The alternative would be offering you the older stable release as though it were an upgrade, which would quietly overwrite a newer build with an older one — so instead it tells you plainly where you stand.

**Solution:** Usually, nothing. When a stable release passes the build you're on, it will be offered normally and the message clears itself.

If you'd rather not wait:

- **Go back to Development** if you want to keep receiving test builds — Settings → Updates → **Which builds to offer**.
- **Install a stable build yourself** from the releases page if you want off development builds now. Read the warning about saved data in [the user guide](user-guide.md#which-builds-to-offer) first: a development build may have changed your database in ways an earlier version doesn't understand, so copy `%LOCALAPPDATA%\FSOps` before installing an older version.

## A downloaded update was rejected, or disappeared

**Symptom:** You selected **Download and verify** and got a message saying the download didn't match the release's checksum and was deleted — or an installer you'd already downloaded vanished when you went back for it.

**Cause:** This is the safety check doing its job, and it's the most important thing this feature does. FSOps ships **unsigned** — there's no code-signing certificate — so the SHA-256 checksum published alongside each release is the only thing that distinguishes the installer the author actually built from whatever happened to arrive over your network. FSOps downloads that checksum first, downloads the installer to a temporary name, hashes the bytes that actually landed on disk, and only gives the file its real name if the two match exactly. Anything else — a corrupted or truncated download, a proxy that rewrote the file, a mirror serving something else — is deleted rather than handed to you.

The same check runs *again* when you select **Show the installer**, because "verified twenty minutes ago" isn't the same statement as "these bytes are correct now". If the file changed on disk in between, it's deleted then instead.

This is identical on both release channels. A development build is verified exactly as strictly as a stable one — being a pre-release is a reason to be more careful about what you run, not less.

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

## A landing shows as "not measured"

**Symptom:** The report card's landing quality section reads "Touchdown was recorded, but the sim never reported a rate for it — landing rate not measured" instead of showing a touchdown rate, even though you clearly landed.

**Cause:** Not a bug, and not the same thing as a missing touchdown. MSFS publishes its own touchdown-rate simvar a moment *after* the wheels actually touch, not at the instant of contact — reading it at that exact instant sees it still sitting at its idle zero, which used to show a real landing (even a hard one) as a perfect 0 fpm. FSOps now watches the few seconds after contact for that simvar to actually report something, and falls back to the vertical speed from the moment just before touchdown if it never does. **"Not measured" only appears when neither of those produced a usable figure** — genuinely rare, and specifically not the same as landing softly. A touchdown with no rate at all is still scored on peak G-force, bounces and centreline deviation, whichever of those were captured.

**Solution:** Nothing to fix. If this happens on every single landing rather than occasionally, that's worth reporting (see [How to report a problem](#how-to-report-a-problem)) with the aircraft you were flying, since some third-party aircraft may not populate the underlying simvar reliably. A flight completed with **"Complete with estimates"** always shows no touchdown at all rather than "not measured" — that's the other, unrelated case where nothing about the landing could be captured in the first place; see [Ending a flight](user-guide.md#ending-a-flight-and-what-happens-if-it-gets-interrupted).

## A sector wasn't valid for payment (slew or a position jump)

**Symptom:** The report card shows a **Flight integrity** card saying slew was active, or that telemetry showed a position change inconsistent with normal flight, and that "this sector isn't valid for payment" — no ticket revenue was posted, even though the flight felt normal to fly.

**Cause:** FSOps' integrity monitor genuinely detected either slew mode being active at some point during the flight, or a position change between two telemetry samples that no real aircraft could have made — both mean part of the recorded flight wasn't actually flown, so the ledger-posting code never reaches the step that would add ticket revenue for it. This is a structural gate, not a deduction, and it's deliberately not an accusation: an elevated simulation rate (speeding up cruise) is scored completely differently and never voids a sector on its own — see [Flight integrity](user-guide.md#flight-integrity).

A position jump specifically has to be **corroborated** before it's flagged, and several separate checks now have to agree before a sector is refused. Each of these came from a case where a flight that was genuinely flown could have lost its pay:

- **One bad reading is never enough.** FSOps ignores an implausible reading unless the aircraft was already tracked through a spell of plausible flight beforehand, and then only confirms the jump once the aircraft has gone on flying plausibly afterwards too. A reading that comes back close to where it appeared to leave from is dismissed as bad data.
- **If the aircraft comes home, the finding is withdrawn.** A jump that has already been flagged is un-flagged if the aircraft turns out to be back where that jump started from — including when the correction arrives minutes later. An aircraft that ends up where it began has flown nothing and gained nothing, so a sim that reports a wrong position for a while and then puts itself right costs you nothing, however long it takes to right itself. This applies to each jump separately, and coming home is the only thing that withdraws one: a reposition you fly on from, or park at the far end of, stays flagged. Slew is not affected by this at all — any slewing invalidates the sector however the flight ends.
- **The first position is checked against where the flight should be starting**, and discarded rather than believed if it's wildly off.
- **A short jump is never a teleport.** A discontinuity that moves the aircraft less than about five miles in total is ignored however impossible the speed looks, because nobody gains anything by teleporting five miles — and a scenery load or the aircraft settling onto the ground can easily throw a position out by a mile or two.
- **A correction back to your own departure airport is treated as a correction, not a cheat** — as long as you haven't taken off or flown away from it yet. Nobody teleports *to* the stand they're already parked on. Once you're airborne this no longer applies, so it can't be used to jump home later in a flight.
- **A break in the connection is not measured as flying.** If SimConnect drops out mid-flight and comes back, FSOps discards what it knew about your position rather than comparing across the gap. It can't tell how fast you flew during a period it wasn't watching, and the sim often reports a stale position for a moment after reconnecting — which used to read as the aircraft having crossed the intervening distance instantly. Anything already detected before the drop-out still stands.
- **Time acceleration is accounted for**, including the single moment you change rate, which used to be miscounted for fast aircraft jumping straight to a high multiplier.

This means an occasional bad reading from the simulator — most commonly right as it connects, or just after it reconnects — no longer voids an otherwise clean sector. If you're seeing this on a flight you're confident had no slewing or repositioning in it, that's worth reporting with the approximate time it happened, since a corroborated jump found on a flight that genuinely had none would be a real defect, not a false alarm.

**Slew is the exception to all of that leniency, and it is absolute.** Everything above describes how a *position jump* is now forgiven, because a jump is usually the simulator misreporting rather than anything you did. Slew is different: it is something only you can turn on, and it moves an aircraft without flying it. So it voids the sector the moment it is seen, with no grace period, no allowance for still being on the stand, and no way to undo it by carrying on — including the common case of using it to unstick an aircraft from scenery *after* pressing **Start flight**. Unstick it first, then start the flight, and nothing is lost. The rule is deliberately without exceptions, because a rule with exceptions is one somebody eventually finds a way through.

**Solution:** Nothing to fix if you did genuinely slew or reposition mid-flight — that's working as intended. Whatever fuel the aircraft actually burned is still billed regardless (see [Fuel billing](user-guide.md#fuel-billing)); only the ticket revenue is withheld. If you're confident nothing like that happened, check the [log files](#where-to-find-log-files) around the flagged time and report it.

## Why did completing manually cost me reputation

**Symptom:** After choosing **Complete with estimates** on a flight that needed attention (or otherwise manually completing a flight), your reputation score on the Dashboard dropped slightly, even though the flight itself paid out normally.

**Cause:** This is expected, and it's deliberate. A manually-completed flight has no reliable telemetry — that's the entire reason the option exists — so FSOps has no honest way to judge whether it was actually on time or how it landed. Rather than guess (an earlier internal version tried scoring it from the wall-clock gap between starting and completing, which backfired badly — a flight completed moments after starting read as an enormous *early* arrival), FSOps applies a small, fixed penalty for the sector being **unverified**. It's deliberately smaller than the worst a properly-tracked sector could cost you, so flying a sector out for real is never the worse choice — but it's also never zero, so ending tracking early is never a free way to escape a flight that's going badly. See [Your airline's reputation](user-guide.md#your-airlines-reputation) in the user guide for the full picture, including that **abandoning** a flight costs more still (as much as a cancellation), on top of losing the ticket revenue and fuel already spent.

**Solution:** Nothing to fix — this is working as intended. If you'd rather avoid the penalty, let a flight resolve normally (or via SimConnect reconnecting) rather than completing it manually; reserve manual completion for when a flight genuinely can't continue.

## A route doesn't show as flyable

**Symptom:** A route you expect to be able to fly shows up under "Not flyable right now" on the Fly screen, with a reason like "No aircraft at {ICAO} — your fleet is currently at {other ICAO}."

**Cause:** FSOps only lets you fly a route whose departure airport matches where one of your fleet aircraft is actually recorded as being (see [Round trips and where your aircraft actually is](user-guide.md#round-trips-and-where-your-aircraft-actually-is)), **and** that aircraft must be reserved to you (see [An aircraft can't be flown because it isn't reserved](#an-aircraft-cant-be-flown-because-it-isnt-reserved) above). A completed flight moves its aircraft to wherever it actually landed, so this is expected the first time you look at a fresh airline (your only aircraft is still sitting at your home base, so only routes leaving from there show as flyable) or any time your aircraft is genuinely elsewhere — mid-flight, in maintenance, sitting at an airport you haven't flown a route back from yet, or released to virtual pilots rather than reserved to you.

**Solution:** Fly a route departing from wherever your aircraft actually is. If you expect a route to be flyable because you believe your aircraft already landed at its departure airport, but it still isn't showing up, that points to something worth reporting rather than normal behaviour — check the [log files](#where-to-find-log-files) for what actually happened at the end of that earlier flight (for example, whether it completed normally or was abandoned, since an abandoned flight leaves the aircraft where it was rather than moving it).

## A saved schedule says its aircraft isn't where the pattern starts

**Symptom:** Saving a virtual pilot's schedule works, but a notice appears afterwards saying the aircraft isn't standing where the week begins. There are two versions of it, and **which one you get is the answer to your question**:

- *"G-NZHG is at EGPH, and this pattern starts from EGGD — but EGPH is on the pattern too, so nothing is stuck. The schedule is saved and keeps repeating, and G-NZHG picks it up when the leg departing EGPH next comes round…"*
- *"G-NZHG is at LFPG, and no leg in this weekly pattern departs from there. The schedule is saved and keeps repeating, but nothing in it can move the aircraft…"*

**Cause:** Entirely normal, and not an error. A weekly schedule is a pattern that repeats forever, but the aircraft flying it moves around — so a week that begins at your home base is often saved while the airframe is away at the far end of a sector. FSOps mentions it once because of what happens in the meantime: any occurrence the aircraft can't reach is [skipped or cancelled](user-guide.md#the-wall-clock-economy-flying-while-youre-away), and under True-life a cancellation carries a real fee — so it's worth knowing rather than discovering from your ledger.

The distinction the two messages draw is the one that matters. A pattern is a **closed loop**, so if the aircraft is parked at *any* airport the week departs from, that airport's own leg simply flies, moves the aircraft on, and everything after it lines up by itself — the aircraft never has to return to the airport the week happens to begin at. It's only genuinely stuck when it's somewhere **no leg departs from**, because then nothing in the schedule can ever move it.

**Solution:** For the first message, nothing at all — the pattern repairs itself, and the only cost is the legs due before that airport's own leg comes round. For the second, the aircraft needs a hand: either add a leg departing from where it actually is, or reserve it and use **Reposition** on the Fleet page to move it back for the standard fee (see [Moving an aircraft to another airport](user-guide.md#moving-an-aircraft-to-another-airport)) — flying it back yourself costs nothing and earns a sector, which is why both are offered. If the pattern was never meant to start from that airport at all, edit the schedule so its first leg departs from where the aircraft really is.

If a schedule that's already running gets into the second state, the **Pilots** page flags it there too — deliberately with the same facts and the same two ways out, so the two screens can never tell you different things about the same aircraft.

## "FSOps couldn't fit a legal starter schedule together"

**Symptom:** **Suggest a starter schedule** on an empty week reports that no legal starter schedule could be built, even though you have aircraft and routes.

**Cause:** This message now means every eligible aircraft was tried, on every day of the week, and none of them could legally fly anything. The usual reasons are real ones: every unreserved aircraft is already carrying another pilot's week at those times; the only available airframe is parked at an airport none of your routes departs from and has no route back; or your routes are all beyond that aircraft's range.

**Solution:** Open a day manually and pick the aircraft yourself — the **"Why can't I fly the others?"** list under the leg picker names the exact reason for each route, which is the fastest way to see which of the above it is. Hiring a second pilot doesn't help if the *aircraft* is the constraint; freeing one up (releasing a reservation on the Fleet page, or thinning another pilot's week) usually does. Note that a partial week is a success, not a failure — if only some days fit, you'll be offered those days rather than this message.

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
