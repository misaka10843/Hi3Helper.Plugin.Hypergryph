using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Hypergryph.Core.Management;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.Endfield;

public partial class Exports
{
    /// <summary>
    ///     是否走「下载 / 选取官方启动器组件」那条路。当前为 false：游戏自带的文件已经够用，
    ///     见 <see cref="GameDecoyParentBridge" />。官方启动器那套代码整体保留，改回 true 即可恢复。
    /// </summary>
    private static readonly bool AcquireOfficialLauncher = false;

    /// <summary>
    ///     核心启动逻辑
    /// </summary>
    protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, string? startArgument, bool isRunBoosted,
        ProcessPriorityClass processPriority, CancellationToken token)
    {
        return (true, Impl());

        async Task<bool> Impl()
        {
            if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return false;

            if (context is not
                {
                    GameManager: HgGameManager manager, PresetConfig: PluginPresetConfigBase presetConfig
                }) return false;

            var gameDirectory = Path.GetDirectoryName(gameExecutablePath);

            ParentProcessBridge? parent = null;

            if (AcquireOfficialLauncher)
            {
                try
                {
                    LauncherAcquireOutcome outcome = await OfficialLauncherProvider.EnsureAsync(manager,
                        presetConfig.ProfileName, presetConfig.ZoneFullName, token);

                    if (outcome.Status == LauncherAcquireStatus.Acquired)
                    {
                        InstanceLogger.LogInformation(
                            "[Endfield::LaunchGame] The official launcher component has been prepared. Waiting for the user to start the game again.");
                        return false;
                    }

                    if (outcome.Status == LauncherAcquireStatus.Unavailable)
                    {
                        InstanceLogger.LogError(
                            "[Endfield::LaunchGame] The official launcher component is unavailable. The game cannot be started.");
                        return false;
                    }

                    parent = outcome.Bridge;
                }
                catch (Exception e)
                {
                    InstanceLogger.LogError(e, "[Endfield::LaunchGame] Failed to acquire the official launcher.");
                }
            }
            else
            {
                parent = GameDecoyParentBridge.TryCreate(gameDirectory);
            }

            using (parent)
            {
                Process? process = null;

                if (parent != null)
                {
                    try
                    {
                        process = parent.StartGame(gameExecutablePath, startArgument, gameDirectory);
                    }
                    catch (Exception e)
                    {
                        InstanceLogger.LogError(e,
                            "[Endfield::LaunchGame] Failed to start the game through the parent process bridge.");
                        parent.Dispose();
                    }
                }

                process ??= StartGameDirectly(gameExecutablePath, startArgument, gameDirectory);

                if (process == null) return false;

                using (process)
                {
                    try
                    {
                        process.PriorityBoostEnabled = isRunBoosted;
                        process.PriorityClass = processPriority;
                    }
                    catch (Exception e)
                    {
                        InstanceLogger.LogError(e, "[Endfield::LaunchGame] Failed to set process priority.");
                    }

                    _ = ReadGameLog(context, token);

                    await process.WaitForExitAsync(token);

                    return true;
                }
            }
        }
    }

    /// <summary>
    ///     桥建不起来时的退路。游戏大概率会弹「请通过官方启动器打开游戏」，但至少不会卡住玩家。
    /// </summary>
    private static Process? StartGameDirectly(string executablePath, string? arguments, string? workingDirectory)
    {
        ProcessStartInfo startInfo = string.IsNullOrEmpty(arguments)
            ? new ProcessStartInfo(executablePath)
            : new ProcessStartInfo(executablePath, arguments);

        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;

        try
        {
            Process? process = Process.Start(startInfo);
            InstanceLogger.LogWarning(
                "[Endfield::LaunchGame] Started the game without a parent process.");
            return process;
        }
        catch (Exception e)
        {
            InstanceLogger.LogError(e, "[Endfield::LaunchGame] Failed to start the game directly.");
            return null;
        }
    }

    /// <summary>
    ///     检测游戏是否正在运行
    /// </summary>
    protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool isGameRunning, out DateTime gameStartTime)
    {
        isGameRunning = false;
        gameStartTime = default;

        if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return true;

        using var process = FindExecutableProcess(gameExecutablePath);
        if (process != null)
        {
            isGameRunning = true;
            gameStartTime = process.StartTime;
        }

        return true;
    }

    /// <summary>
    ///     等待游戏运行
    /// </summary>
    protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token)
    {
        return (true, Impl());

        async Task<bool> Impl()
        {
            if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return true;

            using var process = FindExecutableProcess(gameExecutablePath);
            if (process != null)
                await process.WaitForExitAsync(token);

            return true;
        }
    }

    /// <summary>
    ///     强制关闭游戏
    /// </summary>
    protected override bool KillRunningGameCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool wasGameRunning, out DateTime gameStartTime)
    {
        wasGameRunning = false;
        gameStartTime = default;

        if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return true;

        using var process = FindExecutableProcess(gameExecutablePath);
        if (process == null) return true;

        wasGameRunning = true;
        gameStartTime = process.StartTime;
        process.Kill();
        return true;
    }

    private static Process? FindExecutableProcess(string? executablePath)
    {
        if (executablePath == null) return null;

        var executableDirPath = Path.GetDirectoryName(executablePath.AsSpan());
        var executableName = Path.GetFileNameWithoutExtension(executablePath);

        var processes = Process.GetProcessesByName(executableName);
        Process? returnProcess = null;

        foreach (var process in processes)
            if (process.MainModule?.FileName.StartsWith(executableDirPath, StringComparison.OrdinalIgnoreCase) ?? false)
            {
                returnProcess = process;
                break;
            }

        foreach (var process in processes.Where(x => x != returnProcess)) process.Dispose();

        return returnProcess;
    }

    private static bool TryGetGameExecutablePath(GameManagerExtension.RunGameFromGameManagerContext context,
        [NotNullWhen(true)] out string? gameExecutablePath)
    {
        gameExecutablePath = null;
        if (context is not
            {
                GameManager: HgGameManager endfieldManager, PresetConfig: PluginPresetConfigBase presetConfig
            }) return false;

        endfieldManager.GetGamePath(out var gamePath);
        presetConfig.comGet_GameExecutableName(out var executablePath);

        gamePath?.NormalizePathInplace();
        executablePath.NormalizePathInplace();

        if (string.IsNullOrEmpty(gamePath)) return false;

        gameExecutablePath = Path.Combine(gamePath, executablePath);
        return File.Exists(gameExecutablePath);
    }

    private static async Task ReadGameLog(GameManagerExtension.RunGameFromGameManagerContext context,
        CancellationToken token)
    {
        if (context is not { PresetConfig: PluginPresetConfigBase presetConfig }) return;

        presetConfig.comGet_GameAppDataPath(out var gameAppDataPath);
        presetConfig.comGet_GameLogFileName(out var gameLogFileName);

        if (string.IsNullOrEmpty(gameAppDataPath) || string.IsNullOrEmpty(gameLogFileName))
            return;

        var gameLogPath = Path.Combine(gameAppDataPath, gameLogFileName);

        var retry = 5;
        while (!File.Exists(gameLogPath) && retry >= 0)
        {
            await Task.Delay(1000, token);
            --retry;
        }

        if (retry <= 0) return;

        var printCallback = context.PrintGameLogCallback;

        try
        {
            await using var fileStream =
                File.Open(gameLogPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fileStream);

            fileStream.Position = 0;
            while (!token.IsCancellationRequested)
            {
                while (await reader.ReadLineAsync(token) is { } line) PassStringLineToCallback(printCallback, line);
                await Task.Delay(250, token);
            }
        }
        catch (Exception ex)
        {
            InstanceLogger.LogWarning($"[Endfield::ReadGameLog] Stopped reading log: {ex.Message}");
        }

        return;

        static unsafe void PassStringLineToCallback(GameManagerExtension.PrintGameLog? invoke, string line)
        {
            var lineP = line.GetPinnableStringPointer();
            var lineLen = line.Length;
            invoke?.Invoke(lineP, lineLen, 0);
        }
    }
}
