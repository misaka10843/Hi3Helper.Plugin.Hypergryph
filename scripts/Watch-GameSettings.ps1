#requires -Version 5.1
<#
.SYNOPSIS
Read-only registry preference capture for Endfield and the Arknights PC client.
.DESCRIPTION
Change ONE game setting and apply it. After the registry becomes stable, label
the detected changes (for example: "Master volume 10 -> 7"). Some games flush
preferences only when applying settings or exiting. Changes made while typing
a label are checked again on the next poll, rather than silently discarded.

Output is UTF-8 JSONL outside the repository by default. Account/session keys
and download/runtime bookkeeping are excluded. Only likely preference names
are read; IncludeNamePattern can be extended if a setting is not detected.
The watcher never changes game preferences. SelfTest uses its own temporary
registry key and deletes it afterwards.
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Watch-GameSettings.ps1
.EXAMPLE
.\scripts\Watch-GameSettings.ps1 -RegistryPaths 'HKCU:\Software\Hypergryph\Endfield' -DiscoverGameKeys:$false
.EXAMPLE
.\scripts\Watch-GameSettings.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string[]] $RegistryPaths = @(
        'HKCU:\Software\Hypergryph\Endfield',
        'HKCU:\Software\Gryphline\Endfield',
        'HKCU:\Software\Hypergryph\Arknights',
        'HKCU:\Software\Yostar\Arknights',
        'HKCU:\Software\Yostar\Arknights_EN',
        'HKCU:\Software\Yostar\Arknights_JP',
        'HKCU:\Software\Yostar\Arknights_KR'
    ),
    [bool] $DiscoverGameKeys = $true,
    [ValidateRange(100, 10000)] [int] $PollMilliseconds = 400,
    [ValidateRange(200, 30000)] [int] $SettleMilliseconds = 1200,
    [ValidateRange(1000, 120000)] [int] $MaxBatchMilliseconds = 15000,
    [ValidateRange(256, 1048576)] [int] $MaxValueBytes = 65536,
    [string] $IncludeNamePattern = '(?i)(^Screenmanager|^UnitySelectMonitor|^video_|^audio_|^sound|^music|^sfx|^voice|^volume|^language_|^camera_|^control_|^input_|^keybind|setting|quality|resolution|fullscreen|sensitivity|vsync|brightness|hdr|motion|gamma|render|shadow|reflection|blur|bloom|occlusion|texture|framerate|fps|antialias)',
    [string] $OutputPath,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This watcher requires Windows.' }
if ($MaxBatchMilliseconds -lt $SettleMilliseconds) { throw 'MaxBatchMilliseconds must be >= SettleMilliseconds.' }
try { $null = [regex]::new($IncludeNamePattern) } catch { throw 'IncludeNamePattern is not a valid regular expression.' }

$script:IgnoreNames = '(?i)(token|password|secret|credential|session|cached_uid|account|login|auth|ticket|device.?id|^PLDK_|^Manual|^CollapseLauncher_|^unity\.|^u8sdk_|^sdk_)'
$script:ReadErrors = @{}
$script:Utf8 = [Text.UTF8Encoding]::new($false)

function Convert-PreferenceValue {
    param([Microsoft.Win32.RegistryKey] $Key, [string] $Name)
    $kind = $Key.GetValueKind($Name)
    $value = $Key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $state = [ordered]@{ kind = $kind.ToString() }
    switch ($kind.ToString()) {
        'DWord' {
            $state.value = ([int]$value).ToString([Globalization.CultureInfo]::InvariantCulture)
            $state.hex = '0x' + ([BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$value), 0)).ToString('X8')
        }
        'QWord' { $state.value = ([long]$value).ToString([Globalization.CultureInfo]::InvariantCulture) }
        'MultiString' {
            $encoded = $script:Utf8.GetBytes(($value | ConvertTo-Json -Compress))
            if ($encoded.Length -le $MaxValueBytes) { $state.value = @($value) }
            else { $state.omitted = $true; $state.byteLength = $encoded.Length }
        }
        { $_ -in 'String', 'ExpandString' } {
            $encoded = $script:Utf8.GetBytes([string]$value)
            if ($encoded.Length -le $MaxValueBytes) { $state.value = [string]$value }
            else { $state.omitted = $true; $state.byteLength = $encoded.Length }
        }
        default {
            [byte[]] $encoded = @()
            if ($null -ne $value) { $encoded = [byte[]]$value }
            $state.byteLength = $encoded.Length
            if ($encoded.Length -le $MaxValueBytes) { $state.base64 = [Convert]::ToBase64String($encoded) }
            else { $state.omitted = $true }
        }
    }
    if ($state.Contains('omitted')) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $state.sha256 = ([BitConverter]::ToString($sha.ComputeHash($encoded))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
    }
    return [pscustomobject]$state
}

function Get-WatchedPaths {
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $RegistryPaths) {
        if ($path -notmatch '^(?i:HKCU:|HKEY_CURRENT_USER)[\\/]') { throw "Only HKCU paths are supported: $path" }
        $relative = ($path -replace '^(?i:HKCU:|HKEY_CURRENT_USER)[\\/]+', '').Replace('/', '\')
        $null = $paths.Add($relative)
    }
    if ($DiscoverGameKeys) {
        foreach ($vendor in @('Hypergryph', 'Gryphline', 'Yostar', 'YostarGames')) {
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\' + $vendor)
            if ($null -eq $key) { continue }
            try {
                foreach ($name in $key.GetSubKeyNames()) {
                    if ($name -match '^(?i:Endfield|Arknights)(?:$|[ _-])') { $null = $paths.Add('Software\' + $vendor + '\' + $name) }
                }
            }
            finally { $key.Dispose() }
        }
    }
    return $paths | Sort-Object
}

function Get-PreferenceSnapshot {
    $snapshot = @{}
    foreach ($path in @(Get-WatchedPaths)) {
        $key = $null
        try {
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
            if ($null -eq $key) { continue }
            foreach ($name in $key.GetValueNames()) {
                if ($name -match $script:IgnoreNames -or $name -notmatch $IncludeNamePattern) { continue }
                # A preference can disappear between enumeration and read. Retry the entire
                # snapshot next poll rather than inventing a removal from a partial read.
                $state = Convert-PreferenceValue $key $name
                $snapshot[$path + [char]0 + $name] = [pscustomobject]@{
                    path = 'HKCU:\' + $path; name = $name; state = $state
                    fingerprint = ($state | ConvertTo-Json -Depth 8 -Compress)
                }
            }
            $script:ReadErrors.Remove($path)
        }
        catch {
            if (-not $script:ReadErrors.ContainsKey($path)) { Write-Warning "Cannot read HKCU:\$path : $($_.Exception.Message)"; $script:ReadErrors[$path] = $true }
            return $null
        }
        finally { if ($null -ne $key) { $key.Dispose() } }
    }
    return $snapshot
}

function Compare-PreferenceSnapshot {
    param([hashtable] $Before, [hashtable] $After)
    $ids = @($Before.Keys) + @($After.Keys) | Sort-Object -Unique
    foreach ($id in $ids) {
        $old = $Before[$id]; $new = $After[$id]
        if ($null -ne $old -and $null -ne $new -and $old.fingerprint -ceq $new.fingerprint) { continue }
        $entry = if ($null -ne $new) { $new } else { $old }
        [pscustomobject][ordered]@{
            path = $entry.path; name = $entry.name
            before = if ($null -ne $old) { $old.state } else { $null }
            after = if ($null -ne $new) { $new.state } else { $null }
        }
    }
}

function Add-CaptureRecord {
    param([string] $File, [object] $Record)
    [IO.File]::AppendAllText($File, ($Record | ConvertTo-Json -Depth 12 -Compress) + [Environment]::NewLine, $script:Utf8)
}

function Invoke-CaptureSelfTest {
    $relative = 'Software\Hi3Helper\SettingsCaptureTest-' + [Guid]::NewGuid().ToString('N')
    $script:RegistryPaths = @('HKCU:\' + $relative)
    $script:DiscoverGameKeys = $false
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($relative)
    $output = [IO.Path]::GetTempFileName()
    try {
        $key.SetValue('audio_volume', 10, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('video_quality', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('audio_auth_token', 'must not be captured')
        $before = Get-PreferenceSnapshot
        if ($before.Count -ne 2) { throw 'Filter or snapshot test failed.' }
        $key.SetValue('audio_volume', 7, [Microsoft.Win32.RegistryValueKind]::DWord)
        $after = Get-PreferenceSnapshot
        $changes = @(Compare-PreferenceSnapshot $before $after)
        if ($changes.Count -ne 1 -or $changes[0].before.value -ne '10' -or $changes[0].after.value -ne '7') { throw 'Value diff test failed.' }
        Add-CaptureRecord $output ([ordered]@{ type = 'change'; label = 'Master volume 10 -> 7'; changes = $changes })
        $record = [IO.File]::ReadAllText($output) | ConvertFrom-Json
        if ($record.label -ne 'Master volume 10 -> 7' -or $record.changes[0].after.kind -ne 'DWord') { throw 'JSONL round-trip failed.' }
        $key.SetValue('audio_binary', [byte[]]@(1, 2, 3), [Microsoft.Win32.RegistryValueKind]::Binary)
        $key.DeleteValue('video_quality')
        $next = Get-PreferenceSnapshot
        $changes = @(Compare-PreferenceSnapshot $after $next)
        if ($changes.Count -ne 2 -or @($changes | Where-Object { $_.name -eq 'audio_binary' })[0].after.base64 -ne 'AQID') { throw 'Creation/removal/binary test failed.' }
        if (@(Compare-PreferenceSnapshot $next $next).Count -ne 0) { throw 'Stable snapshot test failed.' }
        $key.SetValue('audio_empty', [byte[]]@(), [Microsoft.Win32.RegistryValueKind]::Binary)
        $key.SetValue('audio_large', [byte[]]::new($MaxValueBytes + 1), [Microsoft.Win32.RegistryValueKind]::Binary)
        $key.SetValue('input_counter', [long]::MaxValue, [Microsoft.Win32.RegistryValueKind]::QWord)
        $key.SetValue('input_path', '%USERPROFILE%', [Microsoft.Win32.RegistryValueKind]::ExpandString)
        $key.SetValue('audio_languages', [string[]]@('English', '日本語'), [Microsoft.Win32.RegistryValueKind]::MultiString)
        $extra = Get-PreferenceSnapshot
        if ($extra[$relative + [char]0 + 'audio_empty'].state.base64 -ne '' -or
            -not $extra[$relative + [char]0 + 'audio_large'].state.omitted -or
            $extra[$relative + [char]0 + 'input_counter'].state.value -ne '9223372036854775807' -or
            $extra[$relative + [char]0 + 'input_path'].state.value -ne '%USERPROFILE%' -or
            $extra[$relative + [char]0 + 'audio_languages'].state.value[1] -ne '日本語') { throw 'Registry value encoding/size tests failed.' }
        Write-Host 'PASS: read-only snapshots, filters, values, additions/removals, binary data and JSONL labeling.' -ForegroundColor Green
    }
    finally {
        $key.Dispose()
        if ($relative.StartsWith('Software\Hi3Helper\SettingsCaptureTest-', [StringComparison]::Ordinal)) {
            [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($relative, $false)
        }
        [IO.File]::Delete($output)
    }
}

if ($SelfTest) { Invoke-CaptureSelfTest; return }

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $directory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Hi3Helper\SettingsCapture'
    $OutputPath = Join-Path $directory ('settings-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6) + '.jsonl')
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ([IO.File]::Exists($OutputPath)) { throw "Output already exists; choose a new filename: $OutputPath" }
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))
$baseline = Get-PreferenceSnapshot
if ($null -eq $baseline) { throw 'Cannot capture an initial snapshot. Resolve the reported registry access error first.' }
$session = [Guid]::NewGuid().ToString('N')
$initial = @($baseline.Values | Sort-Object path, name | ForEach-Object { [ordered]@{ path = $_.path; name = $_.name; state = $_.state } })
Add-CaptureRecord $OutputPath ([ordered]@{
    type = 'session'; session = $session; at = [DateTimeOffset]::Now.ToString('o')
    paths = @(Get-WatchedPaths | ForEach-Object { 'HKCU:\' + $_ }); includeNamePattern = $IncludeNamePattern
    initial = $initial
})
Write-Host "监听已开始，初始捕获 $($baseline.Count) 个设置值。" -ForegroundColor Cyan
Write-Host "输出文件：$OutputPath"
Write-Host '每次只改一个设置，点击游戏中的应用；必要时退出游戏。检测到变化后，填写设置名称及前后值。'
Write-Host '标注时可输入 /skip 忽略这组，/stop 结束；等待时可按 Ctrl+C 结束。'

$sequence = 0
while ($true) {
    Start-Sleep -Milliseconds $PollMilliseconds
    $observed = Get-PreferenceSnapshot
    if ($null -eq $observed) { continue }
    $changes = @(Compare-PreferenceSnapshot $baseline $observed)
    if ($changes.Count -eq 0) { continue }
    $batch = [Diagnostics.Stopwatch]::StartNew()
    $stable = [Diagnostics.Stopwatch]::StartNew()
    while ($stable.ElapsedMilliseconds -lt $SettleMilliseconds -and $batch.ElapsedMilliseconds -lt $MaxBatchMilliseconds) {
        Start-Sleep -Milliseconds $PollMilliseconds
        $latest = Get-PreferenceSnapshot
        if ($null -eq $latest) { $stable.Restart(); continue }
        if (@(Compare-PreferenceSnapshot $observed $latest).Count -ne 0) { $observed = $latest; $stable.Restart() }
    }
    $changes = @(Compare-PreferenceSnapshot $baseline $observed)
    if ($changes.Count -eq 0) { continue }
    if ($stable.ElapsedMilliseconds -lt $SettleMilliseconds) { Write-Warning '变化仍在持续或读取失败；本组未达到稳定等待时间，可输入 /skip 后重试。' }
    Write-Host "`n检测到 $($changes.Count) 个值变化：" -ForegroundColor Yellow
    foreach ($change in $changes) {
        Write-Host "$($change.path)\$($change.name)"
        Write-Host ('  before: ' + ($change.before | ConvertTo-Json -Depth 8 -Compress))
        Write-Host ('  after : ' + ($change.after | ConvertTo-Json -Depth 8 -Compress))
    }
    do { $label = (Read-Host '这次修改的是哪个设置？例如：主音量 10 -> 7').Trim() } while ([string]::IsNullOrWhiteSpace($label))
    if ($label -eq '/stop') { break }
    if ($label -ne '/skip') {
        $sequence++
        Add-CaptureRecord $OutputPath ([ordered]@{
            type = 'change'; session = $session; sequence = $sequence; at = [DateTimeOffset]::Now.ToString('o')
            label = $label; stable = ($stable.ElapsedMilliseconds -ge $SettleMilliseconds); changes = $changes
        })
        Write-Host "已记录 #$sequence。可以修改下一项。" -ForegroundColor Green
    }
    $baseline = $observed
}
Write-Host "记录已保存：$OutputPath" -ForegroundColor Cyan
