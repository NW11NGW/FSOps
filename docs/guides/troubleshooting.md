# Troubleshooting

Problems and solutions for running FSOps. If you don't find your issue here, see [how to report a problem](#how-to-report-a-problem) at the bottom.

## Table of contents

- [The UI won't load / port 5977 is already in use](#the-ui-wont-load--port-5977-is-already-in-use)
- [The UI shows "FSOps UI not built yet"](#the-ui-shows-fsops-ui-not-built-yet)
- [The map shows no background tiles](#the-map-shows-no-background-tiles)
- [The setup wizard keeps reappearing](#the-setup-wizard-keeps-reappearing)
- [Route creation is refused](#route-creation-is-refused)
- [My currency looks wrong](#my-currency-looks-wrong)
- [Where the database lives](#where-the-database-lives)
- [MSFS won't connect over SimConnect](#msfs-wont-connect-over-simconnect)
- [Flight tracking stopped mid-flight](#flight-tracking-stopped-mid-flight)
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

**Symptom:** The route-planning map is otherwise usable — route lines, the great-circle path, and airport markers all show up — but the background map tiles never load and you're left looking at a blank/grey canvas.

**Cause:** The map's background tiles are fetched over the internet. Everything else on the map (route geometry, airport positions) comes from your local database and works fully offline.

**Solution:** Check your internet connection. There's nothing to configure — once connectivity is back, reload the page and tiles will load normally. If you're intentionally offline, you can still plan and create routes; you just won't see the background imagery.

## The setup wizard keeps reappearing

**Symptom:** FSOps opens into the full-screen airline setup wizard every time you start it, even though you thought you'd already founded an airline.

**Cause:** FSOps decides whether to show the wizard purely by asking the backend whether an airline currently exists for you (`GET /api/v1/airline`). The wizard appears whenever that comes back empty — either no airline has been created yet, or one was deleted (deliberately, via the settings [danger zone](user-guide.md#settings), or by having the database wiped — see [Where the database lives](#where-the-database-lives) below).

**Solution:** If you meant to keep your airline, check whether the database file still exists and hasn't been reset. If it's genuinely gone, there's no way to recover it short of a backup (see [Where your data lives](user-guide.md#where-your-data-lives)) — otherwise, just go through the wizard again.

## Route creation is refused

**Symptom:** The plan panel shows a red "This route can't be created yet" message and the **Create route** button stays disabled.

**Cause:** This happens for one of two reasons: departure and arrival are the same airport, or the route is beyond your aircraft's **practical operating range** — roughly **0.85×** its published range once fuel reserves are accounted for, not the raw catalogue figure. A route just over the raw range but under the 0.85× cutoff will still be refused.

**Solution:** Pick a different airport pair, or add an aircraft with more range to your fleet (once fleet management is available — see the [user guide](user-guide.md#buying-vs-leasing-aircraft)). Amber advisory warnings (short runway, strategy mismatch) look similar but don't block creation — only the red message does.

## My currency looks wrong

**Symptom:** Fares, balances, or prices look off after changing currency in settings, or don't match what you expected.

**Cause:** FSOps stores every amount internally in a single base currency unit and only converts it for display using your selected currency's fixed rate (see [Settings — Currency](user-guide.md#currency) and [Architecture](../architecture.md#money-is-stored-in-a-single-base-unit)). Rates are fixed at build time, not live exchange rates, so they won't match real-world rates exactly — and changing currency never changes your actual stored balance, only how it's displayed.

**Solution:** If a number looks wrong, double check which currency is currently selected in settings. If it still looks wrong after that, it's worth reporting (see [How to report a problem](#how-to-report-a-problem)) — but a mismatch with real-world exchange rates is expected behaviour, not a bug.

## Where the database lives

FSOps stores its SQLite database at:

```
%LOCALAPPDATA%\FSOps\fsops.db
```

Logs live alongside it in `%LOCALAPPDATA%\FSOps\logs\`. This is separate from the repository/install folder, so it survives rebuilding or reinstalling FSOps. **Deleting `fsops.db` resets FSOps completely** — your airline, fleet, routes, pilots, and financial history are all gone, and you'll see the setup wizard again next launch. See [Where your data lives](user-guide.md#where-your-data-lives) for how to back it up first.

## MSFS won't connect over SimConnect

**Symptom:** FSOps shows the simulator as disconnected even though MSFS is open.

**Solutions, in order:**

1. **Make sure MSFS is actually in a flight.** SimConnect only exposes live aircraft data once you're loaded into a flight — being at the main menu, the world map, or a loading screen isn't enough. Load into any aircraft, on the ground or in the air, and check again.
2. **Check for other SimConnect clients.** Only one application can hold certain SimConnect connections cleanly at a time; if you have another SimConnect-based tool running (another tracker, a panel add-on, etc.) alongside FSOps, try closing it and see if FSOps connects.
3. **Restart FSOps.** If MSFS was still loading when FSOps started, or the connection attempt happened at the wrong moment, closing and reopening FSOps after MSFS has finished loading a flight often resolves it.
4. **Check your firewall.** SimConnect communicates locally between MSFS and FSOps. If Windows Firewall or third-party security software is blocking that local traffic, allow both Microsoft Flight Simulator and FSOps through it.
5. **Restart MSFS.** As a last resort, a full restart of the simulator clears up most SimConnect connection issues.

## Flight tracking stopped mid-flight

**Symptom:** FSOps was tracking your flight, then stopped updating — the map freezes, or the connection indicator drops.

**Cause:** Usually either MSFS or FSOps crashed or was closed, or the SimConnect link between them dropped.

**Recovery:**

1. Check whether MSFS is still running. If MSFS itself crashed, you'll need to relaunch the simulator and resume or restart your flight there first.
2. Check whether the FSOps backend (the terminal window) is still running. If it closed unexpectedly, restart it with `dotnet run --project src/FSOps.Server` and reload the browser.
3. Once both MSFS (in an active flight) and FSOps are running again, FSOps will attempt to re-establish the SimConnect connection automatically.
4. If you were partway through a tracked flight when the disconnect happened, check the [log files](#where-to-find-log-files) for what state the flight was left in before reporting it as a problem.

## Where to find log files

FSOps writes log output to a `logs/` folder inside the application's working directory (the folder you ran `dotnet run --project src/FSOps.Server` from, typically the repository root). Each run writes to a log file there — check the most recent one for errors around the time your issue occurred.

## How to report a problem

When reporting an issue, please include:

- What you were doing when the problem happened (as specific as possible — e.g. "mid-descent on a tracked flight" rather than "flying").
- What you expected to happen vs what actually happened.
- The relevant log file(s) from the `logs/` folder covering that time.
- Your FSOps version, MSFS version, and Windows version.
- Whether the problem is reproducible, and if so, the steps to reproduce it.

The more specific the report, the faster the underlying cause can be tracked down.
