# FSOps in-game panel

A small MSFS 2024 Community package that adds an "FSOps" button to the in-game toolbar. Clicking
it opens a panel showing an iframe of FSOps' own `/panel` page (live flight phase, ETA, landing
data, airline cash, next scheduled flights) served from FSOps' local server. This is the same
technique used by Navigraph's in-game panel and by `buzinin/msfs2024-efb-panel` - a toolbar panel
whose content is just an iframe pointed at `http://localhost:<port>`.

## Status: loaded by the simulator for the first time (2026-08-12) - found broken, three fixes applied, none simulator-confirmed

The panel was installed and MSFS 2024 loaded it for real for the first time on 2026-08-12. Two
things were proven by that alone, before anything else, and remain the most solid facts in this
document: the compiled `.spb` **is** read and honoured by the sim (the toolbar button appeared,
positioned correctly, and opened a window), and the install machinery **does** produce a correct
`layout.json` (confirmed by reading the actual installed copy in the player's Community folder
afterwards - present, correctly formatted, listing every file).

What was broken that same day: the button had no icon, and the window it opened was entirely
black - no content, no title, and critically **no error, warning, or failed-load message of any
kind** in anything that could be checked. An empty console is weak evidence; it rules nothing in.
Three fixes have been applied since, each addressing a specific, plausible read of that symptom.
**None of the three has been confirmed by actually loading MSFS and seeing panel content** - every
one of them is a well-evidenced inference from comparing this package against other, genuinely
working MSFS 2024 packages installed on the same machine, not a result observed after the fix was
applied. Do not read "fixed" anywhere below as "confirmed working."

1. **Toolbar button had no icon** - rendered as a plain colour square. Inferred cause: the icon
   shipped only at the legacy MSFS 2020 path, `html_ui/Textures/Menu/toolbar/`. Two other real,
   currently-installed MSFS 2024 packages on the machine this was diagnosed on - `fsdreamteam-gsx-pro`
   (built fresh for 2024) and `fsltl-traffic-injector` (a 2020-era package patched for 2024) - both
   ship the icon at `html_ui/icons/toolbar/` instead (FSLTL ships it at *both* paths). FSOps now
   does the same - see "Toolbar icon ships at two paths" below. This is a plain file addition, no
   recompile needed, already applied.
2. **Panel content was entirely black**, and the window's title bar read `FSOPSPANEL` (the `.spb`'s
   registered `Name`, uppercased) rather than the panel's own title. Inferred cause:
   `source/PackageSources/FSOpsPanel.xml` declared
   `url="html_UI/InGamePanels/FSOpsPanel/FSOpsPanel.html"` (capital `UI`), while the package's real
   folder on disk, and the `layout.json` generated for it, both say `html_ui` (lower-case) - so does
   every other real package checked. That mismatch is fixed in `source/PackageSources/FSOpsPanel.xml`
   and the `.spb` was rebuilt to carry it (below). A `<title>FSOps</title>` was also added to
   `FSOpsPanel.html`'s `<head>` as a low-risk second fix (present in one confirmed-working reference
   package, absent in another, so not decisive on its own - kept because it costs nothing and is
   more correct regardless).
3. **(2026-08-13, `868bec2`) The manifest declared the package as engine content with pinned
   dependencies.** `manifest.json` had `content_type: "CORE"` - the type MSFS uses for its own
   internals, not for a Community add-on - and three `dependencies` entries pinned to specific
   versions of base packages (`fs-base-propdefs`, `fs-base-ui`, `asobo-vcockpits-core`). Both
   `fsdreamteam-gsx-pro` and `fsltl-traffic-injector`, the same two reference packages used above,
   declare `content_type` of `SCENERY` and `MISC` respectively and an empty `dependencies` list -
   see "manifest.json fields" below for the exact values read from both. Either wrong field would
   produce exactly the symptom seen on 2026-08-12: engine content isn't mounted from a Community
   folder the way an add-on is, and a dependency that can't be satisfied lets a package be skipped
   while a toolbar registration already scanned earlier stays behind - registration surviving while
   files don't is precisely "button present, window empty, nothing logged." `manifest.json` now
   declares `content_type: "MISC"` and `dependencies: []`, matching what the SDK's own packaging
   tool generates by default for a package with none. **This is the current best explanation for
   the empty window, not a confirmed fix** - it has not yet been tested against a running
   simulator. If the window is still empty after this change, fixes 1 and 2 above remain live
   possibilities too; a newer finding does not rule out an older one just for being newer.

**What's established vs what's still a guess, for a reader in a hurry:**

- Established (read directly off disk, or observed on a real, running MSFS 2024 session): the
  compiled `.spb` is read and the toolbar button appears and opens a window (2026-08-12); the
  install machinery writes a correct `layout.json`; the folder layout matches two genuinely working
  2024 packages; the icon-path convention and the two reference manifests' `content_type` and
  `dependencies` values, both read directly from the installed copies on this machine.
- Still unproven: that any of fixes 1-3 above actually makes panel content appear inside MSFS. The
  window has been confirmed empty exactly once, on 2026-08-12, before any of the three fixes
  existed. It has not been re-tested since.

The icon fix (item 1) and the manifest fix (item 3) are loose files, not compiled, so any Repair
from FSOps' Settings picks them up. The casing fix (item 2) needed a recompile - see the note on the
rebuilt `.spb` below. **All three only reach a player through a new build of FSOps** -
`PanelPackageInstaller` copies from a template bundled inside the installed application, so a
Repair reinstalls whatever that build happens to carry, not whatever is in this repository. Editing
files here and telling someone to hit Repair does nothing at all.

> **The `.spb` was rebuilt on 2026-08-13** to carry the item-2 casing fix.
> `package/InGamePanels/FSOpsPanel.spb` is SHA-256
> `B56CA7179FA7223A3A831045BD8DD4777B36B3BFBE53A727E32A7608DDE398D9`, replacing `5AA95E70...`. The
> two files are the same 668 bytes and differ in exactly two adjacent bytes - nothing can read a
> path out of this format, since values are stored by reference, but a two-byte change is what a
> reference to a same-length path looks like when the only edit was the case of two letters. The
> build is trustworthy because `source/_out/` was deleted outright beforehand and the file
> reappeared in it. Item 3 (the manifest) needed no recompile - `manifest.json` sits outside the
> compiled `.spb` entirely and is copied to the player's Community folder as-is.

## Rebuilding the `.spb` (only needed if `source/PackageSources/FSOpsPanel.xml` changes)

The panel's HTML, CSS, JS and the port it points at are plain text and are **never** compiled.
Only the toolbar registration - the icon, panel ID, default size and docking - lives in the
compiled file. So a change to the panel's content, or to the port, never needs the SDK.

**The tool does not compile anything itself - it hands the work to `FlightSimulator2024.exe`.** So
the simulator must be closed (the tool launches its own copy) and the tool must be able to launch
it, which on a Microsoft Store installation it cannot do without help. See trap 4.

One-time setup, and the thing that makes the difference: put the real path to the simulator in the
override file that ships next to the tool, **with no byte-order mark**, or the path is read with an
invisible character on the front and rejected:

```
[System.IO.File]::WriteAllText("C:\MSFS 2024 SDK\Tools\bin\fspackagetool_overrideExePath.txt", "C:\XboxGames\Microsoft Flight Simulator 2024\Content\FlightSimulator2024.exe", (New-Object System.Text.UTF8Encoding($false)))
```

Then, with MSFS closed:

```
& "C:\MSFS 2024 SDK\Tools\bin\fspackagetool.exe" "<repo>\src\fsops-ingame-panel\source\FSOpsPanel.xml" -rebuild -nopause
```

`-rebuild` is not optional in practice: the tool caches build state under `source/_Temp/`, and a
changed source file alone will not always persuade it there is work to do. The simulator will start
up on screen; that is the build running. Delete `source/_out/` first, so anything that appears is
unambiguously new.

**Then verify it actually produced something before trusting it** - see trap 4. Only once
`source/_out/Packages/fsops-panel/InGamePanels/FSOpsPanel.spb` exists and its hash differs from the
committed copy, copy the result over the committed copy:

```
copy "<repo>\src\fsops-ingame-panel\source\_out\Packages\fsops-panel\InGamePanels\FSOpsPanel.spb" "<repo>\src\fsops-ingame-panel\package\InGamePanels\FSOpsPanel.spb"
```

`source/_out/` and `source/_Temp/` are build scratch and are gitignored; the compiled `.spb` under
`package/` is what is committed.

### Four things about the package tool that cost time to work out

These are recorded because none of them are obvious, and three of them fail *silently*.

1. **The tool takes a project file, not the panel definition.** An earlier version of this README
   told you to point `fspackagetool.exe` straight at the panel definition XML with a `-nomirroring`
   flag. Neither is right - there is no `-nomirroring` flag, and the tool only accepts a `<Project>`
   file, which then references a package definition, which then references the folder of sources.
   That is why `source/` has three XML files for what is conceptually one panel.
2. **The asset group type is `SPB`, not `InGamePanels`.** The panel document declares
   `Type="InGamePanels"` internally, but that describes the *document*; `SPB` ("SimPropBinary") is
   the *compiler*. MSFS 2024's asset-type list has no `InGamePanels` entry at all. Getting this
   wrong produces no output and no error message.
3. **The compiled file is named after the source file**, not after the `<Filename>` element inside
   the document. `PackageSources/FSOpsPanel.xml` is what makes the output `FSOpsPanel.spb`; the
   `<Filename>InGamePanel_FSOpsPanel.spb</Filename>` line inside it does not control the name.
   This is why the source file is named for the panel rather than something descriptive.
4. **The tool attaches to a *running* MSFS instance, and on this machine neither survived that -
   with no error either way.** Run against a real, already-open MSFS 2024 session (2026-08-12): the
   tool printed `Attached to EXE - waiting for completion.` and nothing else, then exited by itself
   about fifty seconds later with a blank (unreadable) exit code. `source/_out/` was never created -
   no output, no partial output, nothing. In the same window, the running `FlightSimulator2024.exe`
   process disappeared entirely - it did not merely lose focus or hang, it was gone from the process
   list, and nothing that ran that evening issued a stop/kill against it. The working conclusion:
   **`fspackagetool.exe` must be run with MSFS 2024 completely closed.** It is not documented
   anywhere obvious that the tool wants to attach to a live instance at all, so this is easy to get
   wrong by simply having the sim open in the background while you rebuild a package, the way you
   normally would for everything else in this repo. Because this fails with no error, no exception,
   and a *clean-looking* exit, the only reliable check is the one already recommended above:
   confirm `source/_out/...FSOpsPanel.spb` exists and hash it against the previously-committed copy
   before assuming anything changed - a successful-looking run is not evidence of a successful build.

## Why the port lives in its own file

MSFS's toolbar panel loads `FSOpsPanel.html`, which is static once installed - it can't ask FSOps
what port it's running on. FSOps' server defaults to port 5977 but can be moved with `FSOPS_PORT`
(see `Program.cs`), so the panel needs to know the *current* port without anyone recompiling
anything. `FSOpsPanel.config.js` is a one-line file (`window.FSOPS_PANEL_PORT = <port>;`) and it is
the *only* file `PanelPackageInstaller` rewrites on every install/update/repair. Every other file,
including the `.spb`, is untouched by a port change - so moving FSOps to a different port never
requires the MSFS SDK or a recompile, only re-running install from the app.

If FSOps later moves port, an already-installed panel keeps pointing at the old one. FSOps'
Settings page detects that drift and offers Repair, which rewrites just this file.

## Toolbar icon ships at two paths

The toolbar button appeared with no icon (a plain colour square) the first time this package was
loaded by a real MSFS 2024 install. `source/PackageSources/FSOpsPanel.xml` references the icon only
by bare ID (`icon="ICON_TOOLBAR_FSOPS_PANEL"`, no path), so MSFS resolves it by searching fixed,
conventional directories rather than a literal path baked into the compiled `.spb` - unlike the
panel's HTML `url`, which *is* a literal baked-in path (see the status note above).

This package was built from a 2020-era reference template and only ever shipped the icon at the
MSFS 2020 convention, `html_ui/Textures/Menu/toolbar/`. Two real MSFS 2024 packages installed on
the machine this was diagnosed on say otherwise:

- `fsdreamteam-gsx-pro` (built and versioned for MSFS 2024, `minimum_game_version` 1.39.12) ships
  its toolbar icon *only* at `html_ui/icons/toolbar/` - no legacy copy at all.
- `fsltl-traffic-injector` (an older, 2020-era package since patched for 2024,
  `minimum_game_version` 1.32.7) ships the *identical* icon file at **both**
  `html_ui/icons/toolbar/` and `html_ui/Textures/Menu/toolbar/` - the same shape of fix being made
  here.

So the icon now ships at both paths too: `html_ui/icons/toolbar/ICON_TOOLBAR_FSOPS_PANEL.svg` (the
new, apparently MSFS-2024-read one) and `html_ui/Textures/Menu/toolbar/ICON_TOOLBAR_FSOPS_PANEL.svg`
(the original, kept for compatibility - not a leftover to be cleaned up later). This is a plain file
addition - the `.spb` does not need recompiling for it, `PanelPackageInstaller` picks it up
automatically because `CopyTemplateFiles` and `WriteLayoutJson` both walk the template directory
rather than reading a hardcoded file list (see
`PanelPackageInstallerTests.InstallOrRepair_FromTheRealTemplate_WritesTheToolbarIconAtBothTheNewAndLegacyPaths`
and the companion repair test, which install from this real template into a throwaway folder and
assert both files land and both are listed in the generated `layout.json`).

Which of the two paths MSFS 2024 actually reads has not been confirmed by launching the sim - only
inferred by comparing against other installed packages. Shipping both costs a few hundred bytes and
cannot make things worse, so there was no reason to guess between them.

## Package layout

```
package/                                          <- copied byte-for-byte into
                                                      <Community>/fsops-panel/ by the app
  manifest.json                                   <- package metadata + version stamp
                                                      (package_version - the app compares this
                                                      against what's installed to detect drift)
  html_ui/
    InGamePanels/FSOpsPanel/
      FSOpsPanel.html                             <- registers the <ingamepanel-fsops> element
      FSOpsPanel.css                               and lays out the iframe
      FSOpsPanel.js
      FSOpsPanel.config.js                        <- ONLY file the app rewrites (the port)
    icons/toolbar/
      ICON_TOOLBAR_FSOPS_PANEL.svg                <- toolbar icon, MSFS 2024 path - see
                                                      "Toolbar icon ships at two paths" below
    Textures/Menu/toolbar/
      ICON_TOOLBAR_FSOPS_PANEL.svg                <- SAME icon, legacy MSFS 2020 path, kept for
                                                      compatibility - not a leftover, see below
  InGamePanels/
    FSOpsPanel.spb                                <- compiled toolbar registration (committed)

source/                                           <- everything the SDK needs to rebuild the .spb
  FSOpsPanel.xml                                  <- the project file you point the tool at
  PackageDefinitions/fsops-panel.xml              <- declares the SPB asset group
  PackageSources/FSOpsPanel.xml                   <- the panel definition itself; the compiled
                                                      file takes its name from this filename
```

`layout.json` is deliberately **not** checked in here. MSFS's virtual file system uses it as an
integrity manifest (every file's path, size, and last-write time), and hand-maintaining a second
copy that has to be kept in sync with the real files is exactly the kind of duplicate-source-of-
truth mistake that produces a stale, silently-wrong package. `PanelPackageInstaller` generates it
fresh from the real files it just wrote, every single time it installs, updates, or repairs.

## manifest.json fields

`minimum_game_version` and `minimum_compatibility_version` were previously inherited from
MSFS-2020-era references because no 2024-dated example could be found. They are no longer guesses:
the 2024 SDK, building this exact package, reported `minimum_game_version` **1.7.35** and
`minimum_compatibility_version` **7.26.0.214**, and those are the values now in `manifest.json`.

The trade-off worth knowing: this is stricter than the `1.0.0` that was there before. A player on
an MSFS 2024 build older than 1.7.35 will find the package simply does not load, with no error. If
that is ever reported, lowering `minimum_game_version` back to `1.0.0` is the first thing to try -
the panel uses no version-specific features, so the strict value reflects what it was built
against rather than anything it actually needs.

**`content_type` and `dependencies` changed on 2026-08-13 (`868bec2`)** - see item 3 under Status
above for the reasoning. Was `content_type: "CORE"` with three pinned `dependencies`
(`fs-base-propdefs`, `fs-base-ui`, `asobo-vcockpits-core`); now `content_type: "MISC"` with
`dependencies: []`. The two reference packages read directly off this machine's installed
Community folder, for comparison:

| Package | `content_type` | `dependencies` |
| --- | --- | --- |
| `fsdreamteam-gsx-pro` (built fresh for 2024) | `SCENERY` | `[]` |
| `fsltl-traffic-injector` (2020-era, patched for 2024) | `MISC` | `[]` |
| FSOps panel, before `868bec2` | `CORE` | 3 pinned entries |
| FSOps panel, now | `MISC` | `[]` |

`MISC` was chosen to match `fsltl-traffic-injector` specifically, since it is - like this panel -
a UI/toolbar add-on with no scenery of its own; `SCENERY` is presumably right for GSX because it
also places jetway and ground-service objects in the world. Neither reference package declares any
dependency, and the packaging tool's own freshly-generated manifest for this package (used as the
tie-breaker) declares none either, which is why `dependencies` is now empty rather than pointing at
some other version. **This table records what was compared, not a confirmed outcome** - whether
`MISC` with no dependencies is actually the combination MSFS 2024 wants for this package is exactly
what item 3 under Status has not yet confirmed.

Two other fields changed in the same commit, both incidental to the fix: `creator` went from `""`
to `"FSOps"`, and the `total_package_size` field (a checksum-like placeholder of zeroes,
`"00000000000000000000"`) was dropped entirely - it does not appear in the packaging tool's own
freshly-generated manifest either, and nothing was found that reads it.

## References consulted (not guessed)

- `bymaximus/msfs2020-toolbar-window-template` - a working, source-available toolbar panel using
  the identical iframe technique; `manifest.json`, `layout.json`, the
  `InGamePanels.InGamePanelDefinition` XML schema, the project/package-definition file structure,
  and the panel HTML/CSS/JS structure in this package are adapted directly from it.
- `bymaximus/msfs2020-toolbar-little-nav-map` - a second, independent working panel, originally
  used to cross-check the three pinned `dependencies` entries the manifest declared before
  `868bec2` removed them. Superseded for that specific question by the two real, installed MSFS
  2024 packages compared under "manifest.json fields" above, which declare no dependencies at all.
- Official MSFS 2024 SDK docs (`docs.flightsimulator.com/msfs2024`) - Package Tool XML Properties
  and Asset Types pages, which is where the `SPB` asset type and the `<AssetPackage>` schema come
  from.
- MSFS 2024 Community-folder locations (Microsoft Store vs Steam vs custom `UserCfg.opt` /
  `InstalledPackagesPath`) were cross-checked across the official forums and independent guides;
  see the doc comments in `PanelPackageInstaller.cs` for how detection handles the conflicting
  folder names found during that research.
