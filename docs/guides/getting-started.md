# Getting Started

This guide walks through installing the prerequisites, building FSOps from source, and running it for the first time.

**If you just want to use FSOps, you don't need any of this.** Download the installer from the [Releases page](https://github.com/NW11NGW/FSOps/releases) and follow the [Installation guide](installation.md) instead. The installer carries its own copy of the .NET runtime and the built UI, so there is nothing to install beforehand and nothing to compile — and it doesn't need administrator rights. This guide is for people who want to change FSOps, contribute to it, or run a build that hasn't been released yet.

## Table of contents

- [Before you start](#before-you-start)
- [1. Install the prerequisites](#1-install-the-prerequisites)
- [2. Get the code](#2-get-the-code)
- [3. Build the backend](#3-build-the-backend)
- [4. Build the frontend](#4-build-the-frontend)
- [5. Run FSOps](#5-run-fsops)
- [6. Found your airline](#6-found-your-airline)
- [7. Connect to MSFS](#7-connect-to-msfs)
- [Where things stand](#where-things-stand)
- [Next steps](#next-steps)

## Before you start

FSOps is early in development. This guide covers building FSOps from source and running it, founding your airline through the setup wizard, and connecting to the simulator. For the step-by-step of actually flying a tracked flight once you're connected, see the [User Guide](user-guide.md#planning-and-flying-a-tracked-flight).

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

This builds all five .NET projects — `FSOps.Core`, `FSOps.Data`, `FSOps.Sim`, `FSOps.Server`, and the `FSOps.Desktop` window shell — plus the test projects. The first run will take longer while NuGet packages are restored. If you're contributing rather than just running FSOps, `dotnet test` from the repository root runs the full backend xUnit suite.

## 4. Build the frontend

The web UI lives in `src/fsops-web` and is built separately with npm. From the repository root:

```
cd src/fsops-web
npm install
npm run build
```

`npm install` pulls down the frontend's dependencies (only needed once, or again after they change). `npm run build` compiles the React app into static files under `src/FSOps.Server/wwwroot`. If you skip this step entirely, the server will still start, but the browser will show a message that the UI hasn't been built yet — see [troubleshooting](troubleshooting.md#the-ui-shows-fsops-ui-not-built-yet) if you hit that.

**`npm run build` on its own does not update the UI you see, and this costs real time if you don't know it.** `FSOps.Server` doesn't serve straight out of `src/FSOps.Server/wwwroot` — it serves from a `wwwroot` folder beside the *built* server assembly (its `.csproj` copies `wwwroot`'s contents there, `PreserveNewest`, as part of a `dotnet build`). That copy only happens when the backend itself is built. So after changing frontend code and running `npm run build` again, the browser keeps showing the **previous** build (or, on a brand-new checkout that's never been built at all, "UI not built yet") until you also run `dotnet build` — or just restart with `dotnet run --project src/FSOps.Server`, which builds first anyway — to pick the freshly-built files up. If you've built the frontend and the browser genuinely doesn't reflect it, rebuild the backend before looking anywhere else.

If you're contributing to the frontend, `npm test` (watch mode) or `npm run test:run` (single pass) runs its [Vitest](https://vitest.dev/) suite from the same `src/fsops-web` folder — see [Architecture](../architecture.md#testing) for what it covers. Neither is required just to build and run FSOps.

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

### Running the desktop shell instead

`dotnet run --project src/FSOps.Server` runs the server alone, which is usually what you want while developing — you get the logs in your terminal and your browser reloads on demand. Installed users don't launch it that way, though. They launch `FSOps.Desktop`, a thin window that starts the server as a child process and renders the UI inside itself:

```
dotnet run --project src/FSOps.Desktop
```

It's worth running at least once before you change anything near startup, because it's the only way to exercise the paths a real user hits — how the shell finds the server sitting next to it, how it picks a port, and how it behaves when the Edge WebView2 runtime is missing (it falls back to opening the UI in your default browser). Set `FSOPS_USE_BROWSER=1` to force that fallback on a machine that does have WebView2, which is the only practical way to test it.

### Producing a packaged build

To build the thing a user actually downloads — a self-contained folder that runs on a PC with no .NET installed:

```
.\scripts\publish.ps1
```

That builds the web UI, publishes both .NET applications into `artifacts\publish`, and then asserts that every asset the app resolves at runtime is present, so a half-populated output fails on your machine rather than on someone else's. To wrap that folder in the installer as well, see [installer/README](../../installer/README.md) — that step additionally needs [Inno Setup](https://jrsoftware.org/isdl.php) 6.3 or newer.

## 6. Found your airline

FSOps opens straight into a full-screen setup wizard whenever no airline exists yet for your machine — on first run, and again any time you delete your airline from the settings danger zone (see the [user guide](user-guide.md#settings)). The wizard has eight steps:

1. **Welcome** — a short introduction to the wizard.
2. **Identity** — your airline's name (2-40 characters), a 2-3 letter ICAO code (e.g. `FSO`), and your own name as the founding pilot — it appears on every flight you fly and throughout the Pilots page. Leave it blank for a default; you can change it later from Settings.
3. **Home base** — search for and pick the airport your airline will be based at. It needs scheduled service or a runway of at least 5,000 ft.
4. **Playstyle** — choose Casual or True-life. This sets your starting capital and every fixed cost — starter lease rate, deposit term, insurance, maintenance downtime, and loan rate ceiling — see the [user guide](user-guide.md#playstyles) for what each one means. Unlike strategy, **this cannot be changed later** — switching means deleting the airline and starting a new one.
5. **Strategy** — choose International, Domestic, Low-cost, Premium, or Balanced (a neutral all-rounder). This shapes suggested fares, how sensitive demand is to your pricing, and which route-length advisories you'll see — see the [user guide](user-guide.md#creating-your-airline) for what each one means. It isn't a one-time choice: you can pick a different strategy at any time from Settings → Airline.
6. **Aircraft** — pick an accent colour (a preset swatch or a custom hex value) used throughout the UI, and a starter aircraft family: Airbus A320 or Boeing 737-800.
7. **Currency** — your display currency and your preferred distance, altitude, and weight units, time display, and clock format.
8. **Review** — optionally add a startup loan (off by default — you have to switch it on and enter an amount and term yourself). The annual rate is shown, not chosen — it's always your playstyle's capped rate, since a brand-new airline has no trading history to price a better one from — with a live monthly payment estimate. The amount itself is capped too: **£250,000** for Casual, **£5,000,000** for True-life; asking for more is refused with an explanation rather than silently trimmed. Review everything you've chosen, and select **Found your airline** to create it.

Creating your airline also leases your starter aircraft (a deposit posted to your ledger — one month under Casual, two under True-life), hires you as your first pilot, and records your starting capital (and any loan proceeds) there too — you'll land in the main app with a fleet of one and cash in the bank. From then on, that lease payment (plus insurance and salaries) posts again automatically every 30 days — see [The monthly billing cycle](user-guide.md#the-monthly-billing-cycle).

## 7. Connect to MSFS

FSOps talks to Microsoft Flight Simulator through SimConnect, Microsoft's official interface for external apps to read and write simulator state. To connect:

1. Start Microsoft Flight Simulator 2024.
2. Load into a flight (SimConnect data isn't available while you're sitting at the main menu — you need to be in an aircraft, on the ground or in the air).
3. With FSOps already running (or started afterwards), it will attempt to establish a SimConnect connection automatically, and keeps retrying every few seconds on its own if the first attempt doesn't land — you don't need to restart FSOps just because MSFS wasn't ready yet.

You can see the connection state at a glance from two indicator pills in the top-right of FSOps' top bar, next to your cash balance: one shows whether FSOps' own live-update connection to its backend is up, the other shows whether the simulator itself is connected ("Sim connected" in green once MSFS is reachable, "Sim offline" otherwise). You can also check `GET /api/v1/sim/status` directly, or watch it via the readiness checks on the Fly screen once you're ready to fly (see the [User Guide](user-guide.md#planning-and-flying-a-tracked-flight)).

If FSOps can't reach the simulator, see [troubleshooting](troubleshooting.md#msfs-wont-connect-over-simconnect).

## Where things stand

Founding an airline, planning a route network, flying a fully tracked flight with a post-flight report card, running a fleet (buying, leasing, selling, used aircraft, loans, maintenance), the monthly billing cycle that keeps charging you whether or not you fly, hiring virtual pilots to fly a standing weekly schedule on the real-world clock, a Finances page and a Statistics page for inspecting all of it, importing your SimBrief flight plan, seeing online VATSIM controllers on your live map, and an in-game MSFS panel, all work today.

Two gaps that earlier versions of this guide listed have since closed:

- **FSOps ships as a packaged installer.** Building from source, which is what this guide covers, is no longer the only way to run it — see the [Installation guide](installation.md).
- **The in-game panel's toolbar registration file is now compiled and ships with the app.** Earlier builds installed the panel's files into your Community folder but had no compiled `.spb`, so no MSFS toolbar button could appear. That file is now built and included in every release. If the button still doesn't show up for you, see [the toolbar button isn't there](troubleshooting.md#the-toolbar-button-isnt-there).

See the [User Guide](user-guide.md) for a fuller description of everything above and how it behaves.

## Next steps

- Read the [User Guide](user-guide.md) to understand what FSOps will do as features land.
- Read [Architecture](../architecture.md) if you're interested in how the app is put together under the hood.
- Hit a snag? Check [Troubleshooting](troubleshooting.md).
