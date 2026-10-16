# Bumps the submodules to the tip of their tracked branch.
#
# A bump resets the submodule working trees, which also drops the local patches
# under patches/, so re-apply them right away. Directory.Build.targets would do
# it on the next build anyway; doing it here keeps `git status` honest.
git submodule update --remote --recursive

& (Join-Path $PSScriptRoot 'scripts\Apply-SubmodulePatches.ps1')
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
