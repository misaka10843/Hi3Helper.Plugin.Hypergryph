# 官方启动器父进程桥

> 状态：代码已实现并通过编译。接口、下载、校验、只取 `Games.exe`、验签、挂起父进程、清理这条链路已实测跑通，国服客户端日志里出现过 `Parent process check success`。但存在一个未解阻塞：`Games.exe` 清单要求管理员权限，非提权宿主创建不了它，见 6.5。
> 适用范围：仅《明日方舟：终末地》，中国大陆服与国际服。
> 最后更新：2026-10-16

## 0. 适用范围

| 范围 | 是否接入 | 说明 |
| --- | --- | --- |
| 终末地 中国大陆（`launcher.hypergryph.com`） | 接入 | 父进程校验与组件下载都已实测 |
| 终末地 国际服（`launcher.gryphline.com`，启动器叫 Gryphlink） | 接入 | appcode 与文件名已确认，下载链路已实测；受 6.5 的提权问题影响 |
| 明日方舟 国服 / B 服 | 不接入 | 没有这个问题，代码保持原样 |
| 明日方舟 国际服 / 日服（Yostar） | 不接入 | 没有这个问题，代码保持原样 |

父进程校验是终末地客户端自己的行为，由游戏目录里的 `OneChannelLC.dll` 实现。明日方舟系列不加载这个模块，所以那两个分支不动，免得引入没必要的风险。

## 1. 问题出在哪

终末地更新到 1.6.3 之后，插件直接 `CreateProcess` 拉起 `Endfield.exe`，游戏会弹「游戏启动失败 / 请通过官方启动器打开游戏」。

原因跟命令行参数无关，也跟 `U8Data\config\Launcher.meta` 无关。是游戏自己在启动时查了父进程。

### 1.1 检查是怎么做的

检查代码在游戏目录的 `OneChannelLC.dll` 里，由 `U8SDK.dll` 通过 `HGU8SDKSetData → U8SDKImpl::U8StartLaunchCheck` 调起来，顺序是：

1. `getpid()` 之后用 Toolhelp 快照读**直接父 PID**，不往上追祖先。
2. `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` 加 `QueryFullProcessImageNameW` 取父进程完整路径，再取文件名，跟配置里的 `parentNames` 逐项比。忽略大小写，按完整长度精确匹配。不中就返回 `10100`。
3. 拿这个路径去调 `WinVerifyTrust`（动作 GUID `WINTRUST_ACTION_GENERIC_VERIFY_V2`）。签名不可信或者读不出签名证书，返回 `10101`。
4. 用 `CryptQueryObject` / `CryptMsgGetParam` / `CertFindCertificateInStore` 枚举签名者与嵌套签名证书，读 SHA-1 与 SHA-256 指纹。配置里的 `fingerprints` 非空时，至少要有一项忽略大小写精确命中，否则返回 `10102`。

配置键是 `checkItems[]`，每项有 `name`、`parentNames`、`fingerprints`。

导出的三个函数（Windows x64 ordinal ABI）：

```cpp
// Ordinal 1：执行检查；userDataPath 下写入 sdklogs/OneChannelLC.log
uint32_t OCL_1(const char* userDataPath, const char* configJson);

// Ordinal 2：返回内部 JSON 结果字符串；调用方不释放
const char* OCL_2();

// Ordinal 3：尝试启动官方启动器
bool OCL_3(int isCN);
```

日志写在 `%USERPROFILE%\AppData\LocalLow\HyperGryph\Endfield\sdklogs\`，文件名是 `OneChannelLC.log` 和 `u8sdk_pc.log`。

### 1.2 由此得出的几条硬约束

- 它只认鹰角签名的父进程。自己编译一个同名 `Games.exe` 没用，第 3、4 步照样过不去。
- 签名相关的 API 名字是编码过的，运行时解码后再 `LoadLibraryExA` / `GetProcAddress` 动态取。导入表里没有这些名字，别拿导入表来否定签名校验。
- 系统里必须有一份真的官方 `Games.exe`，否则第 2 到 4 步无解。
- 它只看直接父进程。所以官方启动器不需要真的跑起来，只要游戏进程的父进程是那个官方 `Games.exe` 就行。

## 2. 启动流程

用户点「开始游戏」之后，插件按下面的顺序走：

```
开始
│
├─ 按顺序找可用的 Games.exe（见 2.1）
│   ① 上次记住的路径（picked_launcher.txt）
│   ② 游戏目录下的组件目录
│   ③ 注册表里的协议处理器目录
│   每一项都要过「文件存在 + WinVerifyTrust 通过 + 签名主体命中 Hypergryph / GRYPHLINE」
│     └─ 有 → 挂起父进程桥启动，用户零感知
│
└─ 都没找到，或者全部验签没过
      │
      ├─ 先查 get_latest_launcher，判断这个区域能不能下载
      │
      ├─ 弹原生提示（两次 MessageBoxW，不是 Collapse 的界面，也不是游戏弹框）
      │   ①「是否现在下载官方启动器组件（约 112.2 MB）？」
      │       是 → 下载
      │       否 → 进 ②
      │       取消 → 中止启动
      │   ②「是否手动选取已安装的官方启动器？」
      │       是 → 打开文件选择框
      │       否 → 中止启动
      │
      ├─ 下载分支
      │   1. POST get_latest_launcher，拿到 zip_package_url / md5 / package_size
      │   2. 弹进度窗口（自绘原生窗口，非模态），三个阶段依次显示：
      │        正在下载官方启动器组件… n%  →  正在校验下载文件…  →  正在解压…
      │        断点续传（Range，失败重试 3 次），百分比没变时不刷新界面
      │   3. 校验 MD5
      │   4. 解压，且只落盘 Games.exe 这一个条目
      │   5. 删掉 package.zip 和 .tmp
      │   6. 验签确认这份 Games.exe 真的可用
      │        可用 → MessageBoxW「组件已就绪，请再次点击『开始游戏』」→ 本次返回，不自动启动
      │        不可用 → MessageBoxW 提示下载失败 → 返回启动失败
      │
      ├─ 选取分支
      │   → GetOpenFileNameW 文件选择框，过滤器只列 Games.exe
      │   → 拿到路径后直接验签
      │        通过 → 把路径写进 picked_launcher.txt，下次直接命中 ① → 走桥启动
      │        失败 → MessageBoxW 提示，回到文件选择框
      │
      └─ 取消 → 返回启动失败
```

下载分支刻意**不自动启动游戏**。下载和解压要花十几秒到几十秒，中间用户可能已经切走了；直接接管拉起游戏会让人措手不及，而且组件刚落地就立刻用，出问题也不好定位。所以拿到组件、验签通过之后只弹一个确认框，把「再点一次开始游戏」交给用户。第二次点击会命中 ① 或 ②，走挂起父进程桥正常启动，之后每次启动都是零感知的。

选取分支不受这条约束：用户刚刚亲手点完文件选择框，立刻启动才是符合预期的，所以那条路是拿到就启动。

### 2.1 找现成的组件

`OfficialLauncherBridge.TryCreate(hintDirectory, hintExecutable, ...)` 会枚举候选可执行文件，取第一个「验签通过且能挂起创建成功」的返回。候选来自三处，按顺序：

- `hintExecutable`，也就是用户上次在文件选择框里选中的那个 `Games.exe`。路径记在 `picked_launcher.txt` 里（见 2.2），所以只需要选一次。
- `hintDirectory`，也就是游戏目录下的组件目录（见 2.2）。
- 注册表里的协议处理器目录。遍历 `HKCU` 与 `HKLM` 的 `Software\Classes`，取子键名以 `hypergryphlauncher` 或 `gryphlinelauncher` 开头的，读它的 `\shell\open\command`，从命令行里解析出 exe 路径，再取父目录。这一条只是为了「本机已经装了官方启动器」时不用打扰用户。

后两个是目录，每个只看两层：`<dir>\Games.exe` 和 `<dir>\<一级子目录>\Games.exe`。不递归，不搜 `Program Files` 的常规位置。文件名是常量 `Games.exe`，没有配置入口。

### 2.2 组件和记住的路径放在哪

都放在游戏安装目录下，按 `ProfileName` 分一层：

```
<游戏目录>\Hi3Helper\launcher\<ProfileName>\
    Games.exe              下载下来的组件（有的话）
    picked_launcher.txt    用户手选的官方启动器路径（有的话）
    package.zip / .tmp     下载中间文件，结束后自动删掉
```

游戏目录就是 `Endfield.exe` 所在的那一级，由宿主经 `IGameManager.GetGamePath` 提供。走到这一步时它必然非空，因为启动入口已经先确认过 `Endfield.exe` 存在。选这里是因为它不需要任何运行时自省，而且游戏目录本来就是可写的，插件的安装和修复管线就写在这。

`ProfileName` 是 `EndfieldCn` / `EndfieldBili` / `EndfieldGlobal`。分目录是因为国服和国际服的启动器是两份不同的发布物，同一台机器上可能两边都装了。

`picked_launcher.txt` 里只有一行，就是用户选中的那个 `Games.exe` 的绝对路径。它存在的唯一目的，是让用户不必每次启动都重选一遍。文件不存在、内容为空、或者路径上的文件已经不在了，都按没选过处理，重新走一遍寻找流程。

两者互不覆盖，`picked_launcher.txt` 排在 `Games.exe` 前面，因为那是用户自己指定的。删掉整个 `launcher` 目录等于完全重置，下次启动会重新询问。

不装进 `Program Files`，不建快捷方式，不注册协议处理器。

早期版本试过把组件放在插件自己的目录下，靠 `GetModuleHandleW` 解析自身模块路径来定位。这条路走不通：NativeAOT 会把 `Hypergryph.Core` 编进 `Endfield.dll` 这一个原生模块，进程里没有 `Hypergryph.Core.dll` 这个模块，查询直接返回 `ERROR_MOD_NOT_FOUND (126)`。换成 `GetModuleHandleExW(FROM_ADDRESS)` 在 AOT 下成立，但在 JIT 下必然失败（JIT 生成的代码不属于任何已加载模块），没法验证，所以放弃。

### 2.3 下载这一步

**接口。** 和游戏 API 同一个主机与入口：

```
国服：https://launcher.hypergryph.com/api/proxy/batch_proxy
国际：https://launcher.gryphline.com/api/proxy/batch_proxy
```

**`appcode` 与 `launcher_appcode` 的语义是对调的。** 这是最初反复拿到 `LAUNCHER_RECORD_NOT_FOUND` 的根因，写反了就永远查不到。

```json
{
  "seq": "5",
  "proxy_reqs": [
    {
      "kind": "get_latest_launcher",
      "get_latest_launcher_req": {
        "appcode": "abYeZZ16BPluCFyT",
        "launcher_appcode": "6LL0KJuqHBVz33WK",
        "channel": "1",
        "sub_channel": "1",
        "version": ""
      }
    }
  ]
}
```

| 字段 | 值 | 说明 |
| --- | --- | --- |
| `appcode` | `abYeZZ16BPluCFyT` | 启动器自己的 appcode。在游戏的请求里它叫 `launcher_appcode` |
| `launcher_appcode` | `6LL0KJuqHBVz33WK` | 游戏 appcode。在游戏的请求里它叫 `appcode` |
| `channel` / `sub_channel` | `1` / `1` | 国服 |
| `version` | `""` | 空表示不比对本地版本，直接取最新 |

**响应原文：**

```json
{
  "proxy_rsps": [
    {
      "kind": "get_latest_launcher",
      "get_latest_launcher_rsp": {
        "action": 1,
        "version": "1.6.0",
        "request_version": "",
        "zip_package_url": "https://launcher-sign.hycdn.cn/abYeZZ16BPluCFyT/launcher/1/1/official/1.6.0_YMRXC5D5c0y8U0M7/of9nAIGn/HypergryphLauncher_v1.6.0.1611_1_1_official.zip?auth_key=...",
        "md5": "30859dd39aa9b14fad2e0996633d22b7",
        "package_size": "117680606",
        "total_size": "375908442",
        "description": ""
      }
    }
  ],
  "timestamp": "1792115872420",
  "seq": "5"
}
```

`action` 只有两个取值。`0` 表示已是最新，此时 `zip_package_url` 为空、`package_size` 为 `0`；`1` 表示有更新包，返回 `zip_package_url` / `md5` / `package_size` / `total_size`。

响应里没有文件清单（没有 `file_list` 字段），所以没法按文件增量取，只能整包下载。

**实测数据：**

| 项 | 值 |
| --- | --- |
| 启动器版本 | `1.6.0`（`HypergryphLauncher_v1.6.0.1611_1_1_official.zip`） |
| 压缩包 | 117,680,606 字节（约 112.2 MB） |
| 解压后 | 375,908,442 字节（约 358.5 MB） |
| 启动器 CDN | `launcher-sign.hycdn.cn`，与游戏 CDN `beyond.hycdn.cn` 不同域 |
| 断点续传 | 支持，实测返回 `206` + `Content-Range` |
| 国服游戏版本 | `1.6.3`，CDN `beyond.hycdn.cn` |
| 国际服游戏版本 | `1.6.3`，CDN `beyond.hg-cdn.com`，`total_size` 130,923,419,249 字节 |

**其它已确认的端点：**

| 端点 | 说明 |
| --- | --- |
| `/api/launcher/get_latest` | 仅 GET；缺 `channel` + `sub_channel` 返回 `400 invalid param` |
| `/api/launcher/config/main_page/get` | 主页面配置 |
| `/api/launcher/config/protocol/get` | 协议版本 |
| `/api/game/get_latest` | 游戏信息 |
| `batch_get_launcher_action` | 批量按钮动作判定，不是文件清单 |

`Games.exe` 里的批量 kind 全集是 `get_protocol` / `get_latest_launcher` / `get_latest_game` / `batch_get_latest_game` / `batch_get_launcher_action`。响应键全集是 `get_protocol`、`get_latest_launcher_rsp`、`get_latest_game_rsp`、`batch_get_latest_game_rsp`、`batch_get_launcher_action_rsp`。

**排错时的状态码判据：**

| 响应 | 含义 |
| --- | --- |
| `400 INVALID_PARAM` | `channel` / `sub_channel` 对这个 `appcode` 不合法。不代表 appcode 未注册 |
| `404 LAUNCHER_RECORD_NOT_FOUND` | 参数合法，但这个 `appcode` 下没有启动器记录，`message` 是 `online version not match` |
| `404 RESOURCE_NOT_FOUND` | 该 kind 在该主机没有资源。`get_protocol` 是常态 |
| `405 Method Not Allowed` | 端点存在，method 错 |
| `404 page not found`（纯文本） | 路由不存在，路径猜错了 |

`400` 和 `404` 的分界不是「appcode 有没有注册」，而是「`channel` / `sub_channel` 对这个 `appcode` 合不合法」。要判断某个 appcode 有效，得先用一组 channel/sub 试探，不能只看单次状态码。

另外实测下来 `launcher_appcode` 这个字段基本不被校验。固定 `appcode` 与 `channel` / `sub_channel` 之后，把 `launcher_appcode` 换成空串、换成别的 appcode、换成任意字符串，响应完全一致。所以排错时只看 `appcode` 加 `channel` / `sub_channel` 两项就够了。

### 2.4 为什么必须整包下载

想过从 zip 里按偏移量单取 `Games.exe`，行不通。

读 `zip_package_url` 前 64 KB 解析本地文件头，第一条是 `deflate 781551 → 1646056 7z.dll`。三条理由任一条就足以否掉这个方案：条目是 deflate 压缩流，`usize ≠ csize`，物理偏移跟原始文件不对应；条目紧排，前面任何一个文件大小变了，后面所有偏移整体平移；deflate 流没法随机跳读，取第 N 个条目必须先解压前 N−1 个。

就算额外发一次 Range 请求解析尾部 Central Directory 拿到精确偏移，还是要下载并解压它前面的全部条目。所以只能整包下载，再在本地按文件名提取。

不过「整包下载」不等于「整包解压落盘」，见下一节。

### 2.5 只需要一个 Games.exe

**实测结论：一个 18 MB 的 `Games.exe` 就够，不需要同级的 Qt5 DLL，也不需要整包解压。**

| 场景 | 挂起创建 | 子进程真实父 PID == 挂起 Games.exe 的 PID |
| --- | --- | --- |
| 完整启动器目录（96 个文件，含全部 Qt5 DLL） | 成功 | 是，`432` == `432` |
| 只有 `Games.exe` 的隔离目录，无任何同级 DLL | 成功 | 是，`6508` == `6508` |

原因是 `CreateProcessW` 带 `CREATE_SUSPENDED` 时初始线程被挂起，`LdrpInitialize` 永不执行，静态导入表不会被解析，缺 `Qt5Core.dll` 之类不会导致创建失败。而游戏侧的校验只对父进程**文件**做 `QueryFullProcessImageNameW` 和 `WinVerifyTrust`，同样只要文件本身在。

收益是磁盘占用从 358 MB 降到 18 MB，也省掉了 358 MB 的解压落盘。

这条结论成立的前提是挂起父进程永远不 Resume。哪天要让官方启动器真正跑起来，就必须整包解压。

提取管线做过交叉验证。同一份官方包（`md5 = 30859dd39aa9b14fad2e0996633d22b7`，跟接口返回值一致），分别用 7-Zip 官方 CLI 和插件自己的 `ArchiveFile.ExtractAsync` 提取 `Games.exe`，两边都是 17,998,824 字节、MD5 `7cccfbf8c13ba792ca682f12cc946711`、CRC32 `BE36236E`，逐字节相同。

开发机上另有一份手工解压的 `1.6.0\Games.exe`，大小相同但 MD5 是 `f5eaa1ca696e7886ea53b223fdd007a6`，是另一个构建。两份都能过 `WinVerifyTrust` 和发布者校验，按第 3 节的判据都算有效。这也反证了「不比对版本号和哈希、只验签」这条判据是对的。

单次获取的实测耗时：查接口 + 下载 112.2 MB + 校验 MD5 + 只提取 `Games.exe` + 删中间包，两次分别测到 13.5 秒和 6.0 秒（差在带宽，不是代码路径变了）。

### 2.6 进度窗口与文案

**窗口是自绘的，不是 `IProgressDialog`。** 发布配置里 `PublishAot=true` 且 `BuiltInComInteropSupport` 没开，NativeAOT 下经典 COM 互操作不可用，`IProgressDialog` / `IFileOpenDialog` 这类靠 `[ComImport]` 接口拿到的对象都走不通。所以进度窗口用 `RegisterClassExW` + `CreateWindowExW` 自己搭：一个 `STATIC` 显示文案，一个 `msctls_progress32` 显示进度，跑在独立的 STA 线程上，自带消息循环。

`ProgressPrompt` 的对外接口只有三个：

```csharp
ProgressPrompt prompt = ProgressPrompt.Start(caption);   // 阻塞到窗口建出来为止
prompt.Report(LauncherProgressStage.Downloading, 42);    // 跨线程安全，内部走 SendMessage
prompt.Dispose();                                        // PostMessage(WM_CLOSE) 后 Join 线程
```

`Report` 允许在窗口创建失败时静默返回，所以宿主环境起不了窗口只会少一个提示，不会让下载失败。下载回调里按「百分比是否变化」节流，112 MB 的包大约只刷新 100 次界面。

窗口关闭按钮（右上角叉）只关窗口，不取消下载。

**文案全部走 i18n，中日英三语。** 语言由宿主经 `PluginBase.SetPluginLocaleId` 传进来，落在 `SharedStatic.PluginLocaleCode`（BCP-47，默认 `en-us`）。`PromptStrings.Current` 按首个子标签选择：

| 前缀 | 语言 |
| --- | --- |
| `zh` | 简体中文 |
| `ja` | 日本語 |
| 其它 | English |

仓库里原本没有任何 i18n 机制（没有 `.resx`，也没有 `ResourceManager`），所以用了一个 14 个字段的字符串表类，三份静态实例，`Current` 每次现取。带占位符的用 `{0}` / `{1:F1}`，靠 `string.Format` 填充；`{1:F1}` 是为了让包体积显示成 `112.2 MB` 而不是一长串小数。

覆盖的文案：两次询问框、文件选择框标题与过滤器名称、选错文件的警告、进度窗口标题与三个阶段、组件就绪、下载失败。

**验证方式。** 自绘窗口没法靠「能编译」证明，所以做了两层实测。

第一层，逐项断言。探针程序反射加载真实产出的 `Hypergryph.Core.dll`，断言三语字符串解析、窗口创建、`STATIC` 文本、`PBM_GETPOS` 进度条位置、三个阶段切换、`Dispose` 耗时与窗口销毁、两个通知框的正文，共 47 项全通过。再把窗口 `PrintWindow` 抓成位图人工看一遍。截图发现过一个真 bug：窗口背景色写成 `COLOR_BTNFACE + 2`，实际渲染成 `COLOR_GRAYTEXT` 的深灰，同时 `STATIC` 没处理 `WM_CTLCOLORSTATIC` 导致文字带白色底块。两处都改掉了。

第二层，打真实接口走一遍完整流程（详见第 7 节 T7 的第二段）。真下载 112.2 MB，进度窗口被观测到 24 条不同文案（`0%` 到 `99%` 共 22 个百分比，然后是「正在校验下载文件…」「正在解压…」），期间抓了 4 张窗口截图；两个弹窗被自动应答（询问框点「是」、就绪框点「确定」）；`EnsureAsync` 返回 `Acquired` 且不带桥；第二次调用返回 `Ready` 且不再弹窗。

## 3. 判断一份 Games.exe 能不能用

每次启动前对候选文件做三项检查：

1. 文件存在。
2. `WinVerifyTrust`（`WINTRUST_ACTION_GENERIC_VERIFY_V2`）通过。
3. 签名主体包含 `Hypergryph`、`GRYPHLINE` 或 `GRYPH FRONTIER` 之一。

三项都过就算有效，不比对版本号。这套检查同时充当更新失效的兜底：官方更新启动器之后，本地文件如果失效，检查自然不通过，会自动走一次重新获取，不需要任何轮询或版本比对。

按签名主体匹配而不是证书指纹，是因为硬编码指纹会在证书轮换后失效。两个区域的主体不同，所以关键字有三个：国服是 `CN="Shanghai Hypergryph Network Technology Co., Ltd."`（命中 `Hypergryph`），国际服是 `CN=GRYPH FRONTIER PTE. LTD.`（命中 `GRYPH FRONTIER`，见 6.3）。国服证书由 `DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1` 签发，SHA-1 指纹 `2B366F397E64A9494A553B70004E8CA1AAE02B9D`，有效期到 2027-08-17。

顺带更正一处旧注释：`dwProvFlags` 写的是 `0x100`，之前在这里标成 `WTD_CACHE_ONLY_URL_RETRIEVAL` 是错的——那个常量实际是 `0x1000`，`0x100` 是 `WTD_SAFER_FLAG`。两种取值在本机实测都返回 `0`，所以不影响结果，但描述已改正。

验签用的是 `WinVerifyTrust`，参数上 `dwUIChoice = 2`（静默）、`dwProvFlags = 0x100`（`WTD_SAFER_FLAG`）。签名主体靠 `X509Certificate.CreateFromSignedFile` 读。这个 API 在 .NET 里被标了 `SYSLIB0057` 废弃，但 `X509CertificateLoader` 没有等价能力，它只能读 PEM / DER 证书文件和 PKCS#12，没法从已签名的 PE 里取出签名者证书，所以保留调用并抑制该警告。

## 4. 挂起父进程桥怎么搭

```
1. CreateProcessW(官方 Games.exe, CREATE_SUSPENDED | CREATE_NO_WINDOW)
     主线程永不 Resume，因此不会进入启动器界面
2. InitializeProcThreadAttributeList(1)
   UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, hSuspendedGames)
3. CreateProcessW(Endfield.exe, ..., EXTENDED_STARTUPINFO_PRESENT)
4. WaitForExitAsync(游戏进程)
5. Dispose → TerminateProcess(hSuspendedGames) + CloseHandle
     只清理本次自建的挂起父进程，不终止既有启动器或游戏
```

全程只用 Windows 正规进程 API。不修改游戏 DLL，不 hook 游戏进程，不关闭任何检查，不伪造父进程。

**第 1 步有个前提：调用方必须是提权进程。** `Games.exe` 的清单要求管理员，非提权进程调
`CreateProcessW` 会直接拿到 `ERROR_ELEVATION_REQUIRED`（740），桥建不起来。详见 6.5。

## 5. 残留挂起进程的清理

挂起父进程由 `Dispose()` 终止。如果宿主进程被强杀（比如任务管理器结束 Collapse），它会残留。所以每次启动前，`OfficialLauncherProvider` 会终止**可执行文件路径位于组件目录下**的同名进程。

作用域刻意收窄到组件目录：选取分支里用户指定的官方安装目录不受影响，不会误杀用户真正在用的启动器。

## 6. 国际服（Gryphlink）

### 6.1 appcode 已定位：`TiaytKBUIEdoEwRT`

拿到国际服启动器整包后，对包内 4 个可执行文件跑跟国服同一条检索：

```bash
grep -aoE 'publish-launcher-[0-9]+.[A-Za-z0-9]{16}' Games.exe Launcher.exe Updater.exe Patch.exe
# -> publish-launcher-184\TiaytKBUIEdoEwRT      （四个文件一致）
```

`TiaytKBUIEdoEwRT` 在 `Games.exe` 里出现 142 次，形态全是
`D:\work\publish-launcher-184\TiaytKBUIEdoEwRT\1.6.0.1607\11\HG\Kernel\...` 这样的
`__FILE__` 编译路径常量。国服是 `publish-launcher-184\abYeZZ16BPluCFyT\1.6.0.1604\11\HG\...`，
两边的 `184` 相同，再次印证同一套启动器代码库按区域分别构建。

### 6.2 接口实测：`channel` / `sub_channel` 必须是 6/6

用 `TiaytKBUIEdoEwRT` 打 `launcher.gryphline.com`，扫 channel × sub_channel：

| channel | sub_channel | 结果 |
| --- | --- | --- |
| 6 | 1 | 200 |
| 6 | 6 | 200 |
| 6 | 2 / 100 / 802 / 900 | 404 |
| 1 / 2 / 3 / 7 / 10 | 1 / 2 / 6 / 100 / 802 / 900 | 404 |

两个 200 返回同一个包：

```json
{"action":1,"version":"1.6.0","request_version":"",
 "zip_package_url":"https://launcher.hg-cdn.com/TiaytKBUIEdoEwRT/launcher/6/6/official/1.6.0_s2nHVJbcPQrJnjb8/2BI9Aq6G/GRYPHLINK_v1.6.0.1612_6_6_official.zip",
 "md5":"d4575a6643792fae65ffcff58beb704c","package_size":"117679901","total_size":"375902887"}
```

国际服启动器 CDN 是 `launcher.hg-cdn.com`（国服是 `launcher-sign.hycdn.cn`），而且**不带 `auth_key`**，
可以直接下载。整包实测：117,679,901 字节、9.3 s、MD5 `d4575a6643792fae65ffcff58beb704c` 与接口一致；
包内 247 个条目，`Games.exe` 在根目录，17,998,728 字节。

`EndfieldGlobalPreSetConfig` 的 `ExChannel` / `ExSubChannel` 本来就是 `6` / `6`，
只需要把 `ExLauncherAppCode` 从游戏 appcode 改成 `TiaytKBUIEdoEwRT`。

`EndfieldPlayPreSetConfig`（Google Play 通道，`sub_channel = 802`）**改不动**：
`get_latest_launcher` 在 6/802 返回 404（`get_latest_game` 正常，包名带 `_googleplay`），
说明该通道没有独立的启动器记录。它会落到「拿不到下载包，只能手动选取」那条路。

### 6.3 验签主体跟国服不一样

`TrustedPublishers` 原先只列 `Hypergryph` / `GRYPHLINE`，那是按国服实测定的：

| 区域 | 证书主体 | 命中的关键字 |
| --- | --- | --- |
| 国服 | `CN="Shanghai Hypergryph Network Technology Co., Ltd.", O=..., S=shanghai, C=CN` | `Hypergryph` |
| 国际服 | `CN=GRYPH FRONTIER PTE. LTD., O=GRYPH FRONTIER PTE. LTD., L=Singapore, C=SG` | `GRYPH FRONTIER` |

国际服主体里既没有 `Hypergryph` 也没有 `GRYPHLINE`，`Contains` 全部落空，
`IsTrustedPublisher` 返回 false，启动器会被当成「非官方组件」拒掉。已把 `GRYPH FRONTIER` 加进列表。
两边 `WinVerifyTrust` 都返回 `0`（链完整、带时间戳），签发者都是
`DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1`。

### 6.4 启动器文件名确认就是 `Games.exe`

Gryphlink 包内主程序就叫 `Games.exe`（17,998,728 字节），跟国服同名。
`OfficialLauncherBridge` / `OfficialLauncherProvider` 里硬编码的 `Games.exe` 对国际服同样成立。
`Launcher.exe`（`asInvoker`）是另一个组件，不是被校验的那个。
国际服注册的协议 scheme 是 `gryphlinklauncher`（UTF-16 字符串），已在 `ProtocolPrefixes` 里。

### 6.5 未解阻塞：`Games.exe` 要求管理员，非提权进程创建不了

**这比 appcode 更硬，而且国服国际服都一样。**

两个区域的 `Games.exe` 清单里都是：

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
```

对照：`Launcher.exe` 是 `asInvoker`，`Updater.exe` / `Patch.exe` 是 `requireAdministrator`。

在 Medium 完整性、`TokenElevation = 0` 的普通用户会话里实测 `CreateProcessW`：

| 目标 | flags | 结果 |
| --- | --- | --- |
| `notepad.exe` | `CREATE_SUSPENDED \| CREATE_NO_WINDOW` | True |
| 国服 `Games.exe` | `CREATE_SUSPENDED \| CREATE_NO_WINDOW` | **False, err 740** |
| 国际服 `Games.exe` | `CREATE_SUSPENDED \| CREATE_NO_WINDOW` | **False, err 740** |
| 国际服 `Games.exe` | `CREATE_SUSPENDED` | False, err 740 |
| 国际服 `Games.exe` | `CREATE_NO_WINDOW` | False, err 740 |

`740` = `ERROR_ELEVATION_REQUIRED`。所以 `OfficialLauncherBridge.TryCreateSuspended`
在非提权宿主里必然失败，`TryCreate` 对所有候选都返回 false，桥永远建不起来。

之前没发现，是因为所有端到端测试都在 Administrator 会话里跑。真机 `OneChannelLC.log` 里两种结果并存：

```text
[10-15 17:28] Not found matched name
              errorCode:10100, filePath: ...\collapselauncher.exe     <- Collapse 直接拉起，被拒
[10-15 17:35] Parent process check success:27132
              filePath: d:\misaka10843\downloads\1.6.0\games.exe       <- 父进程是启动器，通过
```

顺带确认了 `parentNames` 接受 `Games.exe`，且按完整路径取文件名匹配。

> 诊断坑：`Marshal.GetLastWin32Error()` 必须**紧接**在 P/Invoke 之后读。中间插一句
> `Console.WriteLine` 会把 last-error 冲成 0，看起来像「返回 false 但错误码是 0」。

可选处理方向（尚未选定，需要产品决策）：

| 方案 | 代价 |
| --- | --- |
| 要求 Collapse 以管理员运行，检测到 740 时给出明确提示 | 实现最简单，但把提权强加给整个启动器 |
| `ShellExecuteEx(runas)` 拉起提权小助手，由它创建挂起 `Games.exe` 再挂载游戏 | 每次启动弹一次 UAC；游戏会以提权令牌运行 |
| `ShellExecuteEx(runas)` 直接拉 `Games.exe`，再立刻 `NtSuspendProcess` | 不需要独立组件，但返回的句柄不一定有 `PROCESS_SUSPEND_RESUME`，且有竞态 |

### 6.6 国际服客户端是否也做父进程校验

`OneChannelLC.dll` 由游戏客户端自带，`checkItems[]` 来自服务端。国际服客户端是否加载这个模块、
`parentNames` 取什么值，仍未验证。国服实测 `parentNames` 接受 `Games.exe`（见 6.5 的日志），
国际服大概率相同，但没有证据。

## 7. 待办

| 编号 | 内容 | 状态 |
| --- | --- | --- |
| T1 | 原生提示形态 A（两次 `MessageBoxW`） | 已实现 |
| T2 | `get_latest_launcher` DTO 与 `GetLatestLauncherAsync` | 已实现，实测 200 |
| T3 | `OfficialLauncherBridge` | 已实现，实测父 PID 一致 |
| T4 | 组件目录 + 复用下载管线 + 单条目解压 | 已实现 |
| T5 | 接入 `Hi3Helper.Plugin.Endfield/Exports.GameLaunch.cs` | 已实现，Arknights / Yostar 未改动 |
| T6 | 选取分支 | 已实现。文件选择框改用 `GetOpenFileNameW`，过滤器只列 `Games.exe`；选中的路径写进 `picked_launcher.txt`，之后不再询问 |
| T7 | 真机端到端：下载 → 解压 → 取文件 → 桥启动 → `OneChannelLC.log` 出现 `Parent process check success` | 自动化链路已实测通过（含第三方交叉验证），游戏内校验尚未实测 |
| T8 | 取得国际服（Gryphlink）启动器 appcode | 已解：`TiaytKBUIEdoEwRT`，`channel`/`sub_channel` = 6/6，实测 200 且包 MD5 一致，见 6.1 / 6.2 |
| T9 | 验证国际服启动器可执行文件名 | 已解：也叫 `Games.exe`，见 6.4 |
| T10 | 验证国际服客户端是否也执行父进程校验 | 未验证，见 6.6 |
| T11 | 下载进度窗口 + 就绪/失败弹窗 + 中日英三语 i18n | 已实现。47 项断言全通过，窗口渲染经截图人工确认 |
| T12 | 下载完成后不自动启动，交给用户再点一次 | 已实现，见 2 节流程说明 |
| T13 | **非提权宿主下 `Games.exe` 创建失败（`ERROR_ELEVATION_REQUIRED` 740）** | **未解，阻塞整个桥，见 6.5** |
| T14 | 国际服验签主体与国服不同（`GRYPH FRONTIER`） | 已修：加入 `TrustedPublishers`，见 6.3 |

T7 里已经跑通的部分：

```
[0] 组件目录 = <游戏目录>\Hi3Helper\launcher\EndfieldCn
[1] TryAcquireAsync（查接口 -> 下载 -> 校验 MD5 -> 提取 Games.exe）
    acquired = True   elapsed = 13.5s
    目录内容：Games.exe  17998824 bytes
[2] 与 7-Zip 官方 CLI 提取结果逐字节一致
[3] OfficialLauncherBridge.TryCreate(组件目录)  -> OK
[4] 挂起的 Games.exe pid = 6316
[5] StartGame(ping.exe -n 8 127.0.0.1)  -> 子进程 pid = 5536
[6] 子进程的父 pid = 6316  -> PASS，父进程就是那个挂起的官方 Games.exe
[7] Dispose 后挂起父进程已清理  -> PASS
```

T11 / T12 落地后，用真实国服接口把新流程整条跑了一遍：

```
[0] 临时游戏目录（全新，无缓存）
[1] EnsureAsync 第一次（期望走下载分支）
    Status = Acquired   Bridge = null       <-- 不自动启动
    elapsed = 6.0s
    Games.exe 已落盘 17998824 bytes
    组件目录只剩 Games.exe（package.zip / .tmp 已清理）
[2] 进度窗口观测到 24 条不同文案：
    正在下载官方启动器组件… 0% / 5% / 10% / ... / 93% / 99%
    正在校验下载文件…
    正在解压…
    期间抓了 4 张窗口截图
[3] 弹窗：2 个，自动应答
    [1] 点「是」   <- 询问框（含「约 112.2 MB」）
    [2] 点「确定」 <- 官方启动器组件已就绪。请再次点击「开始游戏」来启动游戏。
[4] EnsureAsync 第二次（期望命中缓存）
    Status = Ready   Bridge = not null      <-- 这次才直接启动
    没有再多弹窗
[5] 挂起的 Games.exe pid = 4988
    子进程 pid = 2776，父 pid = 4988  -> PASS
[6] Dispose 后挂起父进程已清理  -> PASS
```

还没验证的是终末地客户端自己放不放行。需要在一台装了终末地 1.6.3 的机器上实际启动一次，然后看

```
%USERPROFILE%\AppData\LocalLow\HyperGryph\Endfield\sdklogs\OneChannelLC.log
```

里有没有出现 `Parent process check success`、内层 `code` 是不是 `0`。在这之前，不要说「完整链路已验证」。

## 8. 明确不做

| 项 | 原因 |
| --- | --- |
| 监控官方启动器更新 / 轮询版本 | 明确不做，由第 3 节的本地验签兜底 |
| 内置 `Games.exe` 到发布物 | 属于再分发受版权保护的二进制 |
| 伪造签名 / 自编译同名 `Games.exe` | 满足不了第 1.1 节的第 3、4 步，也触碰法律与反作弊红线 |
| 篡改 `OneChannelLC.dll` / 内存 patch | 触碰客户端完整性校验，账号级风险 |
| Hook 游戏进程 | ACE 反作弊会阻断或封号 |
| zip 偏移量单文件提取 | 已证伪，见 2.4 |
| 静默下载大文件 | 112 MB 的下载必须经用户确认 |
| 在系统里安装官方启动器 | 跟插件存在的意义冲突，只提取单个可执行文件 |

## 9. 参考

- 第三方研究：Better-Endfield `docs/host/research/1.6.3/launcher-check/LAUNCHER_CHECK.md`
  - 已在其 CN 1.6.3 真机验证过桥方案：`Parent process check success:29624`，内层 `code` 为 `0`
  - 生产实现：`native/loaders/shared/official_launcher.cpp`
- 相关代码：
  - `Hi3Helper.Hypergryph.Core/Management/HgGameManager.cs`（API 请求组装）
  - `Hi3Helper.Hypergryph.Core/Management/HgGameInstaller.cs`（安装 / 修复管线）
  - `Hi3Helper.Hypergryph.Core/Management/OfficialLauncherBridge.cs`
  - `Hi3Helper.Hypergryph.Core/Management/OfficialLauncherProvider.cs`
  - `Hi3Helper.Hypergryph.Core/Management/NativePrompt.cs`（原生询问框与文件选择框）
  - `Hi3Helper.Hypergryph.Core/Management/ProgressPrompt.cs`（自绘进度窗口）
  - `Hi3Helper.Hypergryph.Core/Management/PromptStrings.cs`（中日英三语文案表）
  - `Hi3Helper.Hypergryph.Core/Management/HgFileDownloader.cs`
  - `Hi3Helper.Hypergryph.Core/Management/Api/HgApiStructs.cs`、`HgApiContext.cs`（DTO）
  - `Hi3Helper.Plugin.Endfield/Exports.GameLaunch.cs`（启动入口）
  - `Hi3Helper.Plugin.Core/Utility/Windows/PInvoke.cs`（现有 P/Invoke 基建，`LibraryImport` 风格）
