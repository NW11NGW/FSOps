; FSOps installer (Inno Setup 6.3 or newer).
;
; Builds a per-user, unsigned installer around the self-contained publish that scripts\publish.ps1
; produces. Compile it with installer\build-installer.ps1, which runs the publish first and passes
; the version in - see that script for the one-line invocation.
;
; Two decisions drive most of what follows, and both are deliberate:
;
;   PER-USER, NEVER ELEVATED. FSOps ships unsigned because there is no code-signing certificate.
;   An unsigned installer that asks for administrator rights shows the red "unknown publisher"
;   elevation prompt, which is the single worst thing we could put in front of someone installing a
;   hobby app for the first time. Nothing here needs machine-wide rights either: there is no
;   service, no driver, no shared component, and Kestrel binds localhost so no firewall rule is
;   required. The app already keeps every byte it writes under %LOCALAPPDATA% (see AppPaths), which
;   is per-user regardless. So Setup installs into the user's own profile, never elevates, and the
;   whole install is one UAC prompt shorter than the alternative.
;
;   THE USER'S SAVE IS NOT OURS TO DELETE. %LOCALAPPDATA%\FSOps holds the database - their airline,
;   fleet, routes, pilots, flight history and ledger. Uninstalling FSOps removes the program and
;   leaves that untouched. See CurUninstallStepChanged for the one narrow, opt-in exception.
;
;   WHAT FSOPS PUT IN THE COMMUNITY FOLDER, FSOPS TAKES BACK OUT. Unlike the database above, the
;   in-game panel under <Community>\fsops-panel is not the player's data - it is FSOps' own copied
;   and generated output, reproducible at any time by pressing Install again. So removing it is not
;   opt-in the way the database is: see [UninstallRun] below, which runs PanelUninstallCommand
;   automatically and silently, before anything else in this uninstall happens.

#if VER < EncodeVer(6,3,0)
  #error FSOps.iss needs Inno Setup 6.3 or newer (ArchitecturesAllowed=x64compatible, DownloadTemporaryFile).
#endif

; ---------------------------------------------------------------------------------------------
; Inputs. Each can be overridden from the command line, e.g. ISCC /DAppVersion=0.2.0
; ---------------------------------------------------------------------------------------------

; Keep in step with <Version> in Directory.Build.props. build-installer.ps1 reads it from there and
; passes it in, so the default below only applies to a hand-run ISCC.
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

; The output of scripts\publish.ps1. Installed verbatim - that script is the authority on what the
; layout is, and a second opinion here is just a way for the two to drift apart.
#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\artifacts\publish"
#endif

#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\artifacts\installer"
#endif

#define AppName "FSOps"
#define AppExeName "FSOps.Desktop.exe"
#define AppPublisher "FSOps"
#define AppUrl "https://github.com/NW11NGW/FSOps"

; AddBackslash rather than SourcePath + "\": whether SourcePath already ends in a separator is not
; worth depending on, and a doubled backslash breaks FileExists below while still compiling.
#define IconFile AddBackslash(SourcePath) + "..\src\FSOps.Desktop\fsops.ico"

; ---------------------------------------------------------------------------------------------
; Compile-time checks on the publish output.
;
; Without these, a forgotten or half-finished publish produces an installer that compiles cleanly
; and then installs a broken app - the failure surfaces on a user's machine instead of on the build
; machine. Each file below is something the app resolves at runtime and cannot start, render, or
; feature-complete without, so absence is worth failing the build over.
; ---------------------------------------------------------------------------------------------
#if !FileExists(PublishDir + "\FSOps.Desktop.exe")
  #error Publish output not found. Run scripts\publish.ps1 first (or pass /DPublishDir=...).
#endif
#if !FileExists(PublishDir + "\FSOps.Server.exe")
  #error The publish has no FSOps.Server.exe - the shell has no server to start.
#endif
#if !FileExists(PublishDir + "\WebView2Loader.dll")
  #error The publish has no WebView2Loader.dll - the shell cannot create a web view without it.
#endif
#if !FileExists(PublishDir + "\System.Private.CoreLib.dll")
  #error The publish is not self-contained - it would need .NET installed on the user's machine.
#endif
#if !FileExists(PublishDir + "\economy-config.json")
  #error The publish has no economy-config.json - every fare, cost and demand constant lives there.
#endif
#if !FileExists(PublishDir + "\wwwroot\index.html")
  #error The publish has no SPA in wwwroot - every page would be the "UI not built yet" fallback.
#endif
#if !FileExists(PublishDir + "\PanelTemplate\manifest.json")
  #error The publish has no in-game panel package under PanelTemplate.
#endif
; The compiled .spb is what registers the toolbar button with MSFS. The panel installs and works
; without it in the sense that files land in the Community folder, but no button ever appears - a
; failure that looks like the feature simply not existing.
#if !FileExists(PublishDir + "\PanelTemplate\InGamePanels\FSOpsPanel.spb")
  #error The publish has no PanelTemplate\InGamePanels\FSOpsPanel.spb - the MSFS toolbar button would never appear.
#endif
#if !FileExists(IconFile)
  #error fsops.ico was not found next to the FSOps.Desktop project.
#endif

[Setup]
; Never change AppId: it is how Windows recognises an existing FSOps and upgrades it in place
; instead of leaving two entries in Apps & features.
AppId={{913A792D-F1BF-4FEE-B4CB-3E7D54BFBAFE}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; Per-user throughout. PrivilegesRequired=lowest means Setup never elevates, and every {auto*}
; constant below therefore resolves to the current user's own folders.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; x64-only, because SimConnect's native DLL only ships as x64. x64compatible rather than x64os so
; the app also installs on ARM64 Windows, where it runs under x64 emulation.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; MSFS 2024 itself requires a newer Windows than this, so the floor is really about giving anyone
; on an ancient build a clear message rather than a mysterious failure.
MinVersion=10.0.17763

; The installer is unsigned. Restart Manager is still worth having: it detects FSOps.Desktop.exe or
; FSOps.Server.exe holding files open and offers to close them, instead of failing the copy with an
; "in use" error the user has to decode.
CloseApplications=yes
RestartApplications=no

OutputDir={#OutputDir}
; Must start with "FSOps-Setup" and end in ".exe": UpdateChecker.SelectInstallerAsset looks for
; exactly that prefix when it decides which GitHub release asset is the installer, and the release's
; checksum sidecar is this name plus ".sha256".
OutputBaseFilename=FSOps-Setup-{#AppVersion}
SetupIconFile={#IconFile}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; A published build is around 190 MB across 500+ files. When an install goes wrong the log is the
; only evidence anyone has, and it costs nothing to always write one.
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole publish folder, verbatim and recursive. Listing individual files here would mean this
; script and publish.ps1 both describe the layout, and the day they disagree is the day an install
; is silently missing an asset.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Second line of defence only - see MaybeOfferWebView2. Skipped entirely unless the runtime was
; found to be missing AND the user agreed AND the download actually succeeded. Inno does not treat
; a non-zero exit code from [Run] as a failure, so a bootstrapper that cannot reach Microsoft leaves
; the install successful, exactly as intended.
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; \
  StatusMsg: "Installing the Microsoft Edge WebView2 runtime..."; \
  Flags: waituntilterminated runhidden skipifdoesntexist; Check: ShouldInstallWebView2

Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Removes the in-game panel FSOps installed into the player's chosen MSFS Community folder. The
; player never put those files there - FSOps did, at their request, from Settings - so FSOps cleans
; them up rather than leaving a folder behind forever with MSFS still showing a toolbar button for
; an app that is gone.
;
; This section's entries run "as the first step of uninstallation" (Inno's own documented wording),
; which is exactly the ordering this needs: {app}\FSOps.Server.exe still exists, and so does the
; database PanelUninstallCommand reads the Community folder path from - the optional prompt to
; delete that database (see CurUninstallStepChanged below) fires much later, at usPostUninstall.
;
; PanelUninstallCommand is the whole implementation, deliberately: this line is "call it and don't
; touch anything else" so the interesting logic lives in C#, where it can be unit tested, rather
; than in Pascal, where it cannot. It never fails the uninstall - every I/O problem it can hit (a
; locked file, a missing or moved Community folder, a folder FSOps didn't create) is caught there
; and turned into "leave it, say so, carry on", never an exception or a non-zero exit code.
;
; Runs unconditionally, including during a silent uninstall - unlike the data-directory prompt
; below there is no Yes/No question here to ask: the panel is FSOps' own generated output, never
; anything the player put there themselves, so tidying it up automatically costs nothing the way
; deleting their airline data would. skipifdoesntexist covers the (currently impossible, but cheap
; to guard) case of a previous uninstall step having already removed the exe.
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-panel"; \
  Flags: runhidden waituntilterminated skipifdoesntexist logoutput

[Code]
const
  { Microsoft's permanent short link to the Evergreen WebView2 bootstrapper (about 2 MB). The same
    URL is in WebView2Runtime.EvergreenBootstrapperUrl on the .NET side. }
  WebView2BootstrapperUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2BootstrapperFile = 'MicrosoftEdgeWebview2Setup.exe';

  { EdgeUpdate's client key for the Evergreen runtime. A "pv" value under it is the documented way
    to ask whether the runtime is present. }
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

  FSOpsDataDirVariable = 'FSOPS_DATA_DIR';

var
  WebView2BootstrapperReady: Boolean;
  WebView2AlreadyOffered: Boolean;

{ EdgeUpdate leaves its client key behind with pv set to 0.0.0.0 after the runtime is removed, so
  the key existing is not the same as the runtime being installed. }
function IsRealWebView2Version(const Version: String): Boolean;
begin
  Result := (Version <> '') and (Version <> '0.0.0.0');
end;

{ True when the Evergreen WebView2 runtime is installed for this machine or this user.

  All three registry views are checked on purpose. A machine-wide runtime registers under the 32-bit
  view - HKLM32 here is literally HKLM\SOFTWARE\WOW6432Node\... on 64-bit Windows - some builds also
  write the 64-bit view, and a runtime installed by a non-elevated bootstrapper, which is exactly
  what this installer triggers, lands in HKCU instead. Missing any one of them would have us offer
  to reinstall a runtime that is already there.

  The lookups are written out rather than factored behind a helper taking the root key, because the
  root key parameter is typed HKEY rather than Integer and there is no reason to find out the hard
  way whether that distinction matters. The nesting likewise avoids depending on whether "and"
  short-circuits: HKLM64 is only ever touched after IsWin64 has actually returned True. }
function IsWebView2Installed: Boolean;
var
  Version: String;
begin
  Result := False;

  if RegQueryStringValue(HKLM32, WebView2ClientKey, 'pv', Version) then
    if IsRealWebView2Version(Version) then
    begin
      Result := True;
      Exit;
    end;

  if IsWin64 then
    if RegQueryStringValue(HKLM64, WebView2ClientKey, 'pv', Version) then
      if IsRealWebView2Version(Version) then
      begin
        Result := True;
        Exit;
      end;

  if RegQueryStringValue(HKCU, WebView2ClientKey, 'pv', Version) then
    if IsRealWebView2Version(Version) then
      Result := True;
end;

function ShouldInstallWebView2: Boolean;
begin
  Result := WebView2BootstrapperReady;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

{ Offers the WebView2 runtime when, and only when, it is genuinely absent.

  This is a nicety, not a prerequisite, and the code is shaped to keep it that way. WebView2 ships
  with Windows 11 and reaches Windows 10 through Edge updates, so on almost every machine this does
  nothing at all and the user never sees it. Where it is missing - LTSC, some managed images - FSOps
  still works: the shell detects the absence before it builds a window and opens the SPA in the
  default browser instead. So the runtime buys a nicer window, and nothing more.

  That is why the user is asked rather than told, why declining is a normal outcome, and why every
  failure path below - no internet, a cancelled download, a bootstrapper that will not run - ends
  with the install carrying on regardless. There is no branch here that can fail the install. }
procedure MaybeOfferWebView2;
var
  DownloadPage: TDownloadWizardPage;
begin
  { Going Back from the Ready page and forward again must not re-ask a question already answered. }
  if WebView2AlreadyOffered then
    Exit;

  WebView2BootstrapperReady := False;

  if IsWebView2Installed then
  begin
    WebView2AlreadyOffered := True;
    Exit;
  end;

  WebView2AlreadyOffered := True;

  if MsgBox(
       'FSOps can run in its own window using the Microsoft Edge WebView2 runtime, which is not '
       + 'installed on this PC.' + #13#10#13#10
       + 'Download and install it now? It is about 2 MB, comes from Microsoft, and needs an '
       + 'internet connection.' + #13#10#13#10
       + 'You can safely choose No. FSOps will open in your default browser instead and every '
       + 'feature works the same.',
       mbConfirmation, MB_YESNO) <> IDYES then
    Exit;

  DownloadPage := CreateDownloadPage(
    'Microsoft Edge WebView2 runtime',
    'Downloading the runtime from Microsoft. You can skip this at any time.',
    @OnDownloadProgress);
  try
    DownloadPage.Clear;
    DownloadPage.Add(WebView2BootstrapperUrl, WebView2BootstrapperFile, '');
    DownloadPage.Show;
    try
      DownloadPage.Download;
      WebView2BootstrapperReady := True;
    except
      { Offline, blocked by a proxy, or aborted from the page's own button. None of those are
        reasons to stop installing FSOps. }
      Log('WebView2 bootstrapper download did not complete: ' + GetExceptionMessage);
      MsgBox(
        'The WebView2 runtime could not be downloaded, so it has been skipped.' + #13#10#13#10
        + 'FSOps will install normally and open in your default browser.',
        mbInformation, MB_OK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  { On the Ready page, so it happens after the user has committed to installing but before any file
    is copied - and so the question is never asked of someone who then cancels. }
  if CurPageID = wpReady then
    MaybeOfferWebView2;

  Result := True;
end;

{ Uninstall. The program goes; the airline stays; the panel is already gone by the time this runs.

  Inno removes what it installed into the application folder and nothing else, so the default path
  already leaves LocalAppData\FSOps alone. The prompt below is the only way that directory can ever
  be deleted by this installer, and it is deliberately hard to trigger by accident: it is opt-in,
  the default button is No, it names the exact folder and what is inside it, and it does not appear
  at all during a silent uninstall or when FSOPS_DATA_DIR has moved the data somewhere this
  installer never knew about. Someone who wants a genuinely clean removal can have one; nobody loses
  an airline to a reflexive Enter key.

  The in-game panel is a separate, non-optional cleanup step that has already happened by the time
  usPostUninstall (below) fires - see [UninstallRun] and PanelUninstallCommand. That ordering is
  deliberate: panel removal needs the database this prompt might be about to delete, so it has to
  run first, and it does, because [UninstallRun] entries execute before usUninstall even begins. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  { No user present to answer, so the safe answer is the only answer. }
  if UninstallSilent then
    Exit;

  { The data lives wherever FSOPS_DATA_DIR points. Falling back to the LocalAppData default in that
    case would delete a directory that is not the one in use. }
  if GetEnv(FSOpsDataDirVariable) <> '' then
    Exit;

  DataDir := ExpandConstant('{localappdata}\FSOps');
  if not DirExists(DataDir) then
    Exit;

  if MsgBox(
       'Also delete your FSOps airline data?' + #13#10#13#10
       + DataDir + #13#10#13#10
       + 'That folder holds your airline, fleet, routes, pilots, flight history and ledger. '
       + 'Deleting it cannot be undone, and reinstalling FSOps will not bring it back.' + #13#10#13#10
       + 'Choose No to keep it. Your airline will be waiting exactly as you left it if you ever '
       + 'install FSOps again.',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
    Exit;

  if not DelTree(DataDir, True, True, True) then
    MsgBox(
      'Some files could not be removed, most likely because FSOps is still running.' + #13#10#13#10
      + 'You can delete this folder yourself once it has closed:' + #13#10 + DataDir,
      mbInformation, MB_OK);
end;
