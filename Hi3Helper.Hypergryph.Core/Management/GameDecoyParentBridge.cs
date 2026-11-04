using System;
using System.IO;
using Hi3Helper.Hypergryph.Shared;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Core.Management;

/// <summary>Stages one explicitly selected, matching-signer image as a suspended Games.exe.</summary>
public sealed class GameDecoyParentBridge : ParentProcessBridge
{
    private readonly string _sessionDirectory;

    private GameDecoyParentBridge(nint process, nint thread, string sessionDirectory) : base(process, thread)
        => _sessionDirectory = sessionDirectory;

    public static GameDecoyParentBridge Create(string gameExecutable, string configuredSource)
    {
        string gameDirectory = Path.GetDirectoryName(Path.GetFullPath(gameExecutable))!;
        // Endfield.exe requests elevation; PlatformProcess.exe can be suspended by a normal launcher.
        string source = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredSource) ? "PlatformProcess.exe" : configuredSource, gameDirectory);
        if (!string.Equals(Path.GetExtension(source), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
            throw new FileNotFoundException("父进程来源 EXE 不存在。", source);
        if (!HasMatchingSigner(source, gameExecutable)) throw new InvalidDataException("父进程来源签名无效或与游戏签名者不一致。");

        string directory = Path.Combine(PluginPaths.In(gameDirectory, "parent"), Environment.ProcessId.ToString(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string staged = Path.Combine(directory, GamesExecutableName);
            File.Copy(source, staged);
            File.SetAttributes(staged, FileAttributes.Normal);
            if (!HasMatchingSigner(staged, gameExecutable)) throw new InvalidDataException("暂存父进程签名验证失败。");
            var (process, thread) = Suspend(staged);
            SharedStatic.InstanceLogger.LogInformation("[HgBridge] Parent staged from {Source} to {Staged}", source, staged);
            return new GameDecoyParentBridge(process, thread, directory);
        }
        catch
        {
            Cleanup(directory);
            throw;
        }
    }

    protected override void OnDispose() => Cleanup(_sessionDirectory);

    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SharedStatic.InstanceLogger.LogWarning(ex, "[HgBridge] Cannot remove parent session {Path}", directory); }
    }
}
