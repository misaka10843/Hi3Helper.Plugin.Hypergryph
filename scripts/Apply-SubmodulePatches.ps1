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

        # Reverse-applying only succeeds when the patch is already in the tree.
        $reverse = Invoke-Git @('-C', $submodulePath, 'apply', '--reverse', '--check', '--whitespace=nowarn', '--', $patch)
        if ($reverse.Code -eq 0) {
            $presentCount++
            continue
        }

        $forward = Invoke-Git @('-C', $submodulePath, 'apply', '--check', '--whitespace=nowarn', '--', $patch)
        if ($forward.Code -ne 0) {
            Stop-WithError @"
$label does not apply to $submoduleName (currently at $head).
$($forward.Output -join [Environment]::NewLine)
If upstream has fixed this, delete the patch file. Otherwise rebase it onto the new commit.
"@
        }

        $result = Invoke-Git @('-C', $submodulePath, 'apply', '--whitespace=nowarn', '--', $patch)
        if ($result.Code -ne 0) {
            Stop-WithError "$label failed to apply to $submoduleName.`n$($result.Output -join [Environment]::NewLine)"
        }

        Write-Host "$label -> $submoduleName @ $head"
        $appliedCount++
    }
}

if ($appliedCount -gt 0) {
    Write-Host "Submodule patches: $appliedCount applied, $presentCount already present."
}
