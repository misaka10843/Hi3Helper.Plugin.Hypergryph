# 官方启动器父进程桥

终末地 1.6.3 起，插件直接 `CreateProcess` 拉起 `Endfield.exe` 会被游戏拒掉，弹「请通过官方启动器打开游戏」。绕过办法是拿官方启动器的 `Games.exe` 做挂起父进程，游戏挂在它下面启动。

只涉及《明日方舟：终末地》的中国大陆服与国际服。明日方舟系列不加载那个检查模块，代码没动。

> **启动路径已经换掉了。** 现在用的是游戏自带文件做的诱饵父进程，不需要下载官方启动器，见 `DECOY_PARENT_BRIDGE.md`。本文档记录的官方启动器方案代码完整保留在仓库里，但启动入口不调用（`Exports.GameLaunch.cs` 里的 `AcquireOfficialLauncher = false`）。下面的内容作为逆向记录和备用方案留着。

**宿主必须已提权。** `Games.exe` 清单要求管理员，非提权进程创建它只会拿到 740。Collapse 本来就以管理员运行，这是既有前提而不是缺陷，详见 6.5。

最后更新 2026-10-16。

## 1. 问题

游戏启动时自己查了父进程。跟命令行参数无关，跟 `U8Data\config\Launcher.meta` 也无关。

### 1.1 检查流程

代码在游戏目录的 `OneChannelLC.dll`，由 `U8SDK.dll` 经 `HGU8SDKSetData → U8SDKImpl::U8StartLaunchCheck` 调起。四步：

1. Toolhelp 快照取直接父 PID，不往上追祖先。
2. `QueryFullProcessImageNameW` 取父进程完整路径，拿文件名跟配置里的 `parentNames` 比。忽略大小写，按完整长度精确匹配，不中返回 `10100`。
3. 该路径过 `WinVerifyTrust`（`WINTRUST_ACTION_GENERIC_VERIFY_V2`）。签名不可信或取不到证书，返回 `10101`。
4. `CryptQueryObject` / `CryptMsgGetParam` / `CertFindCertificateInStore` 枚举签名者与嵌套证书，读 SHA-1 与 SHA-256 指纹。`fingerprints` 非空时至少要有一项命中，否则返回 `10102`。

配置键是 `checkItems[]`，每项含 `name` / `parentNames` / `fingerprints`。日志在
`%USERPROFILE%\AppData\LocalLow\HyperGryph\Endfield\sdklogs\`，文件名 `OneChannelLC.log` 与 `u8sdk_pc.log`。

### 1.2 由此得到的约束

它只认鹰角签名的父进程，自己编个同名 `Games.exe` 过不了第 3、4 步。签名相关的 API 名字是编码的，运行时解码后 `LoadLibraryExA` / `GetProcAddress` 动态取，导入表里查不到，别拿导入表否定签名校验。系统里得有一份真的官方 `Games.exe`。

好在它只看直接父进程，所以官方启动器不必真的跑起来，游戏进程的父进程是那个 `Games.exe` 就行。

## 2. 启动流程

用户点「开始游戏」之后：

```
开始
├─ 找现成的 Games.exe（见 2.1），每项都要过「文件在 + WinVerifyTrust + 主体命中」
│    ① picked_launcher.txt 记住的路径
│    ② 游戏目录下的组件目录
│    ③ 注册表里的协议处理器目录
│    找到 → 挂起父进程桥启动，用户零感知
│
└─ 没找到 → 查 get_latest_launcher，看这个区域能不能下载
     ├─ 弹 MessageBoxW ①「是否现在下载官方启动器组件（约 112.2 MB）？」
     │     是 → 下载分支
     │     否 → 弹 ②「是否手动选取已安装的官方启动器？」→ 是走选取分支，否中止
     │     取消 → 中止
     ├─ 下载分支
     │    POST 拿 url / md5 / size → 进度窗口（下载 → 校验 → 解压）
     │    → 校验 MD5 → 只落盘 Games.exe → 删中间文件 → 验签
     │    → 弹「组件已就绪，请再次点击开始游戏」，本次返回，不启动
     └─ 选取分支
          GetOpenFileNameW，过滤器只列 Games.exe → 验签
          通过 → 写进 picked_launcher.txt，直接启动
          失败 → 提示，回到选择框
```

下载分支故意不自动启动游戏。下载加解压要十几秒到几十秒，中间用户可能已经切走了，这时直接接管拉起游戏很突兀，组件刚落地就用也不方便定位问题。所以拿到组件、验签通过只弹一个确认框，把「再点一次」交给用户。第二次点击命中 ① 或 ②，走桥正常启动，之后都是零感知的。

选取分支不受这条约束，用户刚亲手点完文件选择框，拿到就启动才符合预期。

### 2.1 候选从哪来

`OfficialLauncherBridge.TryCreate(hintDirectory, hintExecutable, ...)` 枚举候选，返回第一个「验签通过且挂起创建成功」的：

- `hintExecutable`，用户上次在文件选择框选中的 `Games.exe`，路径记在 `picked_launcher.txt`，所以只需选一次。
- `hintDirectory`，游戏目录下的组件目录，见 2.2。
- 注册表协议处理器目录。遍历 `HKCU` / `HKLM` 的 `Software\Classes`，取子键名以 `hypergryphlauncher` 或 `gryphlinelauncher` 开头的，读 `\shell\open\command`，从命令行解析出 exe 路径再取父目录。这一条是给「本机已经装了官方启动器」准备的，省得再打扰用户。

后两个是目录，每个只看两层：`<dir>\Games.exe` 和 `<dir>\<一级子目录>\Games.exe`。不递归，也不去搜 `Program Files` 的常规位置。文件名是常量 `Games.exe`，没有配置入口。

### 2.2 文件放哪

都在游戏安装目录下，按 `ProfileName` 分一层：

```
<游戏目录>\Hi3Helper\launcher\<ProfileName>\
    Games.exe              下载来的组件
    picked_launcher.txt    用户手选的启动器路径
    package.zip / .tmp     下载中间文件，结束后删
```

游戏目录就是 `Endfield.exe` 所在那一级，宿主经 `IGameManager.GetGamePath` 给。走到这里它必然非空，因为启动入口已经先确认过 `Endfield.exe` 存在。选这里是因为不需要任何运行时自省，而且游戏目录本来就可写，插件的安装与修复管线也写在这。

`ProfileName` 取 `EndfieldCn` / `EndfieldBili` / `EndfieldGlobal`。分目录是因为国服和国际服的启动器是两份不同的发布物，一台机器上可能两边都装。

`picked_launcher.txt` 里只有一行，就是选中的 `Games.exe` 绝对路径，唯一作用是让用户不必每次重选。文件不存在、内容为空、或者路径上的文件没了，都按没选过处理。

两者互不覆盖，`picked_launcher.txt` 排在 `Games.exe` 前面，那是用户自己指定的。删掉整个 `launcher` 目录等于重置，下次启动重新询问。

不装进 `Program Files`，不建快捷方式，不注册协议处理器。

早期版本想把组件放在插件自己的目录下，靠 `GetModuleHandleW` 解析自身模块路径定位。走不通：NativeAOT 会把 `Hypergryph.Core` 编进 `Endfield.dll` 一个原生模块，进程里没有 `Hypergryph.Core.dll`，查询直接返回 `126`。换 `GetModuleHandleExW(FROM_ADDRESS)` 在 AOT 下成立但在 JIT 下必失败，没法验证，放弃。

### 2.3 下载接口

跟游戏 API 同一个主机同一个入口：

```
国服  https://launcher.hypergryph.com/api/proxy/batch_proxy
国际  https://launcher.gryphline.com/api/proxy/batch_proxy
```

**`appcode` 与 `launcher_appcode` 的语义是对调的。** 这是最初一直拿到 `LAUNCHER_RECORD_NOT_FOUND` 的根因，写反了永远查不到。

```json
{"seq":"5","proxy_reqs":[{"kind":"get_latest_launcher","get_latest_launcher_req":{
  "appcode":"abYeZZ16BPluCFyT",
  "launcher_appcode":"6LL0KJuqHBVz33WK",
  "channel":"1","sub_channel":"1","version":""}}]}
```

`appcode` 是启动器自己的，在游戏的请求里它叫 `launcher_appcode`；`launcher_appcode` 是游戏 appcode，在游戏请求里叫 `appcode`。`version` 留空表示不比对本地版本，直接取最新。

响应：

```json
{"proxy_rsps":[{"kind":"get_latest_launcher","get_latest_launcher_rsp":{
  "action":1,"version":"1.6.0","request_version":"",
  "zip_package_url":"https://launcher-sign.hycdn.cn/abYeZZ16BPluCFyT/launcher/1/1/official/1.6.0_YMRXC5D5c0y8U0M7/of9nAIGn/HypergryphLauncher_v1.6.0.1611_1_1_official.zip?auth_key=...",
  "md5":"30859dd39aa9b14fad2e0996633d22b7",
  "package_size":"117680606","total_size":"375908442","description":""}}],
 "timestamp":"1792115872420","seq":"5"}
```

`action` 只有两个值：`0` 已是最新，此时 `zip_package_url` 为空、`package_size` 为 `0`；`1` 有更新包。响应里没有 `file_list`，拿不到文件清单，只能整包下载。

实测：

| 项 | 值 |
| --- | --- |
| 国服启动器包 | `HypergryphLauncher_v1.6.0.1611_1_1_official.zip`，117,680,606 字节（解压后 375,908,442） |
| 启动器 CDN | 国服 `launcher-sign.hycdn.cn`，与游戏 CDN `beyond.hycdn.cn` 不同域 |
| 断点续传 | 支持，实测 `206` + `Content-Range` |
| 游戏版本 | 国服 1.6.3；国际服 1.6.3，CDN `beyond.hg-cdn.com`，`total_size` 130,923,419,249 字节 |

排错时看状态码：

| 响应 | 含义 |
| --- | --- |
| `400 INVALID_PARAM` | `channel` / `sub_channel` 对这个 `appcode` 不合法。不代表 appcode 没注册 |
| `404 LAUNCHER_RECORD_NOT_FOUND` | 参数合法，但这个 `appcode` 下没有启动器记录 |
| `404 RESOURCE_NOT_FOUND` | 该 kind 在该主机没资源，`get_protocol` 是常态 |
| `405 Method Not Allowed` | 端点存在，method 错 |
| `404 page not found`（纯文本） | 路由不存在，路径猜错了 |

`400` 和 `404` 的分界不是「appcode 有没有注册」，而是「`channel` / `sub_channel` 对这个 `appcode` 合不合法」。判断一个 appcode 有效得先用一组 channel/sub 试探，单看一次状态码没意义。

另外 `launcher_appcode` 基本不被校验。固定 `appcode` 和 `channel` / `sub_channel` 之后，把它换成空串、换成别的 appcode、换成任意字符串，响应完全一样。排错只看 `appcode` 加 `channel` / `sub_channel` 就够了。

同一主机上还有 `/api/launcher/get_latest`（仅 GET，缺 `channel` + `sub_channel` 返回 `400`）、`/api/launcher/config/main_page/get`、`/api/launcher/config/protocol/get`、`/api/game/get_latest`。`Games.exe` 里的批量 kind 全集是 `get_protocol` / `get_latest_launcher` / `get_latest_game` / `batch_get_latest_game` / `batch_get_launcher_action`。

### 2.4 为什么必须整包下载

想过从 zip 里按偏移量单取 `Games.exe`，不行。读 `zip_package_url` 前 64 KB，第一条本地文件头是 `deflate 781551 → 1646056 7z.dll`。条目是 deflate 压缩流，`usize ≠ csize`，物理偏移跟原始文件对不上；条目紧排，前面任何文件大小变了后面全部平移；deflate 没法随机跳读，取第 N 个条目得先解压前 N−1 个。这三条任一条就够否掉方案。

就算额外发一次 Range 请求解析尾部 Central Directory 拿到精确偏移，还是要下载并解压它前面的全部条目。所以只能整包下载，本地按文件名提取。不过整包下载不等于整包解压落盘。

### 2.5 只取 Games.exe

一个 18 MB 的 `Games.exe` 就够，不需要同级 Qt5 DLL，也不需要整包解压。

| 场景 | 挂起创建 | 子进程父 PID == 挂起 Games.exe 的 PID |
| --- | --- | --- |
| 完整启动器目录（96 个文件，含全部 Qt5 DLL） | 成功 | 是，`432` == `432` |
| 只有 `Games.exe` 的隔离目录 | 成功 | 是，`6508` == `6508` |

`CreateProcessW` 带 `CREATE_SUSPENDED` 时初始线程被挂起，`LdrpInitialize` 不执行，静态导入表不解析，缺 `Qt5Core.dll` 之类不会导致创建失败。游戏侧的校验只对父进程文件做 `QueryFullProcessImageNameW` 和 `WinVerifyTrust`，也只要文件本身在。磁盘占用从 358 MB 降到 18 MB。

前提是挂起父进程永不 Resume。哪天要让官方启动器真的跑起来，就得整包解压。

提取管线做过交叉验证：同一份官方包（`md5 = 30859dd39aa9b14fad2e0996633d22b7`，跟接口返回一致），7-Zip 官方 CLI 和插件自己的 `ArchiveFile.ExtractAsync` 各提一次 `Games.exe`，都是 17,998,824 字节、MD5 `7cccfbf8c13ba792ca682f12cc946711`、CRC32 `BE36236E`，逐字节相同。

开发机上另有一份手工解压的 `1.6.0\Games.exe`，大小一样但 MD5 是 `f5eaa1ca696e7886ea53b223fdd007a6`，是另一个构建。两份都过 `WinVerifyTrust` 和发布者校验，按第 3 节的判据都算有效，也反证了「不比版本号哈希、只验签」是对的。

单次获取实测两次分别 13.5 秒和 6.0 秒，差在带宽，不是代码路径变了。

### 2.6 进度窗口与文案

窗口是自绘的，不是 `IProgressDialog`。发布配置里 `PublishAot=true` 且没开 `BuiltInComInteropSupport`，NativeAOT 下经典 COM 互操作不可用，`IProgressDialog` / `IFileOpenDialog` 这类靠 `[ComImport]` 拿的对象都走不通。所以用 `RegisterClassExW` + `CreateWindowExW` 自己搭：一个 `STATIC` 显示文案，一个 `msctls_progress32` 显示进度，跑在独立 STA 线程上，自带消息循环。

```csharp
ProgressPrompt prompt = ProgressPrompt.Start(caption);   // 阻塞到窗口建出来
prompt.Report(LauncherProgressStage.Downloading, 42);    // 跨线程安全，内部走 SendMessage
prompt.Dispose();                                        // PostMessage(WM_CLOSE) 后 Join
```

`Report` 在窗口创建失败时静默返回，所以宿主起不了窗口只会少个提示，不会让下载失败。回调按百分比变化节流，112 MB 的包大约刷新 100 次界面。窗口右上角的叉只关窗口，不取消下载。

文案走 i18n，中日英三语。语言由宿主经 `PluginBase.SetPluginLocaleId` 传进来，落在 `SharedStatic.PluginLocaleCode`（BCP-47，默认 `en-us`），`PromptStrings.Current` 按首个子标签选：`zh` 中文，`ja` 日文，其它英文。仓库原本没有任何 i18n 机制，所以用一个 14 字段的字符串表类加三份静态实例。带占位符的用 `{0}` / `{1:F1}` 靠 `string.Format` 填，`{1:F1}` 是为了把包体积显示成 `112.2 MB`。覆盖两次询问框、文件选择框标题与过滤器名、选错文件的警告、进度窗口标题与三个阶段、组件就绪、下载失败。

验证做了两层。一层是断言：探针反射加载真实产出的 `Hypergryph.Core.dll`，断言三语字符串解析、窗口创建、`STATIC` 文本、`PBM_GETPOS` 进度位置、三个阶段切换、`Dispose` 与窗口销毁、两个通知框正文，47 项全过；再把窗口 `PrintWindow` 抓图人工看，抓出过一个真 bug：背景色写成 `COLOR_BTNFACE + 2` 实际渲染成 `COLOR_GRAYTEXT` 的深灰，`STATIC` 又没处理 `WM_CTLCOLORSTATIC` 导致文字带白底块，两处都改了。另一层是打真实接口跑完整流程，见第 7 节的验证记录。

## 3. 怎么判断一份 Games.exe 能用

每次启动前对候选做三项检查：文件存在、`WinVerifyTrust`（`WINTRUST_ACTION_GENERIC_VERIFY_V2`）通过、签名主体包含 `Hypergryph`、`GRYPHLINE` 或 `GRYPH FRONTIER` 之一。三项都过就算有效，不比版本号。

这套检查同时充当更新失效的兜底：官方更新启动器后本地文件如果失效，检查自然不通过，会自动重新获取，不需要轮询或版本比对。

按主体匹配而不是证书指纹，是因为硬编码指纹会在证书轮换后失效。两个区域主体不同：国服 `CN="Shanghai Hypergryph Network Technology Co., Ltd."` 命中 `Hypergryph`，国际服 `CN=GRYPH FRONTIER PTE. LTD.` 命中 `GRYPH FRONTIER`（见 6.3）。国服证书由 `DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1` 签发，SHA-1 指纹 `2B366F397E64A9494A553B70004E8CA1AAE02B9D`，有效期到 2027-08-17。

参数上 `dwUIChoice = 2`（静默）、`dwProvFlags = 0x100`。旧注释把 `0x100` 标成 `WTD_CACHE_ONLY_URL_RETRIEVAL` 是错的，那个常量是 `0x1000`，`0x100` 是 `WTD_SAFER_FLAG`；两种取值实测都返回 `0`，不影响结果。签名主体靠 `X509Certificate.CreateFromSignedFile` 读，这个 API 被标了 `SYSLIB0057` 废弃，但 `X509CertificateLoader` 只能读 PEM / DER 文件和 PKCS#12，没法从已签名的 PE 里取签名者证书，所以保留调用并抑制警告。

## 4. 挂起父进程桥

```
1. CreateProcessW(官方 Games.exe, CREATE_SUSPENDED | CREATE_NO_WINDOW)
     主线程永不 Resume，不会进启动器界面
2. InitializeProcThreadAttributeList(1)
   UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, hSuspendedGames)
3. CreateProcessW(Endfield.exe, ..., EXTENDED_STARTUPINFO_PRESENT)
4. WaitForExitAsync(游戏进程)
5. Dispose → TerminateProcess(hSuspendedGames) + CloseHandle
     只清理本次自建的挂起父进程，不动既有启动器或游戏
```

全程只用 Windows 正规进程 API，不改游戏 DLL，不 hook 游戏进程，不关检查，不伪造父进程。第 1 步要求调用方已提权，理由见 6.5。

## 5. 残留挂起进程的清理

挂起父进程由 `Dispose()` 终止。宿主被强杀（任务管理器结束 Collapse）的话会残留，所以每次启动前 `OfficialLauncherProvider` 会终止可执行文件路径位于组件目录下的同名进程。作用域收窄到组件目录，是为了不误杀选取分支里用户指定的官方安装目录。

## 6. 国际服（Gryphlink）

### 6.1 appcode

拿到国际服启动器整包后，对包内 4 个可执行文件跑跟国服同一条检索：

```bash
grep -aoE 'publish-launcher-[0-9]+.[A-Za-z0-9]{16}' Games.exe Launcher.exe Updater.exe Patch.exe
# -> publish-launcher-184\TiaytKBUIEdoEwRT
```

四个文件一致。`TiaytKBUIEdoEwRT` 在 `Games.exe` 里出现 142 次，形态都是
`D:\work\publish-launcher-184\TiaytKBUIEdoEwRT\1.6.0.1607\11\HG\Kernel\...` 这样的 `__FILE__` 编译路径常量。国服是 `publish-launcher-184\abYeZZ16BPluCFyT\1.6.0.1604\11\HG\...`，两边 `184` 相同，同一套启动器代码库按区域分别构建。

### 6.2 channel / sub_channel 必须是 6/6

拿 `TiaytKBUIEdoEwRT` 打 `launcher.gryphline.com` 扫了一遍：

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

国际服 CDN 是 `launcher.hg-cdn.com`（国服 `launcher-sign.hycdn.cn`），而且不带 `auth_key`，可以直接下。整包实测 117,679,901 字节、9.3 秒，MD5 与接口一致；包内 247 个条目，`Games.exe` 在根目录，17,998,728 字节。

`EndfieldGlobalPreSetConfig` 的 `ExChannel` / `ExSubChannel` 本来就是 6 / 6，只需要把 `ExLauncherAppCode` 从游戏 appcode 改成 `TiaytKBUIEdoEwRT`。

`EndfieldPlayPreSetConfig`（Google Play 通道，`sub_channel = 802`）改不动：`get_latest_launcher` 在 6/802 返回 404（`get_latest_game` 正常，包名带 `_googleplay`），说明这个通道没有独立的启动器记录。它会落到「拿不到包，只能手动选取」那条路。

### 6.3 验签主体跟国服不一样

`TrustedPublishers` 原先只有 `Hypergryph` / `GRYPHLINE`，那是按国服定的：

| 区域 | 证书主体 | 命中 |
| --- | --- | --- |
| 国服 | `CN="Shanghai Hypergryph Network Technology Co., Ltd.", O=..., S=shanghai, C=CN` | `Hypergryph` |
| 国际服 | `CN=GRYPH FRONTIER PTE. LTD., O=GRYPH FRONTIER PTE. LTD., L=Singapore, C=SG` | `GRYPH FRONTIER` |

国际服主体里既没有 `Hypergryph` 也没有 `GRYPHLINE`，`Contains` 全部落空，`IsTrustedPublisher` 返回 false，启动器会被当成非官方组件拒掉。已把 `GRYPH FRONTIER` 加进列表。两边 `WinVerifyTrust` 都返回 `0`，签发者都是 `DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1`。

### 6.4 文件名也是 Games.exe

Gryphlink 包内主程序就叫 `Games.exe`（17,998,728 字节），跟国服同名，代码里硬编码的 `Games.exe` 对国际服同样成立。`Launcher.exe`（`asInvoker`）是另一个组件，不是被校验的那个。国际服注册的协议 scheme 是 `gryphlinklauncher`，已在 `ProtocolPrefixes` 里。

### 6.5 宿主必须已提权

两个区域的 `Games.exe` 清单里都是：

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
```

对照：`Launcher.exe` 是 `asInvoker`，`Updater.exe` / `Patch.exe` 是 `requireAdministrator`。

在 Medium 完整性、`TokenElevation = 0` 的普通会话里实测 `CreateProcessW`：

| 目标 | flags | 结果 |
| --- | --- | --- |
| `notepad.exe` | `CREATE_SUSPENDED \| CREATE_NO_WINDOW` | True |
| 国服 `Games.exe` | `CREATE_SUSPENDED \| CREATE_NO_WINDOW` | False, err 740 |
| 国际服 `Games.exe` | `CREATE_SUSPENDED \| CREATE_NO_WINDOW` | False, err 740 |
| 国际服 `Games.exe` | `CREATE_SUSPENDED` | False, err 740 |
| 国际服 `Games.exe` | `CREATE_NO_WINDOW` | False, err 740 |

`740` = `ERROR_ELEVATION_REQUIRED`。所以 `TryCreateSuspended` 在非提权宿主里必然失败，`TryCreate` 对所有候选都返回 false。

不处理。Collapse Launcher 本来就以管理员权限运行，这是它的既有前提，不是这个桥引入的约束，插件侧不需要做提权兜底，`TryCreateSuspended` 也不用改。

之前没发现，是因为端到端测试都在 Administrator 会话里跑。真机 `OneChannelLC.log` 里两种结果并存：

```text
[10-15 17:28] Not found matched name
              errorCode:10100, filePath: ...\collapselauncher.exe     <- Collapse 直接拉起，被拒
[10-15 17:35] Parent process check success:27132
              filePath: d:\misaka10843\downloads\1.6.0\games.exe       <- 父进程是启动器，通过
```

顺带确认了 `parentNames` 接受 `Games.exe`，按完整路径取文件名匹配。

诊断时踩过一个坑：`Marshal.GetLastWin32Error()` 必须紧接在 P/Invoke 之后读，中间插一句 `Console.WriteLine` 会把 last-error 冲成 0，看起来像「返回 false 但错误码是 0」。

考虑过但没采用的三个方向，留个记录：要求 Collapse 以管理员运行并在检测到 740 时提示；用 `ShellExecuteEx(runas)` 拉一个提权小助手，由它创建挂起 `Games.exe`；`ShellExecuteEx(runas)` 直接拉 `Games.exe` 再立刻 `NtSuspendProcess`。前两个把提权强加给整个启动器或每次弹 UAC，第三个拿到的句柄不一定有 `PROCESS_SUSPEND_RESUME` 而且有竞态。2026-10-16 决定三个都不做。

### 6.6 国际服客户端是否也做校验

`OneChannelLC.dll` 由客户端自带，`checkItems[]` 来自服务端。国际服客户端加载不加载这个模块、`parentNames` 取什么值，还没验证。国服实测接受 `Games.exe`（见 6.5 的日志），国际服大概率一样，但没有证据。

## 7. 待办

| 编号 | 内容 | 状态 |
| --- | --- | --- |
| T1 | 原生提示（两次 `MessageBoxW`） | 已实现 |
| T2 | `get_latest_launcher` DTO 与 `GetLatestLauncherAsync` | 已实现，实测 200 |
| T3 | `OfficialLauncherBridge` | 已实现，实测父 PID 一致 |
| T4 | 组件目录 + 复用下载管线 + 单条目解压 | 已实现 |
| T5 | 接入 `Exports.GameLaunch.cs` | 已实现，Arknights / Yostar 未改动 |
| T6 | 选取分支 | 已实现，选中路径写进 `picked_launcher.txt`，之后不再询问 |
| T7 | 真机端到端 | 自动化链路已跑通（含 7-Zip 交叉验证），游戏内校验未实测 |
| T8 | 国际服 appcode | 已解：`TiaytKBUIEdoEwRT`，6/6 实测 200 且包 MD5 一致，见 6.1 / 6.2 |
| T9 | 国际服启动器文件名 | 已解：也叫 `Games.exe`，见 6.4 |
| T10 | 国际服客户端是否也执行父进程校验 | 未验证，见 6.6 |
| T11 | 进度窗口 + 就绪/失败弹窗 + 三语 i18n | 已实现，47 项断言全过 |
| T12 | 下载完成后不自动启动 | 已实现，见 2 节 |
| T13 | 宿主未提权时创建 `Games.exe` 失败（740） | 不处理，Collapse 本就要求管理员，见 6.5 |
| T14 | 国际服验签主体不同（`GRYPH FRONTIER`） | 已修，见 6.3 |
| T15 | 改用游戏自带文件当诱饵父进程，不再下载启动器 | 已实现，12 项断言全过，见 `DECOY_PARENT_BRIDGE.md` |
| T16 | 官方启动器路径暂停使用（代码保留） | 已跳过，`AcquireOfficialLauncher = false` |

验证记录（真实国服接口，全新游戏目录，无缓存）：

```
EnsureAsync #1  Status = Acquired  Bridge = null（不自动启动）  6.0s
                Games.exe 落盘 17998824 bytes，目录只剩它一个
                进度窗口观测到 24 条文案，0% 到 99% 再到「校验」「解压」
                两个弹窗自动应答：询问框点「是」，就绪框点「确定」
EnsureAsync #2  Status = Ready     Bridge != null（这次才启动）
挂起 Games.exe pid = 4988，子进程 pid = 2776，父 pid = 4988   PASS
Dispose 后挂起父进程已清理                                    PASS
```

还没验证的是终末地客户端自己放不放行。需要在一台装了终末地 1.6.3 的机器上实际启动一次，再看
`%USERPROFILE%\AppData\LocalLow\HyperGryph\Endfield\sdklogs\OneChannelLC.log` 里有没有
`Parent process check success`、内层 `code` 是不是 `0`。在这之前不要说「完整链路已验证」。

## 8. 明确不做

| 项 | 原因 |
| --- | --- |
| 监控启动器更新 / 轮询版本 | 由第 3 节的本地验签兜底 |
| 内置 `Games.exe` 到发布物 | 再分发受版权保护的二进制 |
| 伪造签名 / 自编译同名 `Games.exe` | 过不了 1.1 的第 3、4 步，也碰法律与反作弊红线 |
| 改 `OneChannelLC.dll` / 内存 patch | 触碰客户端完整性校验，账号级风险 |
| Hook 游戏进程 | ACE 会阻断或封号 |
| zip 偏移量单文件提取 | 已证伪，见 2.4 |
| 静默下载大文件 | 112 MB 的下载必须经用户确认 |
| 在系统里安装官方启动器 | 跟插件存在的意义冲突，只提取单个可执行文件 |

## 9. 参考

- 第三方研究：Better-Endfield `docs/host/research/1.6.3/launcher-check/LAUNCHER_CHECK.md`
  - 已在其 CN 1.6.3 真机验证过桥方案：`Parent process check success:29624`，内层 `code` 为 `0`
  - 生产实现：`native/loaders/shared/official_launcher.cpp`
- 相关代码：
  - `Hi3Helper.Hypergryph.Core/Management/OfficialLauncherBridge.cs`
  - `Hi3Helper.Hypergryph.Core/Management/OfficialLauncherProvider.cs`
  - `Hi3Helper.Hypergryph.Core/Management/NativePrompt.cs`
  - `Hi3Helper.Hypergryph.Core/Management/ProgressPrompt.cs`
  - `Hi3Helper.Hypergryph.Core/Management/PromptStrings.cs`
  - `Hi3Helper.Hypergryph.Core/Management/HgFileDownloader.cs`
  - `Hi3Helper.Hypergryph.Core/Management/HgGameManager.cs`（API 请求组装）
  - `Hi3Helper.Hypergryph.Core/Management/HgGameInstaller.cs`（安装 / 修复管线）
  - `Hi3Helper.Hypergryph.Core/Management/Api/HgApiStructs.cs`、`HgApiContext.cs`（DTO）
  - `Hi3Helper.Plugin.Endfield/Exports.GameLaunch.cs`（启动入口）
  - `Hi3Helper.Plugin.Core/Utility/Windows/PInvoke.cs`（现有 P/Invoke 基建）
