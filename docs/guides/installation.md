# Installing FSOps

This guide covers installing FSOps from its packaged installer, what happens the first time you run it, connecting it to Microsoft Flight Simulator, and updating or removing it later.

If you'd rather build FSOps from source — because you want to change it, or you're contributing — see [Getting Started](getting-started.md) instead. You don't need to install anything from this page to do that.

## Table of contents

- [Before you start](#before-you-start)
- [1. Download the installer](#1-download-the-installer)
- [2. Run the installer](#2-run-the-installer)
- [3. First launch](#3-first-launch)
- [4. Connect MSFS](#4-connect-msfs)
- [Where your data lives](#where-your-data-lives)
- [Running FSOps in your browser instead](#running-fsops-in-your-browser-instead)
- [Updating FSOps](#updating-fsops)
- [Uninstalling FSOps](#uninstalling-fsops)
- [If something goes wrong](#if-something-goes-wrong)

## Before you start

You need:

- **64-bit Windows 10 (version 1809 or newer) or Windows 11.** FSOps is x64-only because SimConnect's native library only ships as x64. It installs and runs on ARM64 Windows under x64 emulation, though MSFS itself is the limiting factor there.
- **About 500 MB of free disk space** for the installed application, plus room for your database, which starts small and grows slowly with your flight history.
- **Microsoft Flight Simulator 2024**, if you want the sim-connected features. FSOps installs and runs perfectly well without MSFS present — you just can't fly a tracked flight until it's there.

You do **not** need:

- **.NET.** The installer carries its own copy of the .NET 8 runtime, so FSOps runs on a machine with no .NET installed at all.
- **Node.js, or any development tools.** Those are only for building from source.
- **Administrator rights.** See the next section for why.
- **An internet connection**, except to download the installer itself. FSOps' world data — roughly 44,000 airports and about 74,000 runways — is bundled, not downloaded. The app's own outbound calls are all optional and all opt-in: the update check, SimBrief's OFP fetch if you set a Pilot ID, VATSIM's public feed for the map's ATC layer, and the map's background tiles. Everything else works with no connection at all.

## 1. Download the installer

From the [Releases page](https://github.com/NW11NGW/FSOps/releases), download two files from the newest release:

- `FSOps-Setup-<version>.exe` — the installer.
- `FSOps-Setup-<version>.exe.sha256` — a small text file containing the installer's checksum.

**FSOps is not code-signed.** There is no code-signing certificate behind this project, and buying one is not currently on the cards. That has a real consequence you should know about rather than discover: Windows cannot tell you who built the file, so the checksum is the only thing that distinguishes the installer the author built from a file that has been tampered with somewhere between the release page and your disk. Checking it takes one command in PowerShell, run from wherever you saved the download:

```
Get-FileHash .\FSOps-Setup-<version>.exe -Algorithm SHA256
```

Compare the hash it prints against the contents of the `.sha256` file. They should match, ignoring case. If they don't, delete the download and fetch it again — and if it still doesn't match, don't run it.

This is the same check FSOps' own update checker performs automatically on anything it downloads for you later, and for the same reason.

## 2. Run the installer

Double-click `FSOps-Setup-<version>.exe`.

### The SmartScreen warning

Because the installer is unsigned, Windows will very likely show a blue **"Windows protected your PC"** dialog. This is SmartScreen reporting that it doesn't recognise the publisher — not that it has found anything wrong with the file.

Select **More info**, then **Run anyway**.

If you're not comfortable doing that, verifying the checksum first (above) is the meaningful safety step, and it's a better one than any warning dialog. This message will keep appearing for every release until the project has a signing certificate.

### What the installer does

The install is **per-user** and never asks for administrator rights. FSOps installs into your own profile, at:

```
%LOCALAPPDATA%\Programs\FSOps
```

That's a deliberate choice rather than a limitation. FSOps has no service, no driver, and nothing shared between accounts — its server binds to localhost only, so it doesn't even need a firewall rule — and it already keeps all of its data in your user profile. Installing per-user means no elevation prompt at all, which is a better trade than asking an unsigned installer for administrator rights over your whole machine.

You'll be asked whether you want a desktop shortcut. A Start menu entry is always created.

### The WebView2 prompt

On most machines you'll never see this. FSOps draws its interface in a window powered by the Microsoft Edge WebView2 runtime, which ships as part of Windows 11 and arrives on Windows 10 through Edge updates — so it's almost always already there.

If it genuinely isn't — Windows LTSC and some tightly managed corporate images are the usual cases — the installer offers to download it from Microsoft (about 2 MB). You can decline. **FSOps works either way**: when the runtime is missing, the app opens in your default browser instead, and every feature behaves identically. Declining, being offline, or having the download fail will never stop FSOps installing.

## 3. First launch

Start FSOps from the Start menu or your desktop shortcut. A window opens showing the airline setup wizard.

**The first run imports world data**, and this takes roughly 35 seconds. FSOps is loading roughly 44,000 airports and about 74,000 runways into its local database from a bundled dataset — nothing is downloaded. This happens in the background and does not hold up the setup wizard, which opens straight away.

The one thing it does affect is airport search. Until the import finishes, searching for airports — including the wizard's home-base step — returns incomplete results. If you reach the main Dashboard before it's done, a banner near the top shows its progress. It never runs again after that first time.

From there, [Getting Started](getting-started.md#6-found-your-airline) walks through founding your airline, and the [User Guide](user-guide.md) covers everything the app does.

### What's actually running

FSOps is two processes: a window, and a local web server it starts and stops for you. The server listens on **http://localhost:5977** by default. If something else already holds that port, FSOps quietly moves to the next free one rather than refusing to start.

You can close the window normally; it shuts the server down with it. If you already have FSOps running and launch it again, the new window attaches to the server that's already there instead of starting a second one — there's only one database per user, and pointing two servers at it is a good way to corrupt a ledger.

## 4. Connect MSFS

**SimConnect** needs nothing installed or configured. Start MSFS 2024, load into a flight, and FSOps connects on its own, retrying every few seconds until it succeeds. Two indicator pills in the top-right of FSOps' top bar show the state. See [Connect to MSFS](getting-started.md#7-connect-to-msfs) for detail.

If it doesn't connect, see [MSFS won't connect over SimConnect](troubleshooting.md#msfs-wont-connect-over-simconnect).

## Where your data lives

Everything FSOps writes lives in one folder, in your user profile, separate from the installed program:

```
%LOCALAPPDATA%\FSOps
```

Paste that into the File Explorer address bar to open it. Inside:

- **`fsops.db`** — your airline. Fleet, routes, pilots, flight history, ledger, settings: all of it.
- **`logs\`** — application logs, which are what to attach if you report a problem.

Nothing is ever written next to the installed program, which is why FSOps doesn't need administrator rights and why **uninstalling it doesn't touch your airline**. It also means backing up your save is a matter of copying `fsops.db` somewhere safe. Do that before trying anything drastic; it's the only copy.

See [Where your data lives](user-guide.md#where-your-data-lives) in the User Guide for more, and [where the database lives](troubleshooting.md#where-the-database-lives) if you need to move it.

## Running FSOps in your browser instead

Some people would simply rather use their own browser — for its zoom, extensions, or window management. Set the environment variable `FSOPS_USE_BROWSER` to `1` and FSOps skips its own window and opens in your default browser instead:

```
setx FSOPS_USE_BROWSER 1
```

That sets it permanently for your account; open a new session for it to take effect, and `setx FSOPS_USE_BROWSER ""` undoes it. Nothing else changes — it's the same app, the same server, the same data. This is also the path FSOps takes automatically when the WebView2 runtime isn't available.

## Updating FSOps

FSOps checks for new releases once a day and shows a notice in the app when one exists. It never installs anything on its own.

When you ask it to, it downloads the new installer and verifies it against the checksum published alongside the release. If the hash doesn't match, the file is deleted and never named to you. If a release ships an installer with no checksum, FSOps tells you a new version exists and links you to the release page, but won't offer the download — there'd be nothing to verify it against.

Once a download is verified, FSOps opens the containing folder in Explorer so you can run the installer yourself. It deliberately will not launch it for you: an app that silently runs downloaded executables is precisely the thing the checksum exists to prevent, and the final decision to run an installer belongs to you, where you can see what you're starting.

To update, **run the new installer over the top of your existing install.** It recognises the previous version, replaces it in place, and leaves `%LOCALAPPDATA%\FSOps` — your airline — completely alone. There's no need to uninstall first. Close FSOps before you start; if you forget, the installer notices the files are in use and offers to close it for you.

You can also just download any newer release manually from the [Releases page](https://github.com/NW11NGW/FSOps/releases) and run it. The result is identical.

### Which builds you're offered

Settings → Updates → **Which builds to offer** chooses between two channels. **Stable** is the default and offers finished, released versions only — if you've never touched this setting, that's what you have.

**Development** also offers test builds as they're made. They arrive earlier and they are not tested to release standard: expect bugs, expect some to affect your saved airline, and expect a development build to be able to change your database in ways an older version won't understand. Take a copy of `%LOCALAPPDATA%\FSOps` before you switch if that matters to you. Verification is unchanged either way — a development build is checked against its published checksum exactly as strictly as a stable one.

Switching back to Stable is always allowed, but you'll then be running something newer than the newest stable release, so FSOps will say you're **ahead of the stable channel** and offer nothing until stable catches up. It won't downgrade you. See [which builds to offer](user-guide.md#which-builds-to-offer).

See [FSOps never tells me about updates](troubleshooting.md#fsops-never-tells-me-about-updates), [FSOps says I'm ahead of the stable channel](troubleshooting.md#fsops-says-im-ahead-of-the-stable-channel), and [where a downloaded update goes](troubleshooting.md#where-a-downloaded-update-goes-and-why-fsops-wont-run-it) if the update flow misbehaves.

## Uninstalling FSOps

Uninstall FSOps the normal way — **Settings → Apps → Installed apps → FSOps → Uninstall**, or the Start menu entry.

**Your airline is not deleted.** The uninstaller removes the program and nothing else; `%LOCALAPPDATA%\FSOps` and the database inside it stay exactly where they are. Install FSOps again later, whether it's the same version or a newer one, and your airline is waiting as you left it.

Because some people do want a genuinely clean removal, the uninstaller asks — once, at the end — whether you'd also like to delete that data folder. It names the folder, spells out that it holds your airline, fleet, routes, pilots, flight history and ledger, and **defaults to keeping it**. Answering No, or dismissing it, keeps everything. There is no other circumstance in which uninstalling removes your data.

If you'd rather not be asked at all, or you want to be certain, copy `fsops.db` somewhere else first.

## If something goes wrong

- The app won't start, or the window is blank — see [Troubleshooting](troubleshooting.md).
- Port 5977 is taken — see [the UI won't load](troubleshooting.md#the-ui-wont-load--port-5977-is-already-in-use).
- MSFS won't connect — see [MSFS won't connect over SimConnect](troubleshooting.md#msfs-wont-connect-over-simconnect).
- You need the logs — see [where to find log files](troubleshooting.md#where-to-find-log-files).

The installer writes its own log to your `%TEMP%` folder as `Setup Log <date> #nnn.txt`. If an install itself fails, that file is the thing to attach to a report.
