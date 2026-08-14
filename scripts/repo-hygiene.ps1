<#
.SYNOPSIS
    Refuses to let this repository ship something it should not contain.

.DESCRIPTION
    The scan that ran before every push looked for dangerous CONTENT - secrets, keys, databases -
    and never asked the simpler question of whether a file had any business existing. An empty file
    named "check" sat in the root of this public repository for two days as a result: it held
    nothing, revealed nothing, and no rule was looking for it.

    So this checks three things:

      1. Files that should not exist at all - stray files in the root, build and editor output,
         scratch and temporary files, and empty files with no plausible purpose.
      2. Content that must never ship - credentials, private keys, connection strings, database
         files, and any reference to the tooling used to write this, which a public repository must
         not carry.
      3. Local-only files - anything excluded via .git/info/exclude that has been committed anyway.

    Two design rules matter more than coverage here. A check nobody trusts gets switched off, so
    every rule explains itself when it fires: what was found, why it matters, and what to do. And a
    finding can be accepted deliberately and visibly, by name, without disabling the rule for
    everything else - see $Allowed below.

.PARAMETER Fix
    Reports what would be removed without removing it. There is no automatic deletion: a script
    that deletes files in a repository is a worse problem than the one it solves.

.EXAMPLE
    pwsh scripts/repo-hygiene.ps1
#>
[CmdletBinding()]
param(
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    $findings = New-Object System.Collections.Generic.List[object]

    function Add-Finding {
        param([string]$Rule, [string]$Path, [string]$Why, [string]$What)
        $findings.Add([pscustomobject]@{ Rule = $Rule; Path = $Path; Why = $Why; What = $What })
    }

    # Deliberate, visible exceptions. A path here is one somebody decided was fine, in public, with
    # a reason - which is the whole difference between an exception and a rule quietly weakened.
    $Allowed = @{
        # 'some/path' = 'why this one is genuinely fine'
    }

    # Only tracked files are checked. An untracked scratch file in a working tree is nobody's
    # business; one that has been committed is everybody's.
    $tracked = @(git ls-files)

    # --- 1. Files that should not exist -------------------------------------------------------
    # The root is the only directory where a stray file is invisible in review, because everything
    # there looks like it belongs. Anything not on this list has to justify itself.
    $rootAllowed = @(
        '.editorconfig', '.gitattributes', '.gitignore', 'Directory.Build.props', 'FSOps.sln',
        'LICENSE', 'README.md', 'global.json', 'NuGet.config'
    )

    foreach ($path in $tracked) {
        if ($Allowed.ContainsKey($path)) { continue }

        $isRoot = $path -notmatch '/'
        if ($isRoot -and $rootAllowed -notcontains $path) {
            Add-Finding 'stray-root-file' $path `
                'A file in the repository root that is not one of the known project files. This is where an accidental redirect or scratch file hides, because everything in the root looks like it belongs.' `
                'Delete it, move it into a directory that explains it, or add it to $rootAllowed in this script if it genuinely belongs.'
        }

        if ($path -match '(^|/)(bin|obj|node_modules|_out|_Temp|\.vs|\.idea)/') {
            Add-Finding 'build-output' $path `
                'Build or tool output. It is regenerated on every build, so committing it only creates conflicts and hides what actually changed.' `
                'Remove it from the index and make sure the directory is ignored.'
        }

        if ($path -match '\.(swp|bak|orig|rej|tmp|temp|log)$' -or $path -match '(^|/)(\.DS_Store|Thumbs\.db|desktop\.ini)$') {
            Add-Finding 'editor-or-os-droppings' $path `
                'An editor backup, merge leftover, log or operating-system file. None of these are part of the project.' `
                'Delete it and add the pattern to .gitignore if it keeps coming back.'
        }

        if ($path -match '\.(db|sqlite|sqlite3|mdf)$') {
            Add-Finding 'database-file' $path `
                'A database file. This project keeps player data outside the repository on purpose, and a committed database can carry real personal data with it.' `
                'Remove it from the index and from history if it was ever pushed.'
        }

        if ((Test-Path $path -PathType Leaf) -and (Get-Item $path).Length -eq 0) {
            Add-Finding 'empty-file' $path `
                'An empty tracked file. Occasionally deliberate, usually the residue of a mistyped command - which is exactly how an empty file called "check" reached this repository and stayed.' `
                'Delete it, or add it to $Allowed with a note saying why an empty file is meant to be there.'
        }
    }

    # --- 2. Content that must never ship ------------------------------------------------------
    # Narrow patterns on purpose. A scan that cries wolf gets switched off, and a switched-off scan
    # protects nothing.
    $contentRules = @(
        @{ Rule = 'private-key'; Pattern = '-----BEGIN (RSA |OPENSSH |EC |PGP )?PRIVATE KEY-----'
           Why = 'A private key. Anything committed here must be treated as compromised, whatever happens next.'
           What = 'Remove it, rotate the key, and rewrite the history that carried it.' },
        @{ Rule = 'aws-key'; Pattern = 'AKIA[0-9A-Z]{16}'
           Why = 'What looks like an AWS access key id.'
           What = 'Remove it and rotate the credential.' },
        @{ Rule = 'connection-string-with-password'; Pattern = '(?i)(password|pwd)\s*=\s*[^\s;''"]{4,}'
           Why = 'A connection string carrying a password. This project needs no such credential, so one appearing is either a real secret or a copied example that will be mistaken for one.'
           What = 'Remove it. If it is genuinely an example, make the value obviously fake.' },
        @{ Rule = 'ai-tooling-reference'; Pattern = '(?i)(co-authored-by:\s*claude|generated with \[?claude|anthropic\.com|\bclaude\.ai\b|superlocalmemory|graphify)'
           Why = 'A reference to the tooling used to write this. The repository is public and carries no such trace by deliberate policy.'
           What = 'Remove the reference. Keep tool paths in .git/info/exclude, never in a tracked file.' },
        # This shipped in 1.0.0 and nobody noticed. An arrow written as UTF-8, read back as
        # Windows-1252 and re-saved, became three stray characters - which the app then rendered to
        # the player in a route message and on the Save button. It survives review because it reads
        # as an encoding quirk in the diff rather than a defect, and no test asserts on a character
        # nobody thought could change. PowerShell is the usual culprit: Set-Content and Out-File
        # default to the ANSI codepage.
        #
        # The pattern is BUILT FROM CODE POINTS rather than written literally, so this file stays
        # pure ASCII. Spelling the sequences out here both trips the rule on itself and, worse,
        # stops PowerShell parsing the script at all - which is how the first attempt at this rule
        # silently turned the whole check into a no-op.
        @{ Rule = 'mojibake'
           Pattern = ('[{0}{1}{2}][{3}-{4}{5}-{6}]' -f `
                [char]0x00C2, [char]0x00C3, [char]0x00E2, `
                [char]0x0080, [char]0x00BF, [char]0x2013, [char]0x20AC)
           Why = 'Text that was written UTF-8, read back as Windows-1252 and re-saved, so a character the player is meant to see has turned into stray ones.'
           What = 'Restore the intended character and save the file as UTF-8. In PowerShell write with [System.IO.File]::WriteAllText and a UTF8Encoding($false); Set-Content and Out-File default to the ANSI codepage.' }
    )

    # Skip binaries and the one file that legitimately contains the patterns: this script.
    $textFiles = $tracked | Where-Object {
        $_ -ne 'scripts/repo-hygiene.ps1' -and
        $_ -notmatch '\.(png|jpg|jpeg|gif|ico|svg|woff2?|ttf|eot|exe|dll|pdb|zip|gz)$' -and
        (Test-Path $_ -PathType Leaf)
    }

    foreach ($rule in $contentRules) {
        $hits = Select-String -Path $textFiles -Pattern $rule.Pattern -List -ErrorAction SilentlyContinue
        foreach ($hit in $hits) {
            $rel = (Resolve-Path $hit.Path).Path.Substring($repoRoot.Length + 1).Replace('\', '/')
            if ($Allowed.ContainsKey($rel)) { continue }
            Add-Finding $rule.Rule "${rel}:$($hit.LineNumber)" $rule.Why $rule.What
        }
    }

    # --- 3. Local-only files that were committed anyway ----------------------------------------
    $excludeFile = Join-Path $repoRoot '.git/info/exclude'
    if (Test-Path $excludeFile) {
        $patterns = Get-Content $excludeFile |
            Where-Object { $_.Trim() -and -not $_.StartsWith('#') } |
            ForEach-Object { $_.Trim().TrimStart('/') }

        foreach ($pattern in $patterns) {
            foreach ($path in $tracked) {
                if ($Allowed.ContainsKey($path)) { continue }
                if ($path -eq $pattern -or $path -like $pattern -or $path -like "$pattern/*") {
                    Add-Finding 'committed-local-only-file' $path `
                        "Excluded via .git/info/exclude as '$pattern', which means it is meant to stay on this machine - but it is tracked, so it is already public." `
                        'Remove it from the index, and from history if it has been pushed.'
                }
            }
        }
    }

    # --- Report --------------------------------------------------------------------------------
    if ($findings.Count -eq 0) {
        Write-Host "repo-hygiene: clean - $($tracked.Count) tracked files checked." -ForegroundColor Green
        exit 0
    }

    Write-Host ""
    Write-Host "repo-hygiene found $($findings.Count) thing(s) that should not be in this repository:" -ForegroundColor Red
    foreach ($group in $findings | Group-Object Rule) {
        Write-Host ""
        Write-Host "  [$($group.Name)]" -ForegroundColor Yellow
        foreach ($f in $group.Group) {
            Write-Host "    $($f.Path)"
        }
        Write-Host "    why:  $($group.Group[0].Why)" -ForegroundColor DarkGray
        Write-Host "    what: $($group.Group[0].What)" -ForegroundColor DarkGray
    }
    Write-Host ""
    if ($Fix) {
        Write-Host "Nothing was deleted. A script that removes files from a repository unattended is a worse problem than the one it solves - the list above is the work." -ForegroundColor DarkGray
    }
    exit 1
}
finally {
    Pop-Location
}
