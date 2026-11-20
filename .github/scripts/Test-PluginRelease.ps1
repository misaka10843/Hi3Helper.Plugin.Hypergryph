#requires -Version 7.0
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Invoke-PluginRelease.ps1" -Action Library
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('PluginReleaseTest-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $sandbox
$checks = 0
function Assert([bool] $Condition, [string] $Message) { $script:checks++; if (!$Condition) { throw $Message } }
function Assert-Throws([scriptblock] $Operation, [string] $Pattern = '') {
    $failed = $false
    try { & $Operation } catch { if ($Pattern -and $_.Exception.Message -notmatch $Pattern) { throw }; $failed = $true }
    Assert $failed 'Expected failure was not raised.'
}
function Commit-File([string] $Path, [string] $Subject) {
    $parent = Split-Path $Path -Parent
    if ($parent) { $null = New-Item -ItemType Directory -Force -Path $parent }
    [IO.File]::WriteAllText((Join-Path $PWD $Path), [Guid]::NewGuid().ToString('N'))
    $null = Invoke-Git @('add', '--', $Path)
    $null = Invoke-Git @('commit', '-m', $Subject)
}
function New-PublishFixture([string] $Path, [string] $PluginShort) {
    $null = New-Item -ItemType Directory -Force -Path $Path
    [IO.File]::WriteAllText((Join-Path $Path "$PluginShort.dll"), 'runtime')
    [IO.File]::WriteAllText((Join-Path $Path 'manifest.json'), (@{ MainLibraryName = "$PluginShort.dll"; Assets = @(@{ FilePath = "$PluginShort.dll" }) } | ConvertTo-Json -Depth 4))
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $Path "$PluginShort.zip"), [IO.Compression.ZipArchiveMode]::Create)
    try { $null = $zip.CreateEntry("$PluginShort.dll") } finally { $zip.Dispose() }
}

Push-Location $sandbox
try {
    foreach ($value in @('1.2.3', '1.3.1-pre.1', '0.0.1+build.23')) { Assert-Version $value; $checks++ }
    foreach ($value in @('1.2', '01.2.3', '1.2.3-pre..1', '1.2.3-01', '1.2.3;echo bad')) { Assert-Throws { Assert-Version $value } }
    $Selection = 'all'; $EndfieldVersion = '1.2.0'; $ArknightsVersion = '2.0.0'
    $plan = New-Plan
    Assert (@($plan | ConvertFrom-Json).Count -eq 2) 'Both plugins must be planned.'
    $Selection = 'Hi3Helper.Plugin.Endfield'
    Assert (@((New-Plan) | ConvertFrom-Json).Count -eq 1) 'Single plugin selection failed.'
    $EndfieldVersion = ''; Assert-Throws { New-Plan }
    Assert-Throws { Read-Plan '[{"short":"../outside","name":"x","version":"1.0.0"}]' }
    Assert-Throws { Read-Plan '[]' }
    Assert-Throws { Read-Plan '[{"short":"Endfield","name":"Hi3Helper.Plugin.Endfield","version":"1.0.0"},{"short":"Endfield","name":"Hi3Helper.Plugin.Endfield","version":"1.0.0"}]' }

    $null = Invoke-Git @('init', '-b', 'main')
    $null = Invoke-Git @('config', 'user.name', 'Release Test')
    $null = Invoke-Git @('config', 'user.email', 'release-test@example.invalid')
    $null = Invoke-Git @('config', 'tag.gpgSign', 'false')
    $null = Invoke-Git @('config', 'commit.gpgSign', 'false')
    Commit-File 'initial.txt' 'initial commit'
    $firstNotes = New-Notes 'Endfield' '1.0.0' 'HEAD'
    Assert ($firstNotes -match 'initial commit') 'First release must include history without tags.'
    $null = Invoke-Git @('tag', 'Endfield@v1.0.0'); $null = Invoke-Git @('tag', 'Arknights@v1.0.0')
    Commit-File 'Hi3Helper.Plugin.Endfield/old.cs' 'feat(endfield): previous Endfield release'
    $null = Invoke-Git @('tag', 'Endfield@v1.1.0')
    Commit-File 'Hi3Helper.Plugin.Arknights/old.cs' 'feat(arknights): previous Arknights release'
    $null = Invoke-Git @('tag', 'Arknights@v1.1.0')
    Commit-File 'Hi3Helper.Hypergryph.Shared/common.cs' 'feat (settings): shared configuration'
    Commit-File 'Hi3Helper.Plugin.Endfield/new.cs' 'feat (endfield): new graphics settings'
    Commit-File 'Hi3Helper.Hypergryph.Yostar/api.cs' 'fix(yostar): repair protocol'
    Commit-File 'version.txt' 'feat (release): bump plugin versions [skip ci]'
    $null = Invoke-Git @('tag', 'pre-release')
    $endNotes = New-Notes 'Endfield' '1.2.0' 'HEAD'
    $arkNotes = New-Notes 'Arknights' '1.2.0' 'HEAD'
    Assert ($endNotes -match 'new graphics settings' -and $endNotes -match 'shared configuration') 'Spaced feat subjects were lost.'
    Assert ($endNotes -notmatch 'previous Arknights|repair protocol|bump plugin versions') 'Endfield notes included unrelated changes.'
    Assert ($endNotes -match 'Endfield@v1.1.0') 'Endfield tag boundary was not isolated.'
    Assert ($arkNotes -match 'repair protocol' -and $arkNotes -match 'shared configuration' -and $arkNotes -notmatch 'new graphics settings') 'Arknights paths were not isolated.'
    Assert ($arkNotes -match 'Arknights@v1.1.0') 'Arknights tag boundary was not isolated.'
    $null = Invoke-Git @('tag', 'Endfield@v1.2.0')
    Assert ((New-Notes 'Endfield' '1.2.0' 'HEAD') -match 'new graphics settings') 'Rerunning the same tag lost its changelog.'
    $null = Invoke-Git @('tag', '-a', 'Arknights@v1.2.0', '-m', 'Annotated test tag')
    $Selection = 'all'; $EndfieldVersion = '1.2.0'; $ArknightsVersion = '1.2.0'
    $tagPlan = New-Plan
    Assert-StableTags $tagPlan 'HEAD' '.'
    $checks++
    Commit-File 'next.txt' 'fix: next change'
    Assert-Throws { New-Notes 'Endfield' '1.2.0' 'HEAD' }
    Assert-Throws { Assert-StableTags $tagPlan 'HEAD' '.' } 'Stable tag'
    $EndfieldVersion = '1.3.0'
    Assert-Throws { Assert-StableTags (New-Plan) 'HEAD' '.' } 'Arknights@v1.2.0'
    $ArknightsVersion = '1.3.0'
    Assert-StableTags (New-Plan) 'HEAD' '.'
    $checks++

    $artifacts = Join-Path $sandbox 'artifacts'
    $target = Join-Path $sandbox 'target'
    foreach ($plugin in $plugins) {
        $shortName = $plugin.short
        $source = Join-Path $sandbox "publish-$shortName"
        New-PublishFixture $source $shortName
        Stage-Payload $source (Join-Path $artifacts "release-$shortName/$shortName") $shortName
        $owned = Join-Path $target $shortName
        $null = New-Item -ItemType Directory -Force -Path (Join-Path $owned 'build')
        [IO.File]::WriteAllText((Join-Path $owned 'old.pdb'), 'old symbols')
        [IO.File]::WriteAllText((Join-Path $owned 'build/old.zip'), 'obsolete package')
        [IO.File]::WriteAllText((Join-Path $owned 'README.md'), 'preserve')
    }
    Collect-Payloads $artifacts $target $plan
    foreach ($plugin in $plugins) {
        $path = Join-Path $target $plugin.short
        Assert (Test-Path -LiteralPath (Join-Path $path "$($plugin.short).dll")) 'Combined payload lost a plugin.'
        Assert (!(Test-Path -LiteralPath (Join-Path $path 'old.pdb'))) 'Old symbols were retained.'
        Assert (!(Test-Path -LiteralPath (Join-Path $path 'build/old.zip'))) 'Obsolete ZIP retained.'
        Assert ([IO.File]::ReadAllText((Join-Path $path 'README.md')) -eq 'preserve') 'Unrelated target file changed.'
    }
    $payload = Join-Path $artifacts 'release-Endfield/Endfield'
    [IO.File]::WriteAllText((Join-Path $payload 'extra.pdb'), 'symbols')
    Assert-Throws { Assert-Payload $payload 'Endfield' }
    [IO.File]::Delete((Join-Path $payload 'extra.pdb'))
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $payload 'build/Endfield.zip'), [IO.Compression.ZipArchiveMode]::Update)
    try { $null = $zip.CreateEntry('symbols.pdb.br') } finally { $zip.Dispose() }
    Assert-Throws { Assert-Payload $payload 'Endfield' }
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $payload 'build/Endfield.zip'), [IO.Compression.ZipArchiveMode]::Update)
    try { $zip.GetEntry('symbols.pdb.br').Delete() } finally { $zip.Dispose() }
    $large = [IO.File]::Create((Join-Path $payload 'large.dll'))
    try { $large.SetLength(50MB) } finally { $large.Dispose() }
    Assert-Throws { Assert-Payload $payload 'Endfield' } '50 MiB'
    [IO.File]::Delete((Join-Path $payload 'large.dll'))
    [IO.File]::Delete((Join-Path $payload 'manifest.json'))
    Assert-Throws { Collect-Payloads $artifacts $target $plan }
    Assert (Test-Path -LiteralPath (Join-Path $target 'Arknights/Arknights.dll')) 'Validation failure modified another plugin.'
    foreach ($plugin in $plugins) {
        $project = "$($plugin.name)/$($plugin.name).csproj"
        $null = New-Item -ItemType Directory -Force -Path $plugin.name
        [IO.File]::WriteAllText((Join-Path $PWD $project), "<Project>`r`n<Version>0.0.1</Version>`r`n</Project>", [Text.UTF8Encoding]::new($true))
    }
    Bump-Versions $plan
    foreach ($item in @($plan | ConvertFrom-Json)) {
        $project = "$($item.name)/$($item.name).csproj"
        Assert ([IO.File]::ReadAllText((Join-Path $PWD $project)).Contains("<Version>$($item.version)</Version>")) 'Version was not updated.'
        Assert ([IO.File]::ReadAllBytes((Join-Path $PWD $project))[0] -eq 0xEF) 'Project BOM was changed.'
    }
    Write-Host "PASS: $checks release-tooling checks (versions, tags, changelogs, payloads, aggregation and symbols)."
}
finally {
    Pop-Location
    $resolved = [IO.Path]::GetFullPath($sandbox)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
