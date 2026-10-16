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
- Global and Japan use `YostarGameManager` / `YostarGameInstaller`.

Do not create a separate Global or Japan plugin. Do not make Yostar managers
inherit from Hypergryph managers. Both implementations derive independently
from the abstractions in `Hi3Helper.Plugin.Core`.

## Submodule patches

The nested repositories are pinned to plain upstream commits, so a fix we need
on top of one of them cannot be a commit of our own: a gitlink can only name a
commit the remote already has, and CI fetches the submodule straight from that
remote. Local fixes therefore live in `patches/<submodule>/` as `.patch` files.

- `patches/Hi3Helper.Plugin.Core/0001-social-media-entry-flags.patch` — the
  `LauncherSocialMediaEntry` setters clear the opposite flag with
  `Flags |= ~X`, which sets every unrelated bit instead of clearing one. An
  entry written with an icon buffer and a QR path ends up with `IsPath` and
  `IsDataBuffer` both set, and the media panel renders the buffer as a path.

`Directory.Build.targets` runs `scripts/Apply-SubmodulePatches.ps1` before the
patched submodule compiles, so a plain `dotnet build` and CI use the same
sources. The script is idempotent, and it fails the build when a patch no
longer applies. That failure is the intended behaviour after a submodule bump:
rebase the patch onto the new commit, or delete it if upstream has fixed the
bug. Do not work around it by committing a local submodule commit.

## Endfield game launch

From client 1.6.3 the game refuses to start unless its **direct** parent process
is a Hypergryph-signed `Games.exe`. That parent is never resumed; it exists only
to satisfy the check.

`Exports.GameLaunch.cs` stages one of the game's own signed executables
(`PlatformProcess.exe`, `UnityCrashHandler64.exe`, `Endfield.exe`) into
`%TEMP%\Hi3Helper\ParentBridge\Games.exe`, creates it suspended, then starts the
game with `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`. Nothing is written into the
game directory, and every failure falls back to a plain direct start.

`OfficialLauncherBridge` / `OfficialLauncherProvider` implement the older route
(locate or download the real launcher) and stay compiled but unused:
`AcquireOfficialLauncher` in `Exports.GameLaunch.cs` is `false`. Do not delete
them, and do not flip the flag back without confirming the decoy still passes.

Both routes share `ParentProcessBridge`, which owns suspension, signature
verification, the parent attribute and teardown. A subclass only answers where
the parent executable comes from.

Details: `docs/DECOY_PARENT_BRIDGE.md`, `docs/OFFICIAL_LAUNCHER_BRIDGE.md`.

## Arknights Presets

Preset registration is in `Hi3Helper.Plugin.Arknights/Plugin.cs`.

- `ArknightsCnPresetConfig`: Hypergryph mainland China, channel `1`.
- `ArknightsBiliPresetConfig`: Hypergryph Bilibili, channel `2`.
- `ArknightsGlobalPresetConfig`: Yostar `Arknights_EN`.
- `ArknightsJpPresetConfig`: Yostar `Arknights_JP`.

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

The `.collapse_verify_game` marker requests a full CRC scan on the next update.
Remove it only after a successful repair/update.

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
- Arknights still compiles with all four presets registered.
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
