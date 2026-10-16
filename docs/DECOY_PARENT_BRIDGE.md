# 诱饵父进程桥

终末地启动时会查直接父进程，要求它是一个鹰角签名的 `Games.exe`。游戏目录里本来就有好几个文件符合这个条件，复制一份改名成 `Games.exe` 挂起来当父进程就够了，不需要下载或安装官方启动器。

当前启动路径走的是这条。官方启动器那套（下载 / 选取 / 挂起真的 `Games.exe`）代码完整保留，只是启动时不调用，见第 5 节。

只涉及《明日方舟：终末地》的中国大陆服与国际服。

**宿主必须已提权。** 挂起父进程用的是 `CreateProcessW`，Collapse 本来就以管理员运行，见 `OFFICIAL_LAUNCHER_BRIDGE.md` 第 6.5 节。

最后更新 2026-10-16。

## 1. 为什么能绕

校验只看三件事：直接父进程的文件名、它的 Authenticode 签名是否可信、签名者证书指纹在不在白名单里。它不检查父进程是不是真的启动器进程，也不管父进程跑在哪、有没有界面、有没有被挂起。

`PlatformProcess.exe`、`UnityCrashHandler64.exe`、`Endfield.exe` 都带鹰角签名。任选一个复制出来改名成 `Games.exe`，三项就都满足了。

校验的完整流程（`OneChannelLC.dll`、四步检查、错误码 10100/10101/10102）写在 `OFFICIAL_LAUNCHER_BRIDGE.md` 第 1 节，这里不重复。文件名匹配忽略大小写，所以 `Games.exe` 和 `games.exe` 等价。

## 2. 候选与顺序

按顺序试，第一个「验签通过 + 能挂起创建」的胜出：

| 顺序 | 文件 | 说明 |
| --- | --- | --- |
| 1 | `PlatformProcess.exe` | 平台进程，最轻 |
| 2 | `UnityCrashHandler64.exe` | Unity 崩溃处理 |
| 3 | `Endfield.exe` | 游戏本体，兜底 |

在游戏目录里找：先看根目录，再逐层往下，最多三层，跳过 junction 和符号链接。名字是常量，在 `GameDecoyParentBridge.CandidateFileNames`。

每个候选各自过一遍验签，所以某个文件没签名、或者清单要求管理员导致创建不了（`ERROR_ELEVATION_REQUIRED`，740），都只是被跳过，不影响后面的候选。

## 3. 落盘与清理

只写一个文件：

```
%TEMP%\Hi3Helper\ParentBridge\Games.exe
```

游戏目录一个字节都不动。

启动前先按精确路径删掉上次的残留（宿主被强杀会留下）。`File.Copy` 会把只读属性带过来，复制完显式清成 `Normal`，否则下次删不掉。挂起父进程随游戏退出一起 `TerminateProcess`，`Dispose` 里等它真正结束再删文件，删不掉就重试两次；仍然失败也只是留个残留，下次启动会再清。

路径是固定的，所以同一台机器同时只支持一个游戏实例。真出现并发启动，第二个实例删不掉也覆盖不了前一个的诱饵文件，`TryCreate` 返回 null，退回直启。Collapse 本身也有「游戏已在运行」的检查，这条实际碰不到。

## 4. 失败回退

桥建不起来（找不到候选、验签不过、创建不了、启动抛异常）就退回原来的直启：`ProcessStartInfo` 直接拉 `Endfield.exe`。游戏多半会弹「请通过官方启动器打开游戏」，但不会卡住玩家。

## 5. 官方启动器那条路

`OfficialLauncherBridge`、`OfficialLauncherProvider`、`NativePrompt`、`ProgressPrompt`、`PromptStrings` 全部保留并编译进产物，只是启动入口不调用。开关在 `Exports.GameLaunch.cs`：

```csharp
private static readonly bool AcquireOfficialLauncher = false;
```

改回 `true` 就恢复「找现成的 `Games.exe` → 找不到就下载组件 → 挂起它」那套流程，设计细节见 `OFFICIAL_LAUNCHER_BRIDGE.md`。

保留的理由：现在这套依赖游戏目录里恰好有带鹰角签名的文件。哪天客户端把校验收紧到只认真正的启动器，那条路可以直接用回来。

两条路共用 `ParentProcessBridge`，它负责挂起创建、验签、把挂起句柄当父进程传给游戏、以及收尾终止；两个子类各自只回答「父进程文件从哪来」。

## 6. 验证

这台机器上没有终末地安装，所以用启动器包里的文件当替身跑了一轮。替身选得有讲究：`Launcher.exe` 是鹰角签名的 `asInvoker`，`Games.exe` 是鹰角签名的 `requireAdministrator`，正好覆盖成功和提权失败两条分支。

| 检查 | 结果 |
| --- | --- |
| 未签名的同名文件被拒 | PASS |
| 候选只在子目录里也能找到 | PASS |
| `Games.exe`（要管理员）验签通过但挂起失败，且不留残留 | PASS |
| 签名 `asInvoker` 文件 → 落盘 → 挂起 → 子进程父 PID 等于挂起的 `Games.exe` | PASS |
| `Dispose` 后残留被删 | PASS |
| 紧接着再建一次，不受上次残留影响 | PASS |

12 项断言全过，非提权会话（`TokenElevation = 0`）。另外单独确认了 `File.Copy` 之后签名仍然有效：复制出来的 `Games.exe` 用 `Get-AuthenticodeSignature` 读是 `Valid`，主体 `CN=GRYPH FRONTIER PTE. LTD.`，文件属性回到 `Archive`。

国服真机由用户实测通过。

## 7. 未验证

- 国际服客户端是否也做父进程校验（`OFFICIAL_LAUNCHER_BRIDGE.md` 的 T10）。
- 真机游戏内日志：需要一台装了终末地的机器，看 `OneChannelLC.log` 里有没有 `Parent process check success`、内层 `code` 是不是 `0`。

## 8. 参考

- `Hi3Helper.Hypergryph.Core/Management/ParentProcessBridge.cs`（共用机制）
- `Hi3Helper.Hypergryph.Core/Management/GameDecoyParentBridge.cs`（本方案）
- `Hi3Helper.Hypergryph.Core/Management/OfficialLauncherBridge.cs`（官方启动器方案，当前跳过）
- `Hi3Helper.Plugin.Endfield/Exports.GameLaunch.cs`（启动入口与开关）
- `OFFICIAL_LAUNCHER_BRIDGE.md`（父进程校验的完整逆向、官方启动器方案）
