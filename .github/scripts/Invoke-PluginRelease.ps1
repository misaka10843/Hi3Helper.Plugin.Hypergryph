#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Notes', 'Stage', 'Collect', 'Bump', 'CheckTags', 'Library')] [string] $Action,
    [string] $Selection = 'all', [string] $EndfieldVersion, [string] $ArknightsVersion,
    [string] $Short, [string] $Version, [string] $Revision = 'HEAD',
    [string] $Source, [string] $Destination, [string] $Plan
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$plugins = @(
    @{ name = 'Hi3Helper.Plugin.Endfield'; short = 'Endfield' },
    @{ name = 'Hi3Helper.Plugin.Arknights'; short = 'Arknights' }
)

function Invoke-Git([string[]] $Arguments) {
    $result = @(& git @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments[0]) failed ($LASTEXITCODE)." }
    return ,$result
}

function Assert-Version([string] $Value) {
    if ($Value -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-((0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
        throw "Invalid plugin version: $Value"
    }
}

function Get-Plugin([string] $Value) {
    $plugin = $plugins | Where-Object short -CEQ $Value
    if (!$plugin) { throw "Unknown plugin: $Value" }
    return $plugin
}

function Read-Plan([string] $Json) {
    $items = @($Json | ConvertFrom-Json)
    if (!$items.Count) { throw 'Select at least one plugin version.' }
    $seen = @{}
    foreach ($item in $items) {
        $plugin = Get-Plugin $item.short
        Assert-Version $item.version
        if ($item.name -cne $plugin.name -or $seen.ContainsKey($item.short)) { throw 'Invalid or duplicate plugin in release plan.' }
        $seen[$item.short] = $true
    }
    return $items
}

function Assert-StableTags([string] $Json, [string] $Commit, [string] $Remote) {
    $expected = (Invoke-Git @('rev-parse', '--verify', "$Commit^{commit}"))[0]
    foreach ($item in Read-Plan $Json) {
        $ref = "refs/tags/$($item.short)@v$($item.version)"
        $refs = Invoke-Git @('ls-remote', '--tags', $Remote, $ref, "$ref^{}")
        $targets = @{}
        foreach ($line in $refs) {
            $sha, $name = $line -split "`t", 2
            $targets[$name] = $sha
        }
        $target = $targets["$ref^{}"] ?? $targets[$ref]
        if ($target -and $target -ne $expected) {
            throw "Stable tag $ref points to $target, expected $expected."
        }
    }
}

function New-Plan {
    if ($Selection -notin @('all') + @($plugins.name)) { throw "Unsupported selection: $Selection" }
    $versions = @{ Endfield = $EndfieldVersion; Arknights = $ArknightsVersion }
    $items = @()
    foreach ($plugin in $plugins) {
        $value = $versions[$plugin.short]
        if ($Selection -ne 'all' -and $Selection -ne $plugin.name) { continue }
        if (!$value) { continue }
        Assert-Version $value
        $items += @{ name = $plugin.name; short = $plugin.short; version = $value }
    }
    if (!$items.Count) { throw 'Provide a version for at least one selected plugin.' }
    return ConvertTo-Json -InputObject $items -Compress
}

function New-Notes([string] $PluginShort, [string] $PluginVersion, [string] $Head) {
    $null = Get-Plugin $PluginShort
    Assert-Version $PluginVersion
    $sha = (Invoke-Git @('rev-parse', '--verify', "$Head`^{commit}"))[0]
    $tag = "$PluginShort@v$PluginVersion"
    $existing = Invoke-Git @('tag', '--list', $tag)
    if ($existing.Count -and (Invoke-Git @('rev-parse', "$tag`^{commit}"))[0] -ne $sha) {
        throw "Tag $tag already identifies another commit. Choose a new version; existing release tags are not moved."
    }
    $previous = & git describe --tags --match "$PluginShort@v[0-9]*" --exclude $tag --abbrev=0 $sha 2>$null
    $range = if ($LASTEXITCODE -eq 0) { "$previous..$sha" } else { $sha }
    $commits = Invoke-Git @('log', '--no-merges', '--reverse', '--format=%H%x09%s', $range)
    $groups = [ordered]@{}
    foreach ($line in $commits) {
        $hash, $subject = $line -split "`t", 2
        if ($subject -match '^(chore|feat)\s*\(release\):\s*(bump|update).*versions') { continue }
        $paths = Invoke-Git @('diff-tree', '--root', '--no-commit-id', '--name-only', '-r', $hash)
        $relevant = @($paths | Where-Object {
            $_ -notmatch '^Hi3Helper\.Plugin\.(Endfield|Arknights)/' -and
            !($PluginShort -eq 'Endfield' -and $_ -match '^Hi3Helper\.Hypergryph\.Yostar/') -or
            $_ -like "Hi3Helper.Plugin.$PluginShort/*"
        })
        if (!$relevant.Count) { continue }
        $group = 'Other changes'
        if ($subject -match '^(feat|fix|perf|refactor|build|ci|test|docs|chore)\s*(?:\((.*?)\))?!?:\s*(.+)$') {
            $type, $scope, $message = $Matches[1], $Matches[2], $Matches[3]
            $group = switch ($type) { 'feat' { 'Features' }; 'fix' { 'Fixes' }; 'perf' { 'Performance' }; 'refactor' { 'Refactoring' }; { $_ -in 'ci','build' } { 'Build & CI' }; default { 'Other changes' } }
            $subject = if ($scope) { "${scope}: $message" } else { $message }
        }
        if (!$groups.Contains($group)) { $groups[$group] = [Collections.Generic.List[string]]::new() }
        $groups[$group].Add("- $subject ($($hash.Substring(0,7)))")
    }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("## [$tag] - $([DateTime]::UtcNow.ToString('yyyy-MM-dd'))")
    $lines.Add('')
    foreach ($entry in $groups.GetEnumerator()) {
        $lines.Add("### $($entry.Key)"); $lines.Add('')
        $lines.AddRange([string[]]$entry.Value); $lines.Add('')
    }
    if (!$groups.Count) { $lines.Add('No plugin code changes in this range.'); $lines.Add('') }
    $lines.Add("Source commit: ``$sha``")
    if ($previous) { $lines.Add("Previous plugin release: ``$previous``") }
    return $lines -join "`n"
}

function Assert-Payload([string] $Path, [string] $PluginShort) {
    $null = Get-Plugin $PluginShort
    foreach ($name in @("$PluginShort.dll", 'manifest.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $Path $name) -PathType Leaf)) { throw "Missing release file: $Path/$name" }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $Path 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.MainLibraryName -cne "$PluginShort.dll" -or !@($manifest.Assets).Count) { throw "Invalid manifest for $PluginShort" }
    $root = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($asset in $manifest.Assets) {
        $relative = [string]$asset.FilePath
        if (!$relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Replace('\','/').Split('/') -contains '..') { throw 'Invalid manifest asset path.' }
        $file = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (!$file.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing manifest asset: $relative" }
    }
    $zips = @(Get-ChildItem -LiteralPath (Join-Path $Path 'build') -Filter '*.zip' -File)
    if (!$zips.Count) { throw "No indexed ZIP for $PluginShort" }
    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse) {
        if ($file.Extension -eq '.pdb' -or $file.Name -like '*.pdb.br') { throw "Debug symbols must not enter the release repository: $($file.Name)" }
        if ($file.Length -ge 50MB) { throw "Release repository file reaches 50 MiB: $($file.Name). Distribute it as a release asset instead." }
    }
    foreach ($zip in $zips) {
        $archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
        try {
            if (@($archive.Entries | Where-Object { $_.FullName -match '\.pdb(\.br)?$' }).Count) { throw "Debug symbols found inside $($zip.Name)" }
        } finally { $archive.Dispose() }
    }
}

function Stage-Payload([string] $From, [string] $To, [string] $PluginShort) {
    $null = Get-Plugin $PluginShort
    if (Test-Path -LiteralPath $To) { throw "Staging destination already exists: $To" }
    $null = New-Item -ItemType Directory -Path $To
    $null = New-Item -ItemType Directory -Path (Join-Path $To 'build')
    foreach ($name in @("$PluginShort.dll", 'manifest.json')) { Copy-Item -LiteralPath (Join-Path $From $name) -Destination $To }
    foreach ($dll in Get-ChildItem -LiteralPath $From -File -Filter '*.dll') { Copy-Item -LiteralPath $dll.FullName -Destination $To -Force }
    $lib = Join-Path $From 'Lib'
    if (Test-Path -LiteralPath $lib) { Copy-Item -LiteralPath $lib -Destination $To -Recurse }
    foreach ($zip in Get-ChildItem -LiteralPath $From -Filter '*.zip' -File) { Copy-Item -LiteralPath $zip.FullName -Destination (Join-Path $To 'build') }
    Assert-Payload $To $PluginShort
}

function Collect-Payloads([string] $From, [string] $To, [string] $Json) {
    $items = @(Read-Plan $Json)
    # Validate every artifact before changing the target checkout.
    foreach ($item in $items) { Assert-Payload (Join-Path $From "release-$($item.short)/$($item.short)") $item.short }
    $root = [IO.Path]::GetFullPath($To)
    foreach ($item in $items) {
        $target = [IO.Path]::GetFullPath((Join-Path $root $item.short))
        if (![IO.Path]::GetDirectoryName($target).Equals($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid target directory.' }
        $null = New-Item -ItemType Directory -Force -Path $target
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Target plugin directory must not be a link.' }
        foreach ($directory in @('Lib', 'build')) {
            $owned = Join-Path $target $directory
            if (Test-Path -LiteralPath $owned) { Remove-Item -LiteralPath $owned -Recurse -Force }
        }
        foreach ($file in Get-ChildItem -LiteralPath $target -File) {
            if ($file.Extension -in '.dll','.pdb' -or $file.Name -eq 'manifest.json' -or $file.Name -like '*.pdb.br') { Remove-Item -LiteralPath $file.FullName -Force }
        }
        Copy-Item -Path (Join-Path $From "release-$($item.short)/$($item.short)/*") -Destination $target -Recurse -Force
    }
}

function Bump-Versions([string] $Json) {
    foreach ($item in @(Read-Plan $Json)) {
        $path = Join-Path $PWD "$($item.name)/$($item.name).csproj"
        $text = [IO.File]::ReadAllText($path)
        if ([regex]::Matches($text, '<Version>[^<]*</Version>').Count -ne 1) { throw "Expected one Version property in $path" }
        $bytes = [IO.File]::ReadAllBytes($path)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        [IO.File]::WriteAllText($path, [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$($item.version)</Version>"), [Text.UTF8Encoding]::new($bom))
    }
}

switch ($Action) {
    'CheckTags' { Assert-StableTags $Plan $Revision 'origin' }
    'Plan' { $json = New-Plan; $json; if ($env:GITHUB_OUTPUT) { "matrix=$json" >> $env:GITHUB_OUTPUT } }
    'Notes' { [IO.File]::WriteAllText([IO.Path]::GetFullPath($Destination), (New-Notes $Short $Version $Revision), [Text.UTF8Encoding]::new($false)) }
    'Stage' { Stage-Payload $Source $Destination $Short }
    'Collect' { Collect-Payloads $Source $Destination $Plan }
    'Bump' { Bump-Versions $Plan }
}
