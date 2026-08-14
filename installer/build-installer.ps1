<#
.SYNOPSIS
    Compiles the FSOps installer from the self-contained publish output.

.DESCRIPTION
    Runs scripts\publish.ps1 (unless told not to), then compiles installer\FSOps.iss with Inno
    Setup's command-line compiler, then writes the SHA-256 sidecar that the in-app update checker
    needs.

    The sidecar is not optional. UpdateChecker fetches "<installer name>.sha256" BEFORE it downloads
    anything, and refuses to offer an in-app download at all when it is missing - FSOps ships
    unsigned, so that hash is the only thing distinguishing the author's build from a file off the
    internet. A release that publishes the .exe without the .sha256 silently degrades every user's
    updater to a link to the releases page.

    The version comes from Directory.Build.props rather than being repeated here, because that
    property is also what the running app reports as its own version - and the update checker
    compares the two. If the installer's name said 0.2.0 while the assembly said 0.1.0, every user
    on 0.2.0 would be offered 0.2.0 forever.

.PARAMETER SkipPublish
    Reuse whatever is already in artifacts\publish. The .iss still refuses to compile if that
    folder is missing or incomplete.

.PARAMETER Configuration
    Passed through to publish.ps1.

.PARAMETER OutputPath
    Where the installer is written. Defaults to artifacts\installer.

.PARAMETER IsccPath
    Full path to ISCC.exe if it is not in the usual place or on PATH.

.EXAMPLE
    .\installer\build-installer.ps1

.EXAMPLE
    .\installer\build-installer.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [string]$Configuration = 'Release',
    [string]$OutputPath,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$issPath = Join-Path $PSScriptRoot 'FSOps.iss'
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$propsPath = Join-Path $repoRoot 'Directory.Build.props'

if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'artifacts\installer' }

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

# Windows PowerShell turns native-executable stderr into error records, which under
# $ErrorActionPreference = 'Stop' aborts the script on output that is merely a warning. Exit code is
# the only reliable signal. Same reasoning as publish.ps1.
function Invoke-Native {
    param(
        [Parameter(Mandatory)][scriptblock]$Command,
        [Parameter(Mandatory)][string]$What
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = 0
    try { & $Command } finally { $ErrorActionPreference = $previous }

    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

# ---------------------------------------------------------------------------------------------
# 1. Version, read from the one place it is written down
# ---------------------------------------------------------------------------------------------
Write-Step 'Reading the version from Directory.Build.props'

# SelectSingleNode rather than dotted property access. Directory.Build.props has more than one
# <PropertyGroup> and only one of them carries <Version>, so $props.Project.PropertyGroup is an
# array whose first element has no such property - and under Set-StrictMode that is a terminating
# error ("The property 'Version' cannot be found on this object") rather than a null. XPath asks the
# question that is actually meant: the first Version element anywhere under Project.
[xml]$props = Get-Content $propsPath -Raw
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode) { throw "No <Version> element found in $propsPath." }
$version = $versionNode.InnerText.Trim()
if (-not $version) { throw "The <Version> element in $propsPath is empty." }

# A pre-release version ("1.1.0-beta.1") is allowed, because the development release channel depends
# on it. The suffix has to survive into the assembly, the installer's name and the release tag: FSOps
# compares them as semantic versions, and semver puts 1.1.0-beta.1 BELOW 1.1.0. Building a beta from
# a plain <Version>1.1.0</Version> would therefore produce an app claiming to be 1.1.0 while the
# release said 1.1.0-beta.1, and every beta tester would be told they were ahead of the channel and
# never offered another beta - a development channel that silently stops updating, which nobody would
# report because it looks like no betas were published.
#
# What the suffix must NOT reach is Inno's VersionInfoVersion, which writes the Windows file-version
# resource and is four numbers or nothing. So the two are separated here and passed as separate
# defines: $version is displayed and names the file, $versionInfo is numeric.
if ($version -notmatch '^\d+\.\d+(\.\d+){0,2}(-[0-9A-Za-z.-]+)?$') {
    throw "Version '$version' is not a version this script can build. Expected 1.2.3 or 1.2.3-beta.1."
}

$versionInfo = ($version -split '-', 2)[0]
if ($versionInfo -notmatch '^\d+\.\d+(\.\d+){0,2}$') {
    throw "Version '$version' does not start with a numeric x.y.z, so there is no file-version to stamp."
}

Write-Host "    version $version"
if ($versionInfo -ne $version) {
    Write-Host "    file-version resource $versionInfo (pre-release suffix stripped - it cannot go in a Windows version resource)"
}

# ---------------------------------------------------------------------------------------------
# 2. Inno Setup
# ---------------------------------------------------------------------------------------------
Write-Step 'Locating the Inno Setup compiler'

if (-not $IsccPath) {
    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    $IsccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $IsccPath) {
        $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
        if ($onPath) { $IsccPath = $onPath.Source }
    }
}

if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    throw @"
ISCC.exe was not found. Install Inno Setup 6.3 or newer from https://jrsoftware.org/isdl.php,
or pass -IsccPath with its full path. Looked in:
  C:\Program Files (x86)\Inno Setup 6\ISCC.exe
  C:\Program Files\Inno Setup 6\ISCC.exe
  PATH
"@
}
Write-Host "    $IsccPath"

# ---------------------------------------------------------------------------------------------
# 3. Publish
# ---------------------------------------------------------------------------------------------
if ($SkipPublish) {
    Write-Step 'Skipping the publish (-SkipPublish)'
}
else {
    Write-Step 'Publishing FSOps'
    & (Join-Path $repoRoot 'scripts\publish.ps1') -Configuration $Configuration
}

if (-not (Test-Path (Join-Path $publishDir 'FSOps.Desktop.exe'))) {
    throw "No publish output at $publishDir. Run scripts\publish.ps1, or drop -SkipPublish."
}

# ---------------------------------------------------------------------------------------------
# 4. Compile
# ---------------------------------------------------------------------------------------------
Write-Step 'Compiling the installer'

New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
$OutputPath = (Resolve-Path $OutputPath).Path

Invoke-Native {
    & $IsccPath `
        "/DAppVersion=$version" `
        "/DVersionInfo=$versionInfo" `
        "/DPublishDir=$publishDir" `
        "/DOutputDir=$OutputPath" `
        $issPath
} 'ISCC'

$installerPath = Join-Path $OutputPath "FSOps-Setup-$version.exe"
if (-not (Test-Path $installerPath)) {
    throw "ISCC reported success but $installerPath does not exist."
}

# ---------------------------------------------------------------------------------------------
# 5. Checksum sidecar
# ---------------------------------------------------------------------------------------------
Write-Step 'Writing the SHA-256 sidecar'

$hash = (Get-FileHash -Path $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$installerName = Split-Path $installerPath -Leaf
$checksumPath = "$installerPath.sha256"

# The sha256sum layout ("<hash>  <filename>"). UpdateChecker.ParseSha256 also accepts a bare hash
# and PowerShell's upper-case Get-FileHash output, but this form is the one a person can verify with
# any standard tool.
"$hash  $installerName" | Out-File -FilePath $checksumPath -Encoding ascii -NoNewline

$sizeMb = [math]::Round((Get-Item $installerPath).Length / 1MB, 1)

Write-Host ''
Write-Host "Installer built" -ForegroundColor Green
Write-Host "  $installerPath"
Write-Host "  $sizeMb MB"
Write-Host "  $checksumPath"
Write-Host "  sha256 $hash"
Write-Host ''
Write-Host '  Both files are what a release publishes. The update checker needs the .sha256 to'
Write-Host '  offer an in-app download at all.'
