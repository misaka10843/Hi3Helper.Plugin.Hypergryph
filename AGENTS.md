# Repository Guidance

## Architecture

This repository exposes game support through one plugin per game and separate
protocol libraries per launcher/vendor.

- `Hi3Helper.Plugin.Core`: shared plugin ABI, base managers/installers, progress
  types, utilities, and COM interop. This is a nested Git repository.
- `Hi3Helper.Hypergryph.Core`: Hypergryph launcher protocol used by mainland
  China/Bilibili presets and Endfield. Do not add Yostar protocol code here.
- `Hi3Helper.Hypergryph.Yostar`: Yostar launcher protocol specifically for
  Hypergryph games published by Yostar.
- `Hi3Helper.Plugin.Arknights`: the single user-facing Arknights plugin. It owns
  all Arknights presets and selects the appropriate protocol implementation.
- `SevenZipExtractor`: nested Git repository used by the Hypergryph installer.

Arknights must remain one plugin with multiple presets:

- Mainland China and Bilibili use `HgGameManager` / `HgGameInstaller`.
- Global, Japan and Korea use `YostarGameManager` / `YostarGameInstaller`.

`Hi3Helper.Hypergryph.Shared` owns common plugin paths, configuration, request
state and settings adapters. It references Plugin.Core, not either protocol
library. Keep Yostar independent from the Hypergryph protocol implementation.

## Commits

After adding or changing functionality, run the relevant validation and create
a Git commit. Write commit subjects and bodies in English. Use exactly
`feat (module): description`, for example
`feat (settings): integrate game and plugin configuration pages`.
Group commits by complete functionality.
Review staged changes and include only task-related source, documentation,
patches and upstream gitlinks. Do not commit user configuration, build outputs
or unrelated lock-file rewrites. Do not push or publish unless requested.

## Documentation changes

For README changes, propose the edits to the user first. Do not edit README
files without the user's explicit approval of those changes.
Do not add investigation notes, evidence/support matrices or validation diaries
to Git unless explicitly requested. Keep temporary research and verification
notes outside tracked source, for example under `.git/local-notes`.
Update existing technical documentation only where needed to describe changed
behavior; do not create extra documentation as a routine implementation step.

## Release automation

Build every selected plugin at the dispatch SHA, then validate and aggregate
all payloads before creating one release-repository PR per workflow run. Keep
PDBs as separate Actions artifacts; remove them before Indexer generates the
runtime manifest and ZIP. Never silently ignore missing release files.
Generate notes from the previous tag for the same plugin through the build SHA,
accept both `feat(scope):` and `feat (scope):` subjects, and omit automatic
version-only commits. Do not move existing stable tags to another commit.
Run `.github/scripts/Test-PluginRelease.ps1` and actionlint after workflow edits.

Do not create a separate Global or Japan plugin. Do not make Yostar managers
inherit from Hypergryph managers. Both implementations derive independently
from the abstractions in `Hi3Helper.Plugin.Core`.

## Upstream Core compatibility

Keep nested repositories pinned to commits available on their upstream remotes.
Do not create local-only submodule commits or patch Core to implement plugin
features. Use the existing public ABI without adding exports or changing types.

`PluginApiBoundary` implements the published settings exports and localized COM
error information. `PluginCulture` retains the requested locale for plugin UI
and passes a compatible locale to Core. `PluginDiagnostics` captures Core's
standard logger callback for file logging and forwards it to the host. News
adapters assign the complete social-media flag set after populating entries.
There is no build-time submodule patch step.

## Endfield game launch

From client 1.6.3 the game refuses to start unless its **direct** parent process
is a Hypergryph-signed `Games.exe`. That parent is never resumed; it exists only
to satisfy the check.

`Exports.GameLaunch.cs` uses one configured parent source, defaulting to the
game-root `PlatformProcess.exe`. `Endfield.exe` itself requests elevation and
is not the default parent. Sources may be absolute or game-relative EXE paths.
Validate both Authenticode signatures and compare SHA-256 fingerprints of the
actual signer certificates with the official game EXE, not publisher strings.
Revalidate the staged copy under `_Hi3_Hg/parent/<PID>/<session>/Games.exe`.
Keep it suspended and release its resources on exit, failure and cancellation.
Failures are explicit: do not reintroduce recursive candidate searches,
official-launcher downloads or automatic direct-start fallbacks.

Details: `docs/DECOY_PARENT_BRIDGE.md`.

## Plugin storage and settings

All plugin-owned working directories belong under `<game>/_Hi3_Hg`, accessed
through `PluginPaths`: `logs`, `downloads`, `delta-temp`, `apply-stage`, `tools`,
and `parent`. Official metadata and protocol-required per-file `.tmp` files
keep their official locations. Migrate legacy directories only when the target
is absent; preserve conflicts and directory links rather than merging them.

`config.json` uses source-generated JSON, atomic replacement, schema versioning
and external-change detection. Preserve unknown fields and malformed files.
`manualVerificationRequested` is a one-shot command: clear it on disk before
accepting it into per-manager memory. Reading the settings page does not
consume it. Status/size queries preserve the in-memory request. Starting the
operation consumes it; failure/cancellation/restart never requeues it.
Suppress preload presentation while a manual verification is pending.
The host can invoke `StartInstallAsync` for its update button. Both install and
update entrypoints must route pending verification through the same operation;
size queries for either kind must leave the request pending. Verification must
request full resource metadata even when the installed version is current, and
must report missing resource URLs or invalid manifests as failures.

Both plugins implement Core v0.1.6 settings-page exports. Edits are drafts until
Apply, isolated by preset and game path. Game settings write to the client's
actual storage, preserve unknown data, reject external changes and block
modifications while the game is running. Expose only verified fields/regions;
do not guess enum values or clone game settings into plugin JSON.
Localize plugin-owned settings labels, choices, explanations and errors in
English, Chinese and Japanese using `PluginCulture.Locale`; keep
stored keys, numbers and enum values invariant. The host controls layout and
its own strings. Keep entry labels short and place shared help on sections,
because the current host renders entries in narrow columns without layout hints.

Game-specific preference implementations belong in their game plugin; Shared
contains only common configuration, localization and ABI adapters. Game
settings keys must be routed through `IGamePreferences.Set` after handling
plugin-owned keys; do not whitelist display/graphics prefixes. Interface smoke
tests must submit every rendered game setting, including audio and language.
Endfield CN audio volumes use eight bytes labelled REG_DWORD, returned as Int64 by .NET.
Preserve that storage shape when writing and rolling back. Do not reinterpret
them as ordinary DWORD integers or silently convert them to REG_QWORD.
Only expose recorded enum options; UI ordering alone does not establish values.

## Arknights Presets

Preset registration is in `Hi3Helper.Plugin.Arknights/Plugin.cs`.

- `ArknightsCnPresetConfig`: Hypergryph mainland China, channel `1`.
- `ArknightsBiliPresetConfig`: Hypergryph Bilibili, channel `2`.
- `ArknightsGlobalPresetConfig`: Yostar `Arknights_EN`.
- `ArknightsJpPresetConfig`: Yostar `Arknights_JP`.
- `ArknightsKrPresetConfig`: Yostar Korea (see its options for the game tag).

Global and Japan use the same Yostar implementation. Region differences belong
in `YostarLauncherOptions`, not duplicated manager/installer classes.

The Arknights game-launch code must work with `IGameManager`. Do not reintroduce
a concrete `HgGameManager` type check in `Exports.GameLaunch.cs`.

## Yostar Protocol

The protocol was validated against Yostar launcher version `1.8.1`.

### Region configuration

| Region | Game tag | API base URL | Default directory |
| --- | --- | --- | --- |
| Global | `Arknights_EN` | `https://api-launcher-en.yo-star.com` | `Arknights_EN` |
| Japan | `Arknights_JP` | `https://api-launcher-jp.yo-star.com` | `Arknights_JP` |

The API may return `Arknights` without an extension for Global and
`Arknights.exe` for Japan. Preserve the API value in
`game-launcher-config.json`, but normalize it to `.exe` when resolving the
actual executable path.

### Authorization

Normal API requests carry an `Authorization` JSON header:

```text
head = {"game_tag":"...","time":UNIX_SECONDS,"version":"1.8.1"}
sign = lowercase_md5(JSON(head) + request_body_or_empty + salt)
salt = DE7108E9B2842FD460F4777702727869
Authorization = {"head":head,"sign":"..."}
```

Property order and compact JSON formatting are part of the signature. CDN and
manifest downloads are unsigned.

Important endpoints:

- `GET /api/launcher/game/config`
- `GET /api/launcher/game/config/json?version=&file_path=`
- `GET /api/launcher/advanced/game/download/cdn`

Keep API DTOs and signing in `Hi3Helper.Hypergryph.Yostar/Api`.

### Remote manifest

The downloaded remote manifest is not the same shape as the local manifest:

```json
{
  "source": "/Arknights_EN-041.2.0-game",
  "file": [
    { "path": "/Arknights.exe", "hash": "...", "size": "675304" }
  ]
}
```

Do not deserialize this directly as the local `manifest.json` model.

Manifest paths are untrusted remote input. Always normalize them and verify the
resolved full path remains under the selected game directory before reading,
writing, moving, or deleting a file.

### CRC64

Yostar file hashes are unsigned decimal CRC64/XZ values:

- Reflected polynomial: `0xC96C5795D7870F42`
- Initial value: `ulong.MaxValue`
- Final XOR: `ulong.MaxValue`
- Output: invariant unsigned decimal string

Do not replace this with a differently configured CRC64 implementation without
checking it against an official CDN file. The current implementation was
validated with this file/hash pair:

```text
Arknights_Data/Plugins/x86_64/API-MS-Win-core-file-l2-1-0.dll
5943362273480959350
```

### Local metadata and VC

Yostar installations use:

- `game-launcher-config.json`
- `manifest.json`

The `vc` value is Base64(MD5(values joined by `;`)). JavaScript
`Object.values()` order is significant. Keep these exact orders when writing:

```text
manifest info: name, version, basis
manifest file: path, hash, size
game config: tag, name, params, version, gameUninstallScript
```

For `params`, JavaScript array stringification is comma-joined. Keep remote file
paths, hashes, and sizes as strings. A change in JSON property order can make
the official launcher reject locally generated metadata.

### Install/update behavior

The Yostar installer is file-based rather than archive-based:

1. Fetch game config, CDN config, and target remote manifest.
2. Diff it against the validated local manifest and current file sizes.
3. Download to `<target>.tmp` with HTTP Range resume support.
4. Retry each file using primary x4, backup x3, primary x3.
5. Validate the completed temporary file with Yostar CRC64.
6. Move verified temporary files into place and delete obsolete manifest files.
7. Write both local metadata files through temporary files.

Keep path validation, download verification, and metadata writing intact. A
downloaded file must never replace the target before its CRC matches.

The legacy `.collapse_verify_game` marker is imported into a one-shot manual
verification request and removed when accepted, not when repair completes.

Yostar currently exposes no separate preload version/package. Keep
`HasPreload` false and do not infer preload state from `game_latest_file_path`.

## HTTP and Security

- Keep signed API traffic separate from unsigned CDN traffic.
- Do not add the official launcher's telemetry or Aliyun SLS upload behavior.
- Do not disable TLS certificate validation for Yostar endpoints.
- Preserve cancellation tokens and streamed responses for large files.
- Do not log the authorization salt-derived header or other sensitive request
  material at normal log levels.

## Validation

Build the protocol library and the user-facing plugin after changes:

```powershell
dotnet build Hi3Helper.Hypergryph.Yostar/Hi3Helper.Hypergryph.Yostar.csproj -c Release -p:Platform=x64
dotnet build Hi3Helper.Plugin.Arknights/Hi3Helper.Plugin.Arknights.csproj -c Release -p:Platform=x64
dotnet build Hi3Helper.Plugin.Hypergryph.sln -c Release -p:Platform=x64
```

At minimum, protocol changes should verify:

- Global and Japan signed API requests both return code `200`.
- Each returned manifest contains a non-empty `source` and file list.
- CRC64 matches at least one small official CDN file.
- Arknights still compiles with all five presets registered.
- `git diff --check` passes.

The solution currently has an existing warning because
`SharpHDiffPatch.Core` lacks a `Release|x64` solution mapping. Do not treat that
warning as a Yostar failure.

Some .NET SDK patch versions may rewrite `packages.lock.json` inside the
`Hi3Helper.Plugin.Core` and `SevenZipExtractor` nested repositories. Do not
commit those unrelated lock-file updates unless dependency changes are part of
the task.

Do not claim full installation validation unless a complete client download,
metadata write, game launch, update, repair, and uninstall cycle was actually
performed. API, CRC, and build smoke tests are not a substitute for that
end-to-end test.

Run the available regression checks appropriate to the changed behavior.
Settings and launch changes also require Native AOT publishing with
`ReleaseNoReflectionPublish-O1` and an interface smoke test; ordinary managed
builds do not establish launcher compatibility. Record live UI/game acceptance
separately from automated checks.
