# Getting Started

This guide walks through installing the prerequisites, building FSOps, and running it for the first time.

## Table of contents

- [Before you start](#before-you-start)
- [1. Install the prerequisites](#1-install-the-prerequisites)
- [2. Get the code](#2-get-the-code)
- [3. Build the backend](#3-build-the-backend)
- [4. Build the frontend](#4-build-the-frontend)
- [5. Run FSOps](#5-run-fsops)
- [6. Found your airline](#6-found-your-airline)
- [7. Connect to MSFS](#7-connect-to-msfs)
- [What's not available yet](#whats-not-available-yet)
- [Next steps](#next-steps)

## Before you start

FSOps is early in development. This guide covers building and running FSOps, founding your airline through the setup wizard, and connecting to the simulator. For the step-by-step of actually flying a tracked flight once you're connected, see the [User Guide](user-guide.md#planning-and-flying-a-tracked-flight).

## 1. Install the prerequisites

You'll need the following installed before building FSOps:

1. **Windows** — FSOps is a Windows desktop application. It relies on SimConnect, which is Windows-only.
2. **Microsoft Flight Simulator 2024** — installed and able to run. You don't need MSFS running to build FSOps, but you'll need it for the sim-connected features later.
3. **.NET 8 SDK** — download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0). The SDK includes the runtime, so you don't need to install that separately. If you only ever plan to run pre-built releases in future, the runtime alone will suffice — but building from source needs the full SDK.
4. **Node.js** (LTS release) — required to build the web frontend. Download from [nodejs.org](https://nodejs.org/). This installs `npm` alongside it, which FSOps' frontend build uses.

You can check what you have installed from a terminal:

```
dotnet --version
node --version
npm --version
```

## 2. Get the code

Clone or download the FSOps repository to a folder on your machine, then open a terminal in the repository's root folder (the one containing `FSOps.sln`).

## 3. Build the backend

From the repository root, restore and build the .NET solution:

```
dotnet build
```

This builds all four backend projects — `FSOps.Core`, `FSOps.Data`, `FSOps.Sim`, and `FSOps.Server` — plus the test project. The first run will take longer while NuGet packages are restored.

## 4. Build the frontend

The web UI lives in `src/fsops-web` and is built separately with npm. From the repository root:

```
cd src/fsops-web
npm install
npm run build
```

`npm install` pulls down the frontend's dependencies (only needed once, or again after they change). `npm run build` compiles the React app into static files that `FSOps.Server` serves. If you skip this step, the server will still start, but the browser will show a message that the UI hasn't been built yet — see [troubleshooting](troubleshooting.md#the-ui-shows-fsops-ui-not-built-yet) if you hit that.

Return to the repository root once the build finishes:

```
cd ../..
```

## 5. Run FSOps

Start the backend server:

```
dotnet run --project src/FSOps.Server
```

Once it's running, open your browser and go to:

```
http://localhost:5977
```

The first time FSOps runs, it needs to import world airport and runway data into its local database — around 78,000 airports and their runways, sourced from a bundled dataset rather than downloaded, so it doesn't need internet access. This kicks off in the background as soon as the server starts and takes roughly half a minute; it doesn't hold up the airline setup wizard described below, which opens straight away, but airport search (including the wizard's home-base step) will only return complete results once the import has actually finished. If you land on the main Dashboard before it's done, a banner near the top shows its progress percentage until it completes; from then on it never runs again. Leave the terminal window open — closing it stops the server.

## 6. Found your airline

FSOps opens straight into a full-screen setup wizard whenever no airline exists yet for your machine — on first run, and again any time you delete your airline from the settings danger zone (see the [user guide](user-guide.md#settings)). The wizard has seven steps:

1. **Welcome** — a short introduction to the wizard.
2. **Identity** — your airline's name (2-40 characters) and a 2-3 letter ICAO code (e.g. `FSO`).
3. **Home base** — search for and pick the airport your airline will be based at. It needs scheduled service or a runway of at least 5,000 ft.
4. **Strategy** — choose International, Domestic, Low-cost, or Premium. This shapes suggested fares and, later, demand modelling — see the [user guide](user-guide.md#creating-your-airline) for what each one means.
5. **Aircraft** — pick an accent colour (a preset swatch or a custom hex value) used throughout the UI, and a starter aircraft family: Airbus A320 or Boeing 737-800.
6. **Currency** — your display currency and your preferred distance, altitude, and weight units, time display, and clock format.
7. **Review** — optionally add a startup loan (amount, term, and annual rate, with a live monthly payment estimate), review everything you've chosen, and select **Found your airline** to create it.

Creating your airline also buys your starter aircraft, hires you as your first pilot, and records your starting capital (and any loan proceeds) in your airline's financial ledger — you'll land in the main app with a fleet of one and cash in the bank.

## 7. Connect to MSFS

FSOps talks to Microsoft Flight Simulator through SimConnect, Microsoft's official interface for external apps to read and write simulator state. To connect:

1. Start Microsoft Flight Simulator 2024.
2. Load into a flight (SimConnect data isn't available while you're sitting at the main menu — you need to be in an aircraft, on the ground or in the air).
3. With FSOps already running (or started afterwards), it will attempt to establish a SimConnect connection automatically, and keeps retrying every few seconds on its own if the first attempt doesn't land — you don't need to restart FSOps just because MSFS wasn't ready yet.

You can see the connection state at a glance from two indicator pills in the top-right of FSOps' top bar, next to your cash balance: one shows whether FSOps' own live-update connection to its backend is up, the other shows whether the simulator itself is connected ("Sim connected" in green once MSFS is reachable, "Sim offline" otherwise). You can also check `GET /api/v1/sim/status` directly, or watch it via the readiness checks on the Fly screen once you're ready to fly (see the [User Guide](user-guide.md#planning-and-flying-a-tracked-flight)).

If FSOps can't reach the simulator, see [troubleshooting](troubleshooting.md#msfs-wont-connect-over-simconnect).

## What's not available yet

Founding an airline, planning a route network, and flying a fully tracked flight with a post-flight report card all work today (see the [User Guide](user-guide.md)). The following are still being built and are **not** available yet:

- The economy simulation (ticket pricing, fuel and fee costs, maintenance spend, loan/lease payments) — flights currently record zero revenue and cost, deliberately, until this lands
- Hiring and assigning virtual pilots
- Buying or leasing additional aircraft, and aircraft maintenance
- The in-game MSFS panel
- Statistics dashboards
- A packaged installer — for now, FSOps is built and run from source (this guide)

See the [User Guide](user-guide.md) for a fuller description of each of these and how they're intended to work once built.

## Next steps

- Read the [User Guide](user-guide.md) to understand what FSOps will do as features land.
- Read [Architecture](../architecture.md) if you're interested in how the app is put together under the hood.
- Hit a snag? Check [Troubleshooting](troubleshooting.md).
