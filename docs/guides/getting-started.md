# Getting Started

This guide walks through installing the prerequisites, building FSOps, and running it for the first time.

## Table of contents

- [Before you start](#before-you-start)
- [1. Install the prerequisites](#1-install-the-prerequisites)
- [2. Get the code](#2-get-the-code)
- [3. Build the backend](#3-build-the-backend)
- [4. Build the frontend](#4-build-the-frontend)
- [5. Run FSOps](#5-run-fsops)
- [6. Connect to MSFS](#6-connect-to-msfs)
- [What's not available yet](#whats-not-available-yet)
- [Next steps](#next-steps)

## Before you start

FSOps is early in development. This guide covers building and running the current application shell — enough to confirm the backend and UI are talking to each other. It is **not yet** a guide to flying tracked flights or running an airline; those features are still being built.

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

You should see the FSOps app shell load, with a live connection indicator confirming the browser and backend are talking over SignalR. Leave the terminal window open — closing it stops the server.

## 6. Connect to MSFS

FSOps talks to Microsoft Flight Simulator through SimConnect, Microsoft's official interface for external apps to read and write simulator state. To connect:

1. Start Microsoft Flight Simulator 2024.
2. Load into a flight (SimConnect data isn't available while you're sitting at the main menu — you need to be in an aircraft, on the ground or in the air).
3. With FSOps already running (or started afterwards), it will attempt to establish a SimConnect connection automatically.

If FSOps can't reach the simulator, see [troubleshooting](troubleshooting.md#msfs-wont-connect-over-simconnect).

## What's not available yet

The current build proves out the plumbing — server, UI, and live connection. The following are still being built and are **not** available yet:

- Creating an airline (name, home base, strategy)
- Building a route network
- Planning and flying tracked flights
- Landing quality scoring and post-flight report cards
- The economy simulation (ticket pricing, fuel, maintenance, loans, leases)
- Hiring and assigning virtual pilots
- The in-game MSFS panel
- Statistics dashboards

See the [User Guide](user-guide.md) for a fuller description of each of these and how they're intended to work once built.

## Next steps

- Read the [User Guide](user-guide.md) to understand what FSOps will do as features land.
- Read [Architecture](../architecture.md) if you're interested in how the app is put together under the hood.
- Hit a snag? Check [Troubleshooting](troubleshooting.md).
