<#
.SYNOPSIS
    Applies the local patches under patches/ to their submodules.

.DESCRIPTION
    A submodule sometimes has to stay pinned to a plain upstream commit, which
    means a fix we need on top of it cannot be a commit of our own. Those fixes
    live in patches/<submodule>/ as .patch files instead.

    The script is idempotent: a patch that is already present in the working
    tree is skipped, so it is safe to run before every build. It is invoked by
    Directory.Build.targets, which keeps a local `dotnet build` and CI on the
    same sources.

    Exits non-zero when a patch no longer applies, so a submodule bump cannot
    silently drop a fix.
#>
[CmdletBinding()]
param(
    [string] $RepositoryRoot
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not populated yet while parameter defaults are evaluated on
# Windows PowerShell 5.1, so the repository root is resolved here instead.
if (-not $RepositoryRoot) {
    $scriptDirectory = $PSScriptRoot
    if (-not $scriptDirectory) {
        $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    }

    $RepositoryRoot = Split-Path -Parent $scriptDirectory
}

# Native commands write to stderr on the expected "not applied yet" probe, which
# trips ErrorActionPreference. Run them through this helper and read the code.
function Invoke-Git {
    param([string[]] $Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git @Arguments 2>&1
        return [pscustomobject]@{ Code = $LASTEXITCODE; Output = $output }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Stop-WithError {
    param([string] $Message)

    [Console]::Error.WriteLine($Message)
    exit 1
}

function Test-PatchApplied {
    param([string] $SubmodulePath, [string] $Patch)

    # Reverse-applying only succeeds when the patch is already in the tree.
    return (Invoke-Git @('-C', $SubmodulePath, 'apply', '--reverse', '--check', '--whitespace=nowarn', '--', $Patch)).Code -eq 0
}

function Test-PatchApplicable {
    param([string] $SubmodulePath, [string] $Patch)

    return (Invoke-Git @('-C', $SubmodulePath, 'apply', '--check', '--whitespace=nowarn', '--', $Patch)).Code -eq 0
}

function Invoke-PatchApply {
    param([string] $SubmodulePath, [string] $Patch)

    return (Invoke-Git @('-C', $SubmodulePath, 'apply', '--whitespace=nowarn', '--', $Patch))
}

$patchesRoot = Join-Path $RepositoryRoot 'patches'
if (-not (Test-Path -LiteralPath $patchesRoot)) {
    return
}

$appliedCount = 0
$presentCount = 0

foreach ($submoduleDir in Get-ChildItem -LiteralPath $patchesRoot -Directory) {
    $submoduleName = $submoduleDir.Name
    $submodulePath = Join-Path $RepositoryRoot $submoduleName

    if (-not (Test-Path -LiteralPath (Join-Path $submodulePath '.git'))) {
        Write-Warning "patches/$submoduleName is present but submodule '$submoduleName' is not checked out. Skipping."
        continue
    }

    $head = (Invoke-Git @('-C', $submodulePath, 'rev-parse', '--short', 'HEAD')).Output

    foreach ($patchFile in Get-ChildItem -LiteralPath $submoduleDir.FullName -Filter '*.patch' | Sort-Object Name) {
        $patch = $patchFile.FullName
        $label = "patches/$submoduleName/$($patchFile.Name)"

        # A single build can reach the same submodule through more than one
        # reference path and run this script twice, so a patch may be applied by
        # a sibling process between the check and the apply. Retry rather than
        # fail on that.
        $outcome = 'failed'
        $lastOutput = $null

        for ($attempt = 1; $attempt -le 3; $attempt++) {
            if (Test-PatchApplied -SubmodulePath $submodulePath -Patch $patch) {
                $outcome = 'present'
                break
            }

            if (Test-PatchApplicable -SubmodulePath $submodulePath -Patch $patch) {
                $result = Invoke-PatchApply -SubmodulePath $submodulePath -Patch $patch
                if ($result.Code -eq 0) {
                    $outcome = 'applied'
                    break
                }

                $lastOutput = $result.Output
            }
            else {
                $lastOutput = (Invoke-Git @('-C', $submodulePath, 'apply', '--check', '--whitespace=nowarn', '--', $patch)).Output
            }

            Start-Sleep -Milliseconds 250
        }

        switch ($outcome) {
            'present' {
                $presentCount++
            }

            'applied' {
                Write-Host "$label -> $submoduleName @ $head"
                $appliedCount++
            }

            default {
                Stop-WithError @"
$label does not apply to $submoduleName (currently at $head).
$($lastOutput -join [Environment]::NewLine)
If upstream has fixed this, delete the patch file. Otherwise rebase it onto the new commit.
"@
            }
        }
    }
}

if ($appliedCount -gt 0) {
    Write-Host "Submodule patches: $appliedCount applied, $presentCount already present."
}
