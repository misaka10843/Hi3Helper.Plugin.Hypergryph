# Hi3Helper.Hypergryph.Core

Hypergryph 游戏插件通用核心库，面向 Collapse Launcher 插件开发使用。

该库封装了鹰角启动器相关的通用能力，包括游戏版本查询、安装/更新、完整性修复、启动器新闻/轮播图/背景图接口，以及鹰角客户端加密配置文件解析等逻辑。`Hi3Helper.Plugin.Arknights`、`Hi3Helper.Plugin.Endfield` 等插件可基于本库快速接入不同游戏或不同渠道。

## 功能概览

`Hi3Helper.Hypergryph.Core` 主要暴露以下能力：

| 模块                   | 说明                          |
| -------------------- | --------------------------- |
| `HgGameManager`      | 游戏安装状态、版本、更新状态、包信息获取        |
| `HgGameInstaller`    | 完整安装、增量更新、断点续传、安装进度回调       |
| `HgGameRepairer`     | 基于 `game_files` 清单的完整性校验与修复 |
| `HgLauncherApiNews`  | 启动器公告、轮播图、新闻条目获取            |
| `HgLauncherApiMedia` | 启动器背景图/背景视频获取               |
| `HgCrypto`           | 鹰角加密文件 AES 解密/加密            |
| `ConfigTool`         | 读取并解析 `config.ini` 中的游戏版本   |
| `MultiVolumeStream`  | 将分卷压缩包模拟为连续 Stream，供解压逻辑使用  |
| `HgApiStructs`       | 鹰角启动器 API 请求/响应 DTO         |

## 项目引用

项目目标框架为：

```xml
<TargetFramework>net10.0</TargetFramework>
```

依赖：

```xml
<ProjectReference Include="..\Hi3Helper.Plugin.Core\Hi3Helper.Plugin.Core.csproj" />
<ProjectReference Include="..\SevenZipExtractor\SevenZipExtractor\SevenZipExtractor.csproj" />
<ProjectReference Include="..\SharpHDiffPatch.Core\SharpHDiffPatch.Core.csproj" />
<PackageReference Include="System.IO.Hashing" Version="10.0.1" />
```

## 基本接入方式

通常在具体游戏插件的 `PluginPresetConfigBase` 实现中创建 Core 提供的对象。

示例：

```csharp
using Hi3Helper.Hypergryph.Core.Management;
using Hi3Helper.Hypergryph.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;

public partial class ExamplePresetConfig : PluginPresetConfigBase
{
    private const string ExExecutableName = "Game.exe";

    private const string ExApiUrl = "https://launcher.hypergryph.com/api/proxy/batch_proxy";
    private const string ExWebApiUrl = "https://launcher.hypergryph.com/api/proxy/web/batch_proxy";

    private const string ExAppCode = "<game-app-code>";
    private const string ExLauncherAppCode = "<launcher-app-code>";
    private const string ExChannel = "1";
    private const string ExSubChannel = "1";
    private const string ExSeq = "5";

    public override ILauncherApiMedia? LauncherApiMedia
    {
        get => field ??= new HgLauncherApiMedia(
            ExWebApiUrl,
            ExAppCode,
            ExChannel,
            ExSubChannel,
            ExSeq);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= new HgLauncherApiNews(
            ExWebApiUrl,
            ExAppCode,
            ExChannel,
            ExSubChannel,
            ExSeq);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= new HgGameManager(
            ExExecutableName,
            ExApiUrl,
            ExWebApiUrl,
            ExAppCode,
            ExLauncherAppCode,
            ExChannel,
            ExSubChannel,
            ExSeq);
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
    gameExecutableNameByPreset,
    apiUrl,
    webApiUrl,
    appCode,
    launcherAppCode,
    channel,
    subChannel,
    seq);
```

负责：

* 判断游戏是否已安装
* 读取本地 `config.ini` 获取当前版本
* 请求鹰角 `get_latest_game` API 获取最新版本
* 判断是否有更新
* 判断当前应使用完整包还是增量包
* 向安装器暴露下载包信息

安装状态判断依赖：

* 游戏目录存在目标 exe
* 游戏目录存在 `config.ini`

## HgGameInstaller

```csharp
new HgGameInstaller(GameManager);
```

负责：

* 获取游戏包大小
* 校验已下载包
* 支持 `.tmp` 断点续传
* 下载完整包或增量包
* MD5 校验
* 解压安装包
* 应用 HDiffPatch 增量补丁
* 写入或更新游戏文件

该类继承自 `GameInstallerBase`，实际安装、大小查询、进度回调等入口由 `Hi3Helper.Plugin.Core` 的安装器接口调用。

## HgGameRepairer

```csharp
var repairer = new HgGameRepairer(httpClient, hgGameManager, installPath);

await repairer.StartRepairAsync(
    progressDelegate,
    progressStateDelegate,
    cancellationToken);
```

负责：

* 拉取并解密 `game_files` 文件清单
* 校验本地文件 MD5
* 找出缺失或损坏文件
* 从资源服务器重新下载损坏文件
* 下载后再次校验

## HgLauncherApiNews

```csharp
new HgLauncherApiNews(
    webApiUrl,
    appCode,
    channel,
    subChannel,
    seq);
```

负责请求：

* `get_banner`
* `get_announcement`
* `get_sidebar`

并暴露给启动器：

* `GetNewsEntries`
* `GetCarouselEntries`
* `GetSocialMediaEntries`

目前社交媒体入口由于 API 无法稳定提供图标，默认返回空列表。

## HgLauncherApiMedia

```csharp
new HgLauncherApiMedia(
    webApiUrl,
    appCode,
    channel,
    subChannel,
    seq);
```

负责请求：

* `get_main_bg_image`

并暴露给启动器：

* `GetBackgroundEntries`
* `GetBackgroundFlag`
* `GetLogoFlag`
* `GetLogoOverlayEntries`
* `GetBackgroundSpriteFps`

当 API 同时返回图片和视频时，会优先使用视频地址作为背景资源。

## 工具类

### HgCrypto

用于处理鹰角客户端加密文件。

```csharp
var text = HgCrypto.DecryptFileToString("config.ini");
var bytes = HgCrypto.DecryptFileToBytes("game_files");

HgCrypto.EncryptStringToFile(content, "config.ini");
```

提供：

* `DecryptFileToBytes`
* `DecryptFileToString`
* `DecryptBytesToString`
* `EncryptStringToFile`

### ConfigTool

用于读取并解析游戏版本。

```csharp
var content = ConfigTool.ReadConfig(configPath);
var version = ConfigTool.ParseVersion(content);
```

### MultiVolumeStream

用于把多个分卷文件作为一个连续流读取，避免物理合并分卷压缩包。

```csharp
using var stream = new MultiVolumeStream(volumePaths);
```

## API DTO

`Management/Api/HgApiStructs.cs` 中定义了鹰角启动器 API 使用的请求与响应结构，包括：

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

这些类型主要用于 `System.Text.Json` 序列化/反序列化，并配合 `HgApiContext` 源生成上下文使用。

## 典型调用链

```text
PresetConfig
 ├─ HgGameManager
 │   ├─ 读取 config.ini
 │   ├─ 请求 get_latest_game
 │   └─ 判断完整包 / 增量包
 │
 ├─ HgGameInstaller
 │   ├─ 校验本地下载缓存
 │   ├─ 下载包
 │   ├─ MD5 校验
 │   ├─ 解压
 │   └─ 应用补丁
 │
 ├─ HgLauncherApiNews
 │   ├─ 获取公告
 │   └─ 获取轮播图
 │
 └─ HgLauncherApiMedia
     └─ 获取启动器背景图 / 视频
```

## 注意事项

* `HgGameManager` 会通过 `config.ini` 判断本地版本，因此目标游戏目录需要包含有效的 `config.ini`。
* `HgGameInstaller` 依赖 `HgGameManager` 初始化后的包信息。
* 增量更新依赖 API 返回的 patch 信息以及 `SharpHDiffPatch.Core`。
* 分卷包解压依赖 `SevenZipExtractor`，`MultiVolumeStream` 用于规避单文件流限制。
* `HgCrypto` 中的 AES 参数为鹰角客户端文件格式专用，不建议用于通用加密场景。

## License

See repository `LICENSE`.
