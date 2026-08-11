# Installer

The FSOps installer: an [Inno Setup](https://jrsoftware.org/isinfo.php) script that wraps the
self-contained publish output into `FSOps-Setup-<version>.exe`.

This folder is about *building* the installer. If you want to *install* FSOps, see
[docs/guides/installation.md](../docs/guides/installation.md).

| File | What it is |
| --- | --- |
| `FSOps.iss` | The Inno Setup script. |
| `build-installer.ps1` | Publishes, compiles, and writes the checksum sidecar. Use this. |

## Building it

Install [Inno Setup](https://jrsoftware.org/isdl.php) **6.3 or newer**, then from the repository
root:

```
.\installer\build-installer.ps1
```

That runs `scripts\publish.ps1` first, so it is the only command you need. If you already have a
current `artifacts\publish`, skip straight to compiling:

```
.\installer\build-installer.ps1 -SkipPublish
```

Two files land in `artifacts\installer`:

```
FSOps-Setup-0.1.0.exe
FSOps-Setup-0.1.0.exe.sha256
```

**A release must publish both.** See [Releasing](#releasing) below for why the sidecar is not
optional.

To compile by hand without the wrapper — bearing in mind you then have to produce the checksum
yourself — the script's own invocation is:

```
ISCC /DAppVersion=0.1.0 /DPublishDir=<repo>\artifacts\publish /DOutputDir=<repo>\artifacts\installer installer\FSOps.iss
```

All three defines are optional; their defaults are in the script's header.

## The decisions worth knowing about

### It installs per-user and never elevates

FSOps is unsigned. An unsigned installer that requests administrator rights produces the red
"unknown publisher" elevation prompt, which is the worst possible first impression and buys nothing
here: there is no service, no driver, no shared component, and Kestrel binds localhost so no
firewall rule is needed. The app already writes everything to `%LOCALAPPDATA%` regardless of how it
was installed. So Setup installs to `%LOCALAPPDATA%\Programs\FSOps` with
`PrivilegesRequired=lowest`, and the whole install is one UAC prompt shorter.

### The publish folder is copied verbatim

One recursive `[Files]` entry, no filtering. `scripts\publish.ps1` is the authority on what the
layout is, and the day this script holds a second opinion is the day the two drift apart and an
install silently ships without something the app needs. That is not hypothetical: `WebView2Loader.dll`
must sit at the publish root — it ships as plain content, never appears in `.deps.json`, and a
self-contained build has no NuGet cache to fall back on — and a build that omitted it failed at
launch with an unloadable-DLL error before any window appeared. Anything that does not belong in an
install should be excluded at publish time, not here.

### It fails at compile time, not on a user's machine

The script's `#if !FileExists(...)` block asserts that every runtime-resolved asset is present in
the publish before it will compile: both executables, the WebView2 loader, the self-contained
runtime, `economy-config.json`, the built SPA, and the in-game panel package including its compiled
`.spb`. A forgotten or half-finished publish otherwise produces an installer that compiles cleanly
and installs a broken app, which is a failure that surfaces on someone else's PC.

### WebView2 is offered, never required

The Evergreen WebView2 runtime ships with Windows 11 and reaches Windows 10 through Edge updates, so
on almost every machine the installer does nothing about it and the user is never asked. Where it is
genuinely absent — LTSC, some managed images — Setup offers to download Microsoft's ~2 MB
bootstrapper, and the user can decline.

Nothing about this can fail an install. FSOps does not need WebView2: the shell detects its absence
before building a window and opens the SPA in the default browser instead. Declining, being offline,
a blocked proxy, an aborted download, or a bootstrapper that refuses to run all end the same way —
the install completes. Detection reads the EdgeUpdate client key in all three registry views
(`HKLM32`, `HKLM64`, `HKCU`), because a machine-wide runtime and one installed by a non-elevated
bootstrapper land in different places.

### Uninstalling never destroys an airline

`%LOCALAPPDATA%\FSOps` holds the database — the user's airline, fleet, routes, pilots, flight
history and ledger. Inno only removes what it installed, so the default path already leaves it
alone, and there is no `[UninstallDelete]` entry pointing anywhere near it.

The uninstaller does offer to remove it, once, and that offer is deliberately hard to trigger by
accident: it is opt-in, the default button is **No**, it names the exact folder and lists what is
inside it, and it is skipped entirely during a silent uninstall (nobody is there to answer) or when
`FSOPS_DATA_DIR` has moved the data somewhere this installer never knew about (deleting the
LocalAppData default would then destroy a folder that is not the one in use).

If you change anything in `CurUninstallStepChanged`, test it against a throwaway
`FSOPS_DATA_DIR` and never against a real save.

## Releasing

The installer's name is a contract, not a convention. `UpdateChecker.SelectInstallerAsset` picks the
release asset whose name starts with `FSOps-Setup` and ends in `.exe`, and
`SelectChecksumAsset` requires a sidecar named exactly `<installer>.exe.sha256`.

**Publish both files.** With an installer but no checksum, FSOps still tells users a new version
exists and links them to the release page, but the in-app download is disabled — FSOps ships
unsigned, so that hash is the only thing separating "the file the author built" from "a file from
the internet", and an updater that cannot verify what it downloads should not download it.

The version comes from `<Version>` in `Directory.Build.props`, read by `build-installer.ps1`. It is
not repeated here on purpose: the running app reports that same property as its own version, and the
update checker compares the two. An installer named `0.2.0` containing an assembly that says `0.1.0`
would offer every user on 0.2.0 an endless upgrade to 0.2.0. Bump the property, and the installer
name follows.

A release therefore needs, in order:

1. `<Version>` in `Directory.Build.props` bumped to match the release tag.
2. `.\installer\build-installer.ps1`
3. Both files from `artifacts\installer` attached to the GitHub release.
