# FSOps in-game panel

A small MSFS 2024 Community package that adds an "FSOps" button to the in-game toolbar. Clicking
it opens a panel showing an iframe of FSOps' own `/panel` page (live flight phase, ETA, landing
data, airline cash, next scheduled flights) served from FSOps' local server. This is the same
technique used by Navigraph's in-game panel and by `buzinin/msfs2024-efb-panel` - a toolbar panel
whose content is just an iframe pointed at `http://localhost:<port>`.

## Status: the toolbar button does not work yet - one manual step is required

Everything in this folder is real and has been verified against a working reference (see
"References" below): `manifest.json`, `layout.json` (generated at install time - see below),
the `html_ui/InGamePanels/FSOpsPanel/*` panel files, and `source/FSOpsPanelDefinition.xml`.

What's missing is `package/InGamePanels/FSOpsPanel.spb` - a **binary file** that registers the
panel with MSFS's toolbar (its icon, panel ID, default size and docking). MSFS does not read
`source/FSOpsPanelDefinition.xml` directly; that XML has to be **compiled** into the `.spb` by the
MSFS SDK's `fspackagetool.exe`, and the result checked into this repository. That compiler is a
Windows tool that ships only with the separately-installed MSFS SDK - it is not part of the game
install, and it cannot be run inside an automated coding session with no SDK and no GUI. Until
someone with the SDK installed runs the command below once, the rest of the package will install
correctly (the app's install/update/repair/uninstall pipeline handles it exactly like any other
file) but **no toolbar button will appear in MSFS**, because the piece that registers it is
missing. FSOps' own install result is honest about this - it reports `spbPresent: false` and
`toolbarWillAppearInSim: false` rather than claiming success.

### How to finish it (one-time, needs the MSFS SDK)

1. Install the free MSFS 2024 SDK (Developer Mode inside the sim, or the standalone SDK
   installer from the Microsoft Flight Simulator website).
2. From a shell with the SDK's `Tools\bin` on the path (or using its full path), run:

   ```
   fspackagetool.exe "<path-to-this-repo>\src\fsops-ingame-panel\source\FSOpsPanelDefinition.xml" -nomirroring
   ```

3. The tool writes a compiled `.spb`. Rename/move it so it ends up at exactly:

   ```
   src/fsops-ingame-panel/package/InGamePanels/FSOpsPanel.spb
   ```

4. Commit that one binary file. Nothing else in this folder needs to change - the app's build
   already copies everything under `package/` into the server's output as `PanelTemplate/`, and
   `PanelPackageInstaller` copies whatever is there (including the now-present `.spb`) into the
   player's Community folder the next time they install, update, or repair the panel from FSOps.

No further compilation is needed after that unless `source/FSOpsPanelDefinition.xml` itself
changes (a different icon, panel ID, or default size) - the HTML/CSS/JS panel content and the port
it points at are plain text and are never compiled.

## Why the port lives in its own file

MSFS's toolbar panel loads `FSOpsPanel.html`, which is static once installed - it can't ask FSOps
what port it's running on. FSOps' server defaults to port 5977 but can be moved with `FSOPS_PORT`
(see `Program.cs`), so the panel needs to know the *current* port without anyone recompiling
anything. `FSOpsPanel.config.js` is a one-line file (`window.FSOPS_PANEL_PORT = <port>;`) and it is
the *only* file `PanelPackageInstaller` rewrites on every install/update/repair. Every other file,
including the not-yet-present `.spb`, is untouched by a port change - so moving FSOps to a
different port never requires the MSFS SDK or a recompile, only re-running install from the app.

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
    FSOpsPanel.spb                                <- NOT PRESENT YET, see above

source/
  FSOpsPanelDefinition.xml                        <- human-readable source the .spb is compiled
                                                      from; kept for anyone who needs to change the
                                                      panel's icon, ID, or default size later
```

`layout.json` is deliberately **not** checked in here. MSFS's virtual file system uses it as an
integrity manifest (every file's path, size, and last-write time), and hand-maintaining a second
copy that has to be kept in sync with the real files is exactly the kind of duplicate-source-of-
truth mistake that produces a stale, silently-wrong package. `PanelPackageInstaller` generates it
fresh from the real files it just wrote, every single time it installs, updates, or repairs.

## References consulted (not guessed)

- `bymaximus/msfs2020-toolbar-window-template` - a working, source-available toolbar panel using
  the identical iframe technique; `manifest.json`, `layout.json`, the
  `InGamePanels.InGamePanelDefinition` XML schema, and the panel HTML/CSS/JS structure in this
  package are adapted directly from it.
- `bymaximus/msfs2020-toolbar-little-nav-map` - a second, independent working panel, used to
  cross-check the manifest dependency versions above.
- Official MSFS 2024 SDK docs (`docs.flightsimulator.com/msfs2024`) - Package Tool page, confirming
  `fspackagetool.exe` ships with the SDK (not the game) and compiles `.spb` files from XML.
- MSFS 2024 Community-folder locations (Microsoft Store vs Steam vs custom `UserCfg.opt` /
  `InstalledPackagesPath`) were cross-checked across the official forums and independent guides;
  see the doc comments in `PanelPackageInstaller.cs` for how detection handles the conflicting
  folder names found during that research.

`minimum_game_version` and the dependency package versions in `manifest.json` are inherited from
those MSFS-2020-era references, because no MSFS-2024-dated example could be found publicly during
this work - Developer Mode's Project Editor will flag it if either needs bumping when the `.spb` is
compiled.
