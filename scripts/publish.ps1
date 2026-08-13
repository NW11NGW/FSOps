<#
.SYNOPSIS
    Produces a self-contained FSOps build that runs on a PC with no .NET installed at all.

.DESCRIPTION
    Builds the web UI, then publishes FSOps.Server and FSOps.Desktop into a single folder as
    self-contained win-x64 applications. The result is the complete product: double-clicking
    FSOps.Desktop.exe in the output folder is exactly what an installed user does.

    Both projects publish into the SAME folder on purpose. The shell finds its server by looking
    next to itself first (see ServerExecutableLocator), and the installer copies this folder
    verbatim, so keeping them together means there is one layout to get right instead of two.

    The script fails loudly rather than producing a half-populated folder. Every asset the app
    resolves at runtime - the built SPA, the world-data seed, the economy config, the WebView2
    loader - is asserted for at the end, because each of them is invisible until a user hits the
    feature that needs it.

.PARAMETER Configuration
    Build configuration. Release unless you are chasing something.

.PARAMETER OutputPath
    Where the published app goes. Defaults to artifacts\publish under the repository root.

.PARAMETER SkipWebBuild
    Reuse whatever is already in src\FSOps.Server\wwwroot instead of running Vite. Only useful
    when iterating on the .NET side; the layout check still refuses an empty wwwroot.

.PARAMETER KeepExistingOutput
    Do not delete the output folder first. Off by default: a stale file left behind by a previous
    publish is exactly the kind of thing that works on the build machine and nowhere else.

.EXAMPLE
    .\scripts\publish.ps1

.EXAMPLE
    .\scripts\publish.ps1 -OutputPath D:\FSOps-release -SkipWebBuild
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputPath,
    [switch]$SkipWebBuild,
    [switch]$KeepExistingOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# SimConnect's native DLL is x64-only, so the whole product is. This is not a parameter.
$RuntimeIdentifier = 'win-x64'

$repoRoot = Split-Path -Parent $PSScriptRoot
$webRoot = Join-Path $repoRoot 'src\fsops-web'
$serverProject = Join-Path $repoRoot 'src\FSOps.Server\FSOps.Server.csproj'
$desktopProject = Join-Path $repoRoot 'src\FSOps.Desktop\FSOps.Desktop.csproj'
$wwwroot = Join-Path $repoRoot 'src\FSOps.Server\wwwroot'

if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'artifacts\publish' }

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Assert-Tool([string]$name, [string]$hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "$name was not found on PATH. $hint"
    }
}

# Windows PowerShell turns anything a native executable writes to stderr into an error record, and
# with $ErrorActionPreference = 'Stop' that becomes a terminating error. Both npm and dotnet write
# ordinary progress and warnings to stderr - a single rollup comment warning was enough to abort
# this script - so native calls are made with the preference relaxed and judged on their exit code,
# which is the only thing that actually means failure.
function Invoke-Native {
    param(
        [Parameter(Mandatory)][scriptblock]$Command,
        [Parameter(Mandatory)][string]$What
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = 0
    try {
        & $Command
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE."
    }
}

# ---------------------------------------------------------------------------------------------
# 1. Tooling
# ---------------------------------------------------------------------------------------------
Write-Step 'Checking build tools'
Assert-Tool 'dotnet' 'Install the .NET 8 SDK from https://dotnet.microsoft.com/download'
Write-Host "    dotnet SDK $(dotnet --version)"

if (-not $SkipWebBuild) {
    Assert-Tool 'npm' 'Install Node.js from https://nodejs.org - the web UI is built with Vite.'
    Write-Host "    npm $(npm --version)"
}

# ---------------------------------------------------------------------------------------------
# 2. Web UI -> src\FSOps.Server\wwwroot
# ---------------------------------------------------------------------------------------------
if ($SkipWebBuild) {
    Write-Step 'Skipping the web build (-SkipWebBuild)'
}
else {
    Write-Step 'Building the web UI'
    Push-Location $webRoot
    try {
        if (-not (Test-Path (Join-Path $webRoot 'node_modules'))) {
            Write-Host '    node_modules missing - running npm ci'
            Invoke-Native { npm ci } 'npm ci'
        }

        Invoke-Native { npm run build } 'npm run build'
    }
    finally {
        Pop-Location
    }
}

# The SPA must exist before the .NET publish runs: wwwroot is globbed when the project is
# evaluated, so publishing first and building the UI afterwards silently ships an empty wwwroot
# and an app whose every page is the "UI not built yet" fallback.
$indexPath = Join-Path $wwwroot 'index.html'
if (-not (Test-Path $indexPath)) {
    throw "No SPA found at $indexPath. Run 'npm run build' in src\fsops-web, or drop -SkipWebBuild."
}

# ---------------------------------------------------------------------------------------------
# 3. Publish
# ---------------------------------------------------------------------------------------------
if ((Test-Path $OutputPath) -and -not $KeepExistingOutput) {
    Write-Step "Clearing $OutputPath"
    Remove-Item -Recurse -Force $OutputPath
}
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
$OutputPath = (Resolve-Path $OutputPath).Path

function Publish-Project([string]$project, [string]$label) {
    Write-Step "Publishing $label ($Configuration, $RuntimeIdentifier, self-contained)"
    Invoke-Native {
        dotnet publish $project `
            --configuration $Configuration `
            --runtime $RuntimeIdentifier `
            --self-contained true `
            --output $OutputPath `
            --nologo `
            -p:PublishSingleFile=false `
            -p:DebugType=none
    } "dotnet publish ($label)"
}

# Server first, shell second. They share the same .NET runtime assemblies and the versions are
# identical, but publishing the thing a user double-clicks last means its own closure is the one
# that wins any tie.
Publish-Project $serverProject 'FSOps.Server'
Publish-Project $desktopProject 'FSOps.Desktop'

# ---------------------------------------------------------------------------------------------
# 4. Verify the layout
# ---------------------------------------------------------------------------------------------
Write-Step 'Verifying the published layout'

$requiredFiles = @(
    @{ Path = 'FSOps.Desktop.exe';   Why = 'the desktop app the user launches' },
    @{ Path = 'FSOps.Server.exe';    Why = 'the server the shell starts' },
    @{ Path = 'WebView2Loader.dll';  Why = 'WebView2 cannot initialise without it' },
    @{ Path = 'economy-config.json'; Why = 'every fare, cost and demand constant' },
    @{ Path = 'wwwroot\index.html';  Why = 'the SPA - without it every page is the not-built fallback' }
)

$problems = @()
foreach ($required in $requiredFiles) {
    if (-not (Test-Path (Join-Path $OutputPath $required.Path))) {
        $problems += "MISSING $($required.Path) - $($required.Why)"
    }
}

# The world-data seed is a folder of gzipped CSVs; the count matters more than any one name.
$seedCount = @(Get-ChildItem -Path (Join-Path $OutputPath 'data') -Filter '*.gz' -ErrorAction SilentlyContinue).Count
if ($seedCount -eq 0) {
    $problems += 'MISSING data\*.gz - the world-data seed, without which no airport exists'
}

# A .NET runtime must be carried in the folder, or "no .NET installed" is not actually satisfied.
if (-not (Test-Path (Join-Path $OutputPath 'System.Private.CoreLib.dll'))) {
    $problems += 'MISSING System.Private.CoreLib.dll - this build is not self-contained'
}

# index.html must be the real SPA, not the placeholder: the server returns HTTP 200 for both.
$indexText = Get-Content (Join-Path $OutputPath 'wwwroot\index.html') -Raw -ErrorAction SilentlyContinue
if (-not $indexText -or $indexText -notmatch 'id="root"') {
    $problems += 'wwwroot\index.html does not contain the SPA mount point (id="root")'
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    throw "The published folder is incomplete - $($problems.Count) problem(s) above."
}

$sizeMb = [math]::Round((Get-ChildItem $OutputPath -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
$fileCount = @(Get-ChildItem $OutputPath -Recurse -File).Count

Write-Host '    all required files present' -ForegroundColor Green
Write-Host ''
Write-Host "Published to $OutputPath" -ForegroundColor Green
Write-Host "  $fileCount files, $sizeMb MB, self-contained $RuntimeIdentifier"
Write-Host "  world-data seed: $seedCount gzipped CSVs"
Write-Host ''
Write-Host '  Launch it with:'
Write-Host "    $(Join-Path $OutputPath 'FSOps.Desktop.exe')"
