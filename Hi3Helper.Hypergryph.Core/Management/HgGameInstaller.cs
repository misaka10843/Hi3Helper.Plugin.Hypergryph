using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Hypergryph.Core.Management.Api;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Core.Management;

[GeneratedComClass]
public partial class HgGameInstaller : GameInstallerBase
{
    private readonly HttpClient _downloadHttpClient;

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
        await InitAsync(token).ConfigureAwait(false);

        if (GameManager is not HgGameManager manager) return 0L;

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
        await InitAsync(token).ConfigureAwait(false);
        if (GameManager is not HgGameManager manager) return 0L;

        var packs = manager.GetGamePacks(gameInstallerKind);
        if (packs == null) return 0L;

        GameManager.GetGamePath(out var installPath);
        if (string.IsNullOrEmpty(installPath)) return 0L;

        var downloadDir = Path.Combine(installPath, "Diffs");
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
        return StartInstallCoreAsync(GameInstallerKind.Install, progressDelegate, progressStateDelegate, token);
    }

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        return StartInstallCoreAsync(GameInstallerKind.Update, progressDelegate, progressStateDelegate, token);
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
        await InitAsync(token).ConfigureAwait(false);

        if (GameManager is not HgGameManager manager)
            throw new InvalidOperationException("GameManager is not HgGameManager");

        var packs = manager.GetGamePacks(GameInstallerKind.Preload);
        if (packs == null || packs.Count == 0)
            throw new InvalidOperationException("No preload packs found in API response.");

        GameManager.GetGamePath(out var installPath);
        if (string.IsNullOrEmpty(installPath))
            throw new InvalidOperationException("Install path is missing.");

        var downloadDir = Path.Combine(installPath, "Diffs");
        Directory.CreateDirectory(downloadDir);

        InstallProgress progress = default;

        long lastReportTicks = DateTime.UtcNow.Ticks;

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
                                      await CheckMd5Async(filePath, pack.Md5, innerToken).ConfigureAwait(false);

                        if (isMatch)
                        {
                            Interlocked.Add(ref alreadyDownloadedBytes, size);
                            Interlocked.Add(ref progress.DownloadedBytes, size);
                            needsDownload = false;
                        }
                        else
                        {
                            ForceDeleteFile(filePath);
                        }
                    }
                    else
                    {
                        ForceDeleteFile(filePath);
                    }
                }

                if (needsDownload)
                {
                    if (File.Exists(tempPath) && new FileInfo(tempPath).Length > size)
                    {
                        ForceDeleteFile(tempPath);
                    }

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

                    await DownloadFileAsync(pack.Url!, tempPath, expectedSize, innerToken, delta =>
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
                    });

                    if (!string.IsNullOrEmpty(pack.Md5))
                    {
                        var isMatch = await CheckMd5Async(tempPath, pack.Md5, innerToken).ConfigureAwait(false);
                        if (!isMatch)
                        {
                            ForceDeleteFile(tempPath);
                            throw new Exception($"MD5 Mismatch after downloading preload file {fileName}");
                        }
                    }

                    ForceDeleteFile(filePath);
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

    private static void ForceDeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }
        catch
        {
            // ignored
        }

        File.Delete(filePath);
    }

    private async Task DownloadFileAsync(string url, string tempPath, long expectedSize, CancellationToken token,
        Action<long> onProgress)
    {
        long totalReported = 0;
        const int maxRetries = 3;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
            try
            {
                long existingLength = 0;
                if (File.Exists(tempPath))
                {
                    existingLength = new FileInfo(tempPath).Length;
                    if (existingLength > expectedSize)
                    {
                        ForceDeleteFile(tempPath);
                        existingLength = 0;
                    }
                }

                var diff = existingLength - totalReported;
                if (diff != 0)
                {
                    onProgress(diff);
                    totalReported += diff;
                }

                if (existingLength == expectedSize) return;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existingLength > 0)
                    request.Headers.Range = new RangeHeaderValue(existingLength, null);

                using var response = await _downloadHttpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (existingLength > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    existingLength = 0;
                    ForceDeleteFile(tempPath);
                    if (totalReported > 0)
                    {
                        onProgress(-totalReported);
                        totalReported = 0;
                    }
                }

                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var fs = new FileStream(tempPath,
                    existingLength > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    true);

                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    onProgress(read);
                    totalReported += read;
                }

                await fs.FlushAsync(token).ConfigureAwait(false);
                if (fs.Length != expectedSize)
                    throw new Exception($"Size mismatch. Expected {expectedSize}, Got {fs.Length}");

                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries)
                    throw new Exception(
                        $"Download failed after {maxRetries} attempts for {url} | Error: {ex.Message}");

                SharedStatic.InstanceLogger.LogWarning(
                    $"[HgInstaller] Preload download interrupted, retrying ({attempt}/{maxRetries}) for {Path.GetFileName(url)}...");
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
    }

    private static async Task<bool> CheckMd5Async(string filePath, string expectedMd5, CancellationToken token)
    {
        if (!File.Exists(filePath)) return false;

        using var md5 = MD5.Create();
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await md5.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return BitConverter.ToString(hashBytes).Replace("-", "")
            .Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
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