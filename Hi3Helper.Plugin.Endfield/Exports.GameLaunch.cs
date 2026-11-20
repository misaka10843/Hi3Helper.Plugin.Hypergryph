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
                    GameManager: HgGameManager manager
                }) return false;

            var gameDirectory = Path.GetDirectoryName(gameExecutablePath);

            token.ThrowIfCancellationRequested();
            using var parent = GameDecoyParentBridge.Create(gameExecutablePath,
                Hi3Helper.Hypergryph.Shared.PluginConfiguration.For(manager).Read().ParentExecutablePath);
            token.ThrowIfCancellationRequested();
            using Process process = parent.StartGame(gameExecutablePath, startArgument, gameDirectory);
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

    /// <summary>
    ///     检测游戏是否正在运行
    /// </summary>
    protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool isGameRunning, out DateTime gameStartTime)
    {
        isGameRunning = false;
        gameStartTime = default;

        if (!TryGetGameExecutablePath(context, out var gameExecutablePath)) return true;

        using var process = Hi3Helper.Hypergryph.Shared.GameProcess.FindExecutable(gameExecutablePath);
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

            using var process = Hi3Helper.Hypergryph.Shared.GameProcess.FindExecutable(gameExecutablePath);
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

        using var process = Hi3Helper.Hypergryph.Shared.GameProcess.FindExecutable(gameExecutablePath);
        if (process == null) return true;

        wasGameRunning = true;
        gameStartTime = process.StartTime;
        process.Kill();
        return true;
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
