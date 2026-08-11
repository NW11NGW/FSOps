# FSOps in-game panel

A small MSFS 2024 Community package that adds an "FSOps" button to the in-game toolbar. Clicking
it opens a panel showing an iframe of FSOps' own `/panel` page (live flight phase, ETA, landing
data, airline cash, next scheduled flights) served from FSOps' local server. This is the same
technique used by Navigraph's in-game panel and by `buzinin/msfs2024-efb-panel` - a toolbar panel
whose content is just an iframe pointed at `http://localhost:<port>`.

## Status: the package is complete and the `.spb` is compiled and committed

`package/InGamePanels/FSOpsPanel.spb` is present. It was compiled with the MSFS 2024 SDK's
`fspackagetool.exe` against a real MSFS 2024 install, produced no errors, and is byte-for-byte
reproducible from the sources in `source/` (verified by building the same project twice from
different directories and comparing SHA-256).

Nothing further is required to ship the panel. It has **not yet been loaded by the simulator** -
that happens the first time the sim is started with the package installed, and is the one step
that cannot be verified without launching MSFS and looking at the toolbar.

## Rebuilding the `.spb` (only needed if `source/PackageSources/FSOpsPanel.xml` changes)

The panel's HTML, CSS, JS and the port it points at are plain text and are **never** compiled.
Only the toolbar registration - the icon, panel ID, default size and docking - lives in the
compiled file. So a change to the panel's content, or to the port, never needs the SDK.

With the MSFS 2024 SDK installed, and MSFS 2024 installed (the tool drives the simulator to do
the conversion, so the game itself must be present):

```
& "C:\MSFS 2024 SDK\Tools\bin\fspackagetool.exe" "<repo>\src\fsops-ingame-panel\source\FSOpsPanel.xml" -nopause
```

Then copy the result over the committed copy:

```
copy "<repo>\src\fsops-ingame-panel\source\_out\Packages\fsops-panel\InGamePanels\FSOpsPanel.spb" "<repo>\src\fsops-ingame-panel\package\InGamePanels\FSOpsPanel.spb"
```

`source/_out/` and `source/_Temp/` are build scratch and are gitignored; the compiled `.spb` under
`package/` is what is committed.

### Three things about the package tool that cost time to work out

These are recorded because none of them are obvious, and two of them fail *silently*.

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
    Textures/Menu/toolbar/
      ICON_TOOLBAR_FSOPS_PANEL.svg                <- toolbar icon (MSFS recolours it; single
                                                      black fill, matches the reference format)
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

## manifest.json version fields

`minimum_game_version` and `minimum_compatibility_version` were previously inherited from
MSFS-2020-era references because no 2024-dated example could be found. They are no longer guesses:
the 2024 SDK, building this exact package, reported `minimum_game_version` **1.7.35** and
`minimum_compatibility_version` **7.26.0.214**, and those are the values now in `manifest.json`.

The trade-off worth knowing: this is stricter than the `1.0.0` that was there before. A player on
an MSFS 2024 build older than 1.7.35 will find the package simply does not load, with no error. If
that is ever reported, lowering `minimum_game_version` back to `1.0.0` is the first thing to try -
the panel uses no version-specific features, so the strict value reflects what it was built
against rather than anything it actually needs.

The `dependencies` block (`fs-base-propdefs`, `fs-base-ui`, `asobo-vcockpits-core`) is unchanged
and matches what a known-working community toolbar panel declares.

## References consulted (not guessed)

- `bymaximus/msfs2020-toolbar-window-template` - a working, source-available toolbar panel using
  the identical iframe technique; `manifest.json`, `layout.json`, the
  `InGamePanels.InGamePanelDefinition` XML schema, the project/package-definition file structure,
  and the panel HTML/CSS/JS structure in this package are adapted directly from it.
- `bymaximus/msfs2020-toolbar-little-nav-map` - a second, independent working panel, used to
  cross-check the manifest dependency versions above.
- Official MSFS 2024 SDK docs (`docs.flightsimulator.com/msfs2024`) - Package Tool XML Properties
  and Asset Types pages, which is where the `SPB` asset type and the `<AssetPackage>` schema come
  from.
- MSFS 2024 Community-folder locations (Microsoft Store vs Steam vs custom `UserCfg.opt` /
  `InstalledPackagesPath`) were cross-checked across the official forums and independent guides;
  see the doc comments in `PanelPackageInstaller.cs` for how detection handles the conflicting
  folder names found during that research.
