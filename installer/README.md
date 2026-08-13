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

### Uninstalling does remove the in-game panel

Unlike the database, the panel FSOps installs into the player's MSFS Community folder is not their
data — it's a folder of files FSOps itself copied and generated there, reproducible at any time by
pressing Install again. So it's cleaned up automatically and silently, with no prompt, as an
ordinary part of removing the program: the installer's `[UninstallRun]` section runs
`FSOps.Server.exe --uninstall-panel`, which reads the Community folder path straight out of the
player's own database (the app is the only thing that ever knew it) and removes the panel through
`PanelPackageInstaller.Uninstall` — the same code path Settings uses, including the check that
refuses to touch a folder FSOps didn't create.

The ordering matters and is why this lives in `[UninstallRun]` rather than
`CurUninstallStepChanged`: those entries run "as the first step of uninstallation", before Inno
removes anything from `{app}` and well before the data-directory prompt above (which fires later,
at `usPostUninstall`). So panel removal always has both a working server executable and an intact
database to read from, regardless of what the player answers at that later prompt. See
`PanelUninstallCommand` (in `src/FSOps.Server/Services`) for the implementation and its tests; the
Pascal side is deliberately just a call to it, since Inno Setup isn't installed in most development
and CI environments and Pascal can't be unit tested the way C# can.

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

### The procedure

`.github/workflows/release.yml` does the building. It runs on any tag matching `v*.*.*`, and it
stops at a **draft** — it never publishes anything.

1. **Bump `<Version>` in `Directory.Build.props`** to the version you are about to release, and
   commit that. Do this first; step 4 refuses to build if it is not done.
2. **Optional dry run.** Run the Release workflow manually from the Actions tab against `main`,
   putting the tag you intend to use in the `tag` box. It checks the version, builds the installer
   and smoke-tests it, and creates no release. Worth doing if anything about the packaging has
   changed.
3. **Tag and push.**

   ```
   git tag v0.2.0
   git push origin v0.2.0
   ```

4. **The workflow runs.** In order: it checks the tag against `<Version>` and stops immediately if
   they disagree; installs Inno Setup; runs `build-installer.ps1`; installs the result silently;
   asserts the real SPA is present and that the installed server actually serves it; uninstalls;
   asserts `%LOCALAPPDATA%\FSOps` survived. Both files are uploaded as workflow artifacts whether
   or not that passes, so a failed run can still be inspected.
5. **A draft release appears**, titled `FSOps <version>`, with the installer and its `.sha256`
   attached and the checksum written into the notes.
6. **Fly it.** Install the draft's installer on a real machine and fly a sector end to end in MSFS.
   Nothing in CI can do this, which is the entire reason the release stops at a draft.
7. **Publish by hand** from the Releases page, editing the notes first. Until you click Publish,
   the release does not exist as far as users are concerned: the updater reads
   `/releases/latest`, which excludes drafts.

To back out before step 7, delete the draft and the tag. After step 7, you cannot — the version is
compiled into an assembly people have installed.

### Why the workflow checks the tag against `Directory.Build.props`

It is the first thing it does, before it installs or builds anything, and a mismatch fails the run.

A `v0.2.0` tag with `<Version>0.1.0</Version>` would produce a release called 0.2.0 containing an
installer named `FSOps-Setup-0.1.0.exe` and an app that reports itself as 0.1.0. Every user who
installed it would then be offered 0.2.0 on every check, permanently, because the app it installed
never claims to be the version the release advertises. Editing the release afterwards does not fix
it; the wrong version is already inside the assembly on their disk.

The tag may be written with or without the leading `v`.

### Why the release is only ever a draft

Two reasons, and neither is squeamishness. FSOps ships unsigned, so an installer that reaches users
without a human deciding it should is not a thing this project does. And the test that decides
whether a build is good — flying a sector end to end in the simulator — is one no CI runner can
perform, so "all checks green" is not the same as "ready".

The workflow creates no tags of its own either: `gh release create` will invent a missing tag, so
it is called with `--verify-tag`.
