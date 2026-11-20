using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Hypergryph.Core.Management.Api;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Core.Management;

[GeneratedComClass]
public partial class HgGameInstaller : GameInstallerBase
{
    private readonly HttpClient _downloadHttpClient;

    private static string GetDownloadDirectory(string gamePath)
    {
        Hi3Helper.Hypergryph.Shared.PluginPaths.Migrate(gamePath);
        return Hi3Helper.Hypergryph.Shared.PluginPaths.In(gamePath, "downloads");
    }

    public HgGameInstaller(IGameManager? gameManager) : base(gameManager)
    {
        _downloadHttpClient = new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.GZip)
            .AllowCookies()
            .AllowRedirections()
            .AllowUntrustedCert()
            .Create();
    }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        if (GameManager is not HgGameManager HgManager)
            throw new InvalidOperationException("GameManager is not HgGameManager");

        return await HgManager.InitAsyncInner(true, token).ConfigureAwait(false);
    }

    protected override async Task<long> GetGameSizeAsyncInner(GameInstallerKind gameInstallerKind,
        CancellationToken token)
    {
        using var log = Hi3Helper.Hypergryph.Shared.PluginDiagnostics.BeginGameOperation(GameManager);
        await InitAsync(token).ConfigureAwait(false);

        if (GameManager is not HgGameManager manager) return 0L;
        if (gameInstallerKind != GameInstallerKind.Preload && manager.IsManualVerifyRequested) return 0L;

        var packs = manager.GetGamePacks(gameInstallerKind);
        if (packs == null) return 0L;

        long totalSize = 0;
        foreach (var pack in packs)
            if (long.TryParse(pack.PackageSize, out var size))
                totalSize += size;
        return totalSize;
    }

    protected override async Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind gameInstallerKind,
        CancellationToken token)
    {
        using var log = Hi3Helper.Hypergryph.Shared.PluginDiagnostics.BeginGameOperation(GameManager);
        await InitAsync(token).ConfigureAwait(false);
        if (GameManager is not HgGameManager manager) return 0L;
        if (gameInstallerKind != GameInstallerKind.Preload && manager.IsManualVerifyRequested) return 0L;

        var packs = manager.GetGamePacks(gameInstallerKind);
        if (packs == null) return 0L;

        GameManager.GetGamePath(out var installPath);
        if (string.IsNullOrEmpty(installPath)) return 0L;

        var downloadDir = GetDownloadDirectory(installPath);
        if (!Directory.Exists(downloadDir)) return 0L;

        long downloadedSize = 0;
        foreach (var pack in packs)
        {
            if (string.IsNullOrEmpty(pack.Url)) continue;

            var fileName = Path.GetFileName(new Uri(pack.Url).LocalPath);
            var filePath = Path.Combine(downloadDir, fileName);

            if (File.Exists(filePath))
            {
                downloadedSize += new FileInfo(filePath).Length;
            }
            else
            {
                var tempPath = filePath + ".tmp";
                if (File.Exists(tempPath))
                    downloadedSize += new FileInfo(tempPath).Length;
            }
        }

        return downloadedSize;
    }

    protected override Task StartInstallAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        return RunInstallOrVerificationAsync(GameInstallerKind.Install, progressDelegate, progressStateDelegate, token);
    }

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
        => RunInstallOrVerificationAsync(GameInstallerKind.Update, progressDelegate, progressStateDelegate, token);

    private async Task RunInstallOrVerificationAsync(GameInstallerKind kind, InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        if (GameManager is not HgGameManager manager)
            throw new InvalidOperationException("GameManager is not HgGameManager");

        using var log = Hi3Helper.Hypergryph.Shared.PluginDiagnostics.BeginGameOperation(manager);
        var configuration = Hi3Helper.Hypergryph.Shared.PluginConfiguration.For(manager);
        bool verify = configuration.BeginVerification();
        try
        {
            if (!verify)
            {
                await StartInstallCoreAsync(kind, progressDelegate, progressStateDelegate, token).ConfigureAwait(false);
                return;
            }
            GameManager.GetGamePath(out string? installPath);
            if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                throw new DirectoryNotFoundException("The game installation directory is missing.");
            if (await manager.InitAsyncInner(true, token, requestFullPackage: true, expectedGamePath: installPath).ConfigureAwait(false) != 0)
                throw new InvalidOperationException("Cannot obtain game resource information for verification.");
            SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Starting requested manual verification.");
            progressStateDelegate?.Invoke(InstallProgressState.Preparing);
            var repairer = new HgGameRepairer(_downloadHttpClient, manager.GetFullResourceBaseUrl(installPath), installPath);
            await repairer.StartRepairAsync(progressDelegate, progressStateDelegate, token).ConfigureAwait(false);
            progressStateDelegate?.Invoke(InstallProgressState.Completed);
        }
        finally { configuration.EndVerification(); }
    }

    protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        return StartPreloadCoreAsync(progressDelegate, progressStateDelegate, token);
    }

    private Task StartInstallCoreAsync(GameInstallerKind kind, InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        var installer = new Install(this);
        return installer.RunAsync(kind, progressDelegate, progressStateDelegate, token);
    }

    private async Task StartPreloadCoreAsync(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        if (GameManager is not HgGameManager manager)
            throw new InvalidOperationException("GameManager is not HgGameManager");

        manager.GetGamePath(out var installPath);
        if (string.IsNullOrEmpty(installPath))
            throw new InvalidOperationException("Install path is missing.");

        using var log = Hi3Helper.Hypergryph.Shared.PluginDiagnostics.BeginGameOperation(installPath);
        if (await manager.InitAsyncInner(true, token, expectedGamePath: installPath).ConfigureAwait(false) != 0)
            throw new InvalidOperationException("Cannot refresh Hypergryph preload information.");
        var packs = manager.GetGamePacks(GameInstallerKind.Preload);
        if (packs is not { Count: > 0 })
            throw new InvalidOperationException("No preload packs found in API response.");

        var downloadDir = GetDownloadDirectory(installPath);
        Directory.CreateDirectory(downloadDir);

        InstallProgress progress = default;

        var lastReportTicks = DateTime.UtcNow.Ticks;

        void Report(InstallProgressState state)
        {
            progressDelegate?.Invoke(in progress);
            progressStateDelegate?.Invoke(state);
        }

        Report(InstallProgressState.Preparing);
        SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Preload started.");

        long totalBytesToDownload = 0;
        long alreadyDownloadedBytes = 0;
        var packsToDownload = new ConcurrentBag<HgPack>();

        foreach (var pack in packs)
            if (long.TryParse(pack.PackageSize, out var size))
                totalBytesToDownload += size;

        progress.TotalCountToDownload = packs.Count;
        progress.DownloadedCount = 0;
        progress.TotalBytesToDownload = totalBytesToDownload;
        progress.DownloadedBytes = 0;
        progress.TotalStateToComplete = packs.Count;
        progress.StateCount = 0;

        Report(InstallProgressState.Verify);

        await Parallel.ForEachAsync(packs,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (pack, innerToken) =>
            {
                if (string.IsNullOrEmpty(pack.Url)) return;

                long.TryParse(pack.PackageSize, out var size);
                var fileName = Path.GetFileName(new Uri(pack.Url).LocalPath);
                var filePath = Path.Combine(downloadDir, fileName);
                var tempPath = filePath + ".tmp";
                var needsDownload = true;

                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length == size)
                    {
                        var isMatch = string.IsNullOrEmpty(pack.Md5) ||
                                      await HgFileDownloader.CheckMd5Async(filePath, pack.Md5, innerToken)
                                          .ConfigureAwait(false);

                        if (isMatch)
                        {
                            Interlocked.Add(ref alreadyDownloadedBytes, size);
                            needsDownload = false;
                        }
                        else
                        {
                            HgFileDownloader.ForceDeleteFile(filePath);
                        }
                    }
                    else
                    {
                        HgFileDownloader.ForceDeleteFile(filePath);
                    }
                }

                if (needsDownload)
                {
                    if (File.Exists(tempPath) && new FileInfo(tempPath).Length > size)
                        HgFileDownloader.ForceDeleteFile(tempPath);

                    packsToDownload.Add(pack);
                }
                else
                {
                    Interlocked.Add(ref progress.DownloadedBytes, size);
                }

                Interlocked.Increment(ref progress.DownloadedCount);
                Interlocked.Increment(ref progress.StateCount);
                Report(InstallProgressState.Verify);
            }).ConfigureAwait(false);

        var downloadTasks = packsToDownload.ToList();

        progress.TotalCountToDownload = packs.Count;
        progress.DownloadedCount = packs.Count - downloadTasks.Count;

        progress.TotalBytesToDownload = totalBytesToDownload;
        progress.DownloadedBytes = alreadyDownloadedBytes;

        progress.TotalStateToComplete = packs.Count;
        progress.StateCount = packs.Count - downloadTasks.Count;

        if (downloadTasks.Count > 0)
        {
            Report(InstallProgressState.Download);
            SharedStatic.InstanceLogger.LogInformation(
                $"[HgInstaller] Downloading {downloadTasks.Count} preload files...");

            await Parallel.ForEachAsync(downloadTasks,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (pack, innerToken) =>
                {
                    var fileName = Path.GetFileName(new Uri(pack.Url!).LocalPath);
                    var filePath = Path.Combine(downloadDir, fileName);
                    var tempPath = filePath + ".tmp";
                    var expectedSize = long.Parse(pack.PackageSize ?? "0");

                    await HgFileDownloader.DownloadFileAsync(_downloadHttpClient, pack.Url!, tempPath,
                        expectedSize, innerToken, delta =>
                        {
                            Interlocked.Add(
                                ref progress.DownloadedBytes,
                                delta);

                            var nowTicks = DateTime.UtcNow.Ticks;

                            if (nowTicks - Interlocked.Read(ref lastReportTicks)
                                >= TimeSpan.TicksPerMillisecond * 500)
                            {
                                Interlocked.Exchange(
                                    ref lastReportTicks,
                                    nowTicks);

                                Report(
                                    InstallProgressState.Download);
                            }
                        }).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(pack.Md5))
                    {
                        var isMatch = await HgFileDownloader.CheckMd5Async(tempPath, pack.Md5, innerToken)
                            .ConfigureAwait(false);
                        if (!isMatch)
                        {
                            HgFileDownloader.ForceDeleteFile(tempPath);
                            throw new Exception($"MD5 Mismatch after downloading preload file {fileName}");
                        }
                    }

                    HgFileDownloader.ForceDeleteFile(filePath);
                    File.Move(tempPath, filePath, true);

                    Interlocked.Increment(ref progress.DownloadedCount);
                    Interlocked.Increment(ref progress.StateCount);
                    Report(InstallProgressState.Download);
                }).ConfigureAwait(false);
        }

        SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Preload completed.");
        progress.DownloadedBytes =
            progress.TotalBytesToDownload;

        progress.DownloadedCount =
            progress.TotalCountToDownload;

        progress.StateCount =
            progress.TotalStateToComplete;

        Report(InstallProgressState.Completed);
    }

    protected override Task UninstallAsyncInner(CancellationToken token)
    {
        GameManager.IsGameInstalled(out var isInstalled);
        if (!isInstalled) return Task.CompletedTask;

        GameManager.GetGamePath(out var installPath);
        if (string.IsNullOrEmpty(installPath)) return Task.CompletedTask;

        try
        {
            if (Directory.Exists(installPath))
                Directory.Delete(installPath, true);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError($"[HgCore] Uninstall failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _downloadHttpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
