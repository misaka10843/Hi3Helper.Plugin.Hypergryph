# Hi3Helper.Hypergryph.Core

`Hi3Helper.Hypergryph.Core` is the shared core library for Hypergryph-based game plugins used by Collapse Launcher.

It provides common implementations for interacting with Hypergryph launcher services, including game version management, installation and updates, file integrity repair, launcher news/background APIs, and encrypted configuration parsing. Plugins such as `Hi3Helper.Plugin.Arknights` and `Hi3Helper.Plugin.Endfield` are built on top of this library.

## Features

The library exposes the following major components:

| Component            | Description                                                                                     |
| -------------------- | ----------------------------------------------------------------------------------------------- |
| `HgGameManager`      | Retrieves installation state, game version, update information, and package metadata            |
| `HgGameInstaller`    | Handles fresh installation, incremental updates, resumable downloads, and installation progress |
| `HgGameRepairer`     | Performs file verification and repair based on the `game_files` manifest                        |
| `HgLauncherApiNews`  | Retrieves launcher announcements, banners, and news entries                                     |
| `HgLauncherApiMedia` | Retrieves launcher background images and videos                                                 |
| `HgCrypto`           | Encrypts and decrypts Hypergryph configuration files                                            |
| `ConfigTool`         | Reads and parses game versions from `config.ini`                                                |
| `MultiVolumeStream`  | Provides a continuous stream interface for multi-volume archives                                |
| `HgApiStructs`       | Request and response models for Hypergryph launcher APIs                                        |

## Requirements

Target framework:

```xml
<TargetFramework>net10.0</TargetFramework>
```

Dependencies:

```xml
<ProjectReference Include="..\Hi3Helper.Plugin.Core\Hi3Helper.Plugin.Core.csproj" />
<ProjectReference Include="..\SevenZipExtractor\SevenZipExtractor\SevenZipExtractor.csproj" />
<ProjectReference Include="..\SharpHDiffPatch.Core\SharpHDiffPatch.Core.csproj" />
<PackageReference Include="System.IO.Hashing" Version="10.0.1" />
```

## Getting Started

Typically, a game plugin creates instances of the core components from its own `PluginPresetConfigBase` implementation.

Example:

```csharp
using Hi3Helper.Hypergryph.Core.Management;
using Hi3Helper.Hypergryph.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;

public partial class ExamplePresetConfig : PluginPresetConfigBase
{
    private const string ExecutableName = "Game.exe";

    private const string ApiUrl = "https://launcher.hypergryph.com/api/proxy/batch_proxy";
    private const string WebApiUrl = "https://launcher.hypergryph.com/api/proxy/web/batch_proxy";

    private const string AppCode = "<game-app-code>";
    private const string LauncherAppCode = "<launcher-app-code>";
    private const string Channel = "1";
    private const string SubChannel = "1";
    private const string Seq = "5";

    public override ILauncherApiMedia? LauncherApiMedia
    {
        get => field ??= new HgLauncherApiMedia(
            WebApiUrl,
            AppCode,
            Channel,
            SubChannel,
            Seq);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= new HgLauncherApiNews(
            WebApiUrl,
            AppCode,
            Channel,
            SubChannel,
            Seq);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= new HgGameManager(
            ExecutableName,
            ApiUrl,
            WebApiUrl,
            AppCode,
            LauncherAppCode,
            Channel,
            SubChannel,
            Seq);
        set;
    }

    public override IGameInstaller? GameInstaller
    {
        get => field ??= new HgGameInstaller(GameManager);
        set;
    }
}
```

## HgGameManager

```csharp
new HgGameManager(
    executableName,
    apiUrl,
    webApiUrl,
    appCode,
    launcherAppCode,
    channel,
    subChannel,
    seq);
```

Responsibilities:

* Detects whether the game is installed.
* Reads the local game version from `config.ini`.
* Queries the Hypergryph launcher API for the latest version.
* Determines whether an update is available.
* Selects between a full package and an incremental patch.
* Exposes package metadata to the installer.

Installation is considered valid when:

* The game executable exists.
* A valid `config.ini` is present.

## HgGameInstaller

```csharp
new HgGameInstaller(GameManager);
```

Responsibilities:

* Calculates download size.
* Validates cached downloads.
* Supports resumable downloads.
* Downloads full or incremental packages.
* Performs MD5 verification.
* Extracts installation packages.
* Applies HDiffPatch incremental patches.
* Writes updated game files.

`HgGameInstaller` derives from `GameInstallerBase` and is intended to be consumed through the interfaces provided by `Hi3Helper.Plugin.Core`.

## HgGameRepairer

```csharp
var repairer = new HgGameRepairer(httpClient, gameManager, installPath);

await repairer.StartRepairAsync(
    progressDelegate,
    progressStateDelegate,
    cancellationToken);
```

Responsibilities:

* Downloads and decrypts the `game_files` manifest.
* Verifies local file hashes.
* Detects missing or corrupted files.
* Downloads replacement files from the resource server.
* Verifies repaired files after download.

## HgLauncherApiNews

```csharp
new HgLauncherApiNews(
    webApiUrl,
    appCode,
    channel,
    subChannel,
    seq);
```

Wraps the following launcher endpoints:

* `get_banner`
* `get_announcement`
* `get_sidebar`

Exposes:

* `GetNewsEntries()`
* `GetCarouselEntries()`
* `GetSocialMediaEntries()`

Currently, social media entries are returned as an empty collection because the official API does not consistently provide icon resources.

## HgLauncherApiMedia

```csharp
new HgLauncherApiMedia(
    webApiUrl,
    appCode,
    channel,
    subChannel,
    seq);
```

Wraps:

* `get_main_bg_image`

Exposes:

* `GetBackgroundEntries()`
* `GetBackgroundFlag()`
* `GetLogoFlag()`
* `GetLogoOverlayEntries()`
* `GetBackgroundSpriteFps()`

If both image and video assets are available, the video asset is preferred.

## Utility Classes

### HgCrypto

Utility for encrypting and decrypting Hypergryph launcher files.

```csharp
var text = HgCrypto.DecryptFileToString("config.ini");
var bytes = HgCrypto.DecryptFileToBytes("game_files");

HgCrypto.EncryptStringToFile(content, "config.ini");
```

Available methods:

* `DecryptFileToBytes()`
* `DecryptFileToString()`
* `DecryptBytesToString()`
* `EncryptStringToFile()`

### ConfigTool

Reads and parses the local game version.

```csharp
var content = ConfigTool.ReadConfig(configPath);
var version = ConfigTool.ParseVersion(content);
```

### MultiVolumeStream

Presents multiple archive volumes as a single continuous stream.

```csharp
using var stream = new MultiVolumeStream(volumePaths);
```

## API Models

`Management/Api/HgApiStructs.cs` contains the request and response models used by the Hypergryph launcher APIs, including:

* `HgBatchRequest`
* `HgProxyRequest`
* `HgGetLatestGameReq`
* `HgCommonReq`
* `HgBatchResponse`
* `HgGetLatestGameRsp`
* `HgPkgInfo`
* `HgPack`
* `HgPatchInfo`
* `HgPatchManifest`
* `HgGetBannerRsp`
* `HgGetAnnouncementRsp`
* `HgGetMainBgImageRsp`
* `HgGetSidebarRsp`
* `HgManifestNode`

These models are designed for `System.Text.Json` serialization and are used together with the generated `HgApiContext`.

## Typical Workflow

```text
PresetConfig
 ├─ HgGameManager
 │   ├─ Read config.ini
 │   ├─ Query get_latest_game
 │   └─ Select full package or patch
 │
 ├─ HgGameInstaller
 │   ├─ Validate cached downloads
 │   ├─ Download packages
 │   ├─ Verify MD5
 │   ├─ Extract archives
 │   └─ Apply patches
 │
 ├─ HgLauncherApiNews
 │   ├─ Retrieve announcements
 │   └─ Retrieve banners
 │
 └─ HgLauncherApiMedia
     └─ Retrieve launcher backgrounds
```

## Notes

* `HgGameManager` relies on `config.ini` to determine the installed game version.
* `HgGameInstaller` requires a properly initialized `HgGameManager`.
* Incremental updates depend on patch metadata returned by the official launcher API and `SharpHDiffPatch.Core`.
* Multi-volume archive extraction is implemented with `SevenZipExtractor` and `MultiVolumeStream`.
* `HgCrypto` implements Hypergryph's proprietary file encryption format and is **not** intended as a general-purpose cryptographic library.

## License

See the repository `LICENSE` file for licensing information.
