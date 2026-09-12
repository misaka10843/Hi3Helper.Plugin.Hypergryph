using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Hypergryph.Yostar.Api;
using Hi3Helper.Hypergryph.Yostar.Storage;
using Hi3Helper.Hypergryph.Yostar.Utility;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Yostar.Management;

[GeneratedComClass]
public partial class YostarGameInstaller : GameInstallerBase
{
    private static readonly bool[] RetryUsePrimary =
        [true, true, true, true, false, false, false, true, true, true];

    public YostarGameInstaller(IGameManager? gameManager) : base(gameManager)
    {
    }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        if (GameManager is not YostarGameManager manager)
            throw new InvalidOperationException("GameManager is not YostarGameManager.");
        await manager.GetTargetPackageAsync(true, token).ConfigureAwait(false);
        return 0;
    }

    protected override async Task<long> GetGameSizeAsyncInner(GameInstallerKind gameInstallerKind,
        CancellationToken token)
    {
        if (gameInstallerKind == GameInstallerKind.Preload) return 0L;
        YostarInstallPlan plan = await CreatePlanAsync(token).ConfigureAwait(false);
        return plan.DownloadFiles.Sum(static file => file.SizeValue);
    }

    protected override async Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind gameInstallerKind,
        CancellationToken token)
    {
        if (gameInstallerKind == GameInstallerKind.Preload) return 0L;
        YostarInstallPlan plan = await CreatePlanAsync(token).ConfigureAwait(false);
        long downloaded = 0;
        foreach (YostarManifestFile file in plan.DownloadFiles)
        {
            token.ThrowIfCancellationRequested();
            string tempPath = GetTargetFilePath(plan.GamePath, file.Path) + ".tmp";
            if (File.Exists(tempPath)) downloaded += Math.Min(new FileInfo(tempPath).Length, file.SizeValue);
        }

        return downloaded;
    }

    protected override Task StartInstallAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        return RunInstallAsync(progressDelegate, progressStateDelegate, token);
    }

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        return RunInstallAsync(progressDelegate, progressStateDelegate, token);
    }

    protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        throw new NotSupportedException("The Yostar launcher protocol does not expose a separate preload package.");
    }

    private async Task RunInstallAsync(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        YostarInstallPlan plan = await CreatePlanAsync(token).ConfigureAwait(false);
        var progress = new InstallProgress
        {
            TotalCountToDownload = plan.DownloadFiles.Count,
            TotalBytesToDownload = plan.DownloadFiles.Sum(static file => file.SizeValue),
            TotalStateToComplete = plan.DownloadFiles.Count + plan.DeleteFiles.Count
        };
        var progressLock = new object();

        void Report(InstallProgressState state)
        {
            lock (progressLock)
            {
                progressDelegate?.Invoke(in progress);
                progressStateDelegate?.Invoke(state);
            }
        }

        Report(InstallProgressState.Preparing);
        Directory.CreateDirectory(plan.GamePath);

        long existingTempBytes = 0;
        foreach (YostarManifestFile file in plan.DownloadFiles)
        {
            string tempPath = GetTargetFilePath(plan.GamePath, file.Path) + ".tmp";
            if (File.Exists(tempPath))
            {
                long length = new FileInfo(tempPath).Length;
                if (length <= file.SizeValue) existingTempBytes += length;
            }
        }

        progress.DownloadedBytes = existingTempBytes;
        long reportedBytes = existingTempBytes;
        int completedDownloads = 0;
        int completedStates = 0;
        long lastReportTicks = DateTime.UtcNow.Ticks;

        Report(InstallProgressState.Download);
        await Parallel.ForEachAsync(plan.DownloadFiles,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (file, innerToken) =>
            {
                await DownloadAndInstallFileAsync(plan, file, innerToken, delta =>
                {
                    long current = Interlocked.Add(ref reportedBytes, delta);
                    Interlocked.Exchange(ref progress.DownloadedBytes, current);
                    long now = DateTime.UtcNow.Ticks;
                    long previous = Interlocked.Read(ref lastReportTicks);
                    if (now - previous >= TimeSpan.TicksPerMillisecond * 300 &&
                        Interlocked.CompareExchange(ref lastReportTicks, now, previous) == previous)
                        Report(InstallProgressState.Download);
                }).ConfigureAwait(false);

                progress.DownloadedCount = Interlocked.Increment(ref completedDownloads);
                progress.StateCount = Interlocked.Increment(ref completedStates);
                Report(InstallProgressState.Download);
            }).ConfigureAwait(false);

        if (plan.DeleteFiles.Count > 0)
        {
            Report(InstallProgressState.Removing);
            foreach (string relativePath in plan.DeleteFiles)
            {
                token.ThrowIfCancellationRequested();
                string targetPath = GetTargetFilePath(plan.GamePath, relativePath);
                ForceDeleteFile(targetPath);
                progress.StateCount = Interlocked.Increment(ref completedStates);
                Report(InstallProgressState.Removing);
            }
        }

        Report(InstallProgressState.Install);
        await YostarLocalStorage.WriteMetadataAsync(plan.GamePath, plan.Manager.Options, plan.Package.Config,
            plan.Package.Manifest.Files, token).ConfigureAwait(false);
        plan.Manager.CompleteManualVerifyRequest();

        progress.DownloadedBytes = progress.TotalBytesToDownload;
        progress.DownloadedCount = progress.TotalCountToDownload;
        progress.StateCount = progress.TotalStateToComplete;
        Report(InstallProgressState.Completed);
    }

    private async Task<YostarInstallPlan> CreatePlanAsync(CancellationToken token)
    {
        if (GameManager is not YostarGameManager manager)
            throw new InvalidOperationException("GameManager is not YostarGameManager.");
        string gamePath = EnsureAndGetGamePath();
        YostarTargetPackage package = await manager.GetTargetPackageAsync(true, token).ConfigureAwait(false);
        YostarLocalManifest? localManifest = YostarLocalStorage.ReadManifest(gamePath);
        bool fullVerify = manager.IsManualVerifyRequested;

        var localFiles = localManifest?.Files.ToDictionary(NormalizeManifestPath,
            StringComparer.OrdinalIgnoreCase) ??
                         new Dictionary<string, YostarManifestFile>(StringComparer.OrdinalIgnoreCase);
        var remoteFiles = package.Manifest.Files.ToDictionary(NormalizeManifestPath,
            StringComparer.OrdinalIgnoreCase);
        var downloadFiles = new List<YostarManifestFile>();

        foreach (YostarManifestFile remoteFile in package.Manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            string targetPath = GetTargetFilePath(gamePath, remoteFile.Path);
            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length != remoteFile.SizeValue)
            {
                downloadFiles.Add(remoteFile);
                continue;
            }

            if (fullVerify)
            {
                string hash = await YostarCrc64.ComputeFileAsync(targetPath, token).ConfigureAwait(false);
                if (!string.Equals(hash, remoteFile.Hash, StringComparison.Ordinal)) downloadFiles.Add(remoteFile);
                continue;
            }

            if (localFiles.TryGetValue(NormalizeManifestPath(remoteFile), out YostarManifestFile? localFile) &&
                !string.Equals(localFile.Hash, remoteFile.Hash, StringComparison.Ordinal))
                downloadFiles.Add(remoteFile);
        }

        var deleteFiles = localFiles.Keys.Where(path => !remoteFiles.ContainsKey(path)).ToList();
        return new YostarInstallPlan(manager, package, gamePath, downloadFiles, deleteFiles);
    }

    private async Task DownloadAndInstallFileAsync(YostarInstallPlan plan, YostarManifestFile file,
        CancellationToken token, Action<long> reportProgress)
    {
        string targetPath = GetTargetFilePath(plan.GamePath, file.Path);
        string tempPath = targetPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (File.Exists(tempPath) && new FileInfo(tempPath).Length > file.SizeValue) ForceDeleteFile(tempPath);

        Exception? lastError = null;
        for (int attempt = 0; attempt < RetryUsePrimary.Length; attempt++)
        {
            token.ThrowIfCancellationRequested();
            bool usePrimary = RetryUsePrimary[attempt];
            string? domain = usePrimary ? plan.Package.Cdn.PrimaryCdn : plan.Package.Cdn.BackupCdn;
            if (string.IsNullOrWhiteSpace(domain)) continue;

            try
            {
                await DownloadAttemptAsync(plan.Manager.ApiClient.DownloadHttpClient,
                    BuildDownloadUri(domain, plan.Package.Manifest.Source, file.Path), tempPath, file.SizeValue,
                    token, reportProgress).ConfigureAwait(false);

                string hash = await YostarCrc64.ComputeFileAsync(tempPath, token).ConfigureAwait(false);
                if (!string.Equals(hash, file.Hash, StringComparison.Ordinal))
                {
                    long invalidLength = new FileInfo(tempPath).Length;
                    ForceDeleteFile(tempPath);
                    reportProgress(-invalidLength);
                    throw new InvalidDataException(
                        $"CRC64 mismatch for {file.Path}. Expected {file.Hash}, got {hash}.");
                }

                ForceDeleteFile(targetPath);
                File.Move(tempPath, targetPath, true);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                SharedStatic.InstanceLogger.LogWarning(
                    $"[Yostar] Download attempt {attempt + 1}/{RetryUsePrimary.Length} failed for {file.Path}: {ex.Message}");
                if (attempt + 1 < RetryUsePrimary.Length)
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }

        throw new IOException($"Failed to download {file.Path} after all retries.", lastError);
    }

    private static async Task DownloadAttemptAsync(HttpClient client, Uri uri, string tempPath, long expectedSize,
        CancellationToken token, Action<long> reportProgress)
    {
        long existingLength = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0L;
        if (existingLength > expectedSize)
        {
            ForceDeleteFile(tempPath);
            reportProgress(-existingLength);
            existingLength = 0;
        }
        if (existingLength == expectedSize)
        {
            // Zero-byte manifest entries need a placeholder temp file so the
            // caller can compute CRC (empty file = "0") and move it into place.
            if (expectedSize == 0 && !File.Exists(tempPath))
                await using (FileStream empty = File.Create(tempPath)) { }
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existingLength > 0) request.Headers.Range = new RangeHeaderValue(existingLength, null);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);

        if (existingLength > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            ForceDeleteFile(tempPath);
            reportProgress(-existingLength);
            existingLength = 0;
        }

        response.EnsureSuccessStatusCode();
        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(tempPath,
            existingLength > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            reportProgress(read);
        }

        await output.FlushAsync(token).ConfigureAwait(false);
        if (output.Length != expectedSize)
            throw new InvalidDataException(
                $"Downloaded file size mismatch for {uri}. Expected {expectedSize}, got {output.Length}.");
    }

    private static Uri BuildDownloadUri(string domain, string? source, string filePath)
    {
        if (!Uri.TryCreate(domain, UriKind.Absolute, out Uri? domainUri))
            throw new InvalidDataException("Yostar returned an invalid CDN domain.");
        string combined = string.Join('/', new[] { source, filePath }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim('/')));
        string[] segments = combined.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new InvalidDataException("Yostar returned an empty download path.");
        segments[^1] = Uri.EscapeDataString(Uri.UnescapeDataString(segments[^1]));
        return new Uri(domainUri, string.Join('/', segments));
    }

    private static string GetTargetFilePath(string gamePath, string manifestPath)
    {
        string relativePath = NormalizeManifestPath(manifestPath).Replace('/', Path.DirectorySeparatorChar);
        string root = Path.GetFullPath(gamePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest path escapes the game directory: {manifestPath}");
        return result;
    }

    private static string NormalizeManifestPath(YostarManifestFile file) => NormalizeManifestPath(file.Path);

    private static string NormalizeManifestPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static void ForceDeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
        }

        File.Delete(path);
    }

    protected override Task UninstallAsyncInner(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string gamePath = EnsureAndGetGamePath();
        if (Directory.Exists(gamePath)) Directory.Delete(gamePath, true);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private sealed record YostarInstallPlan(
        YostarGameManager Manager,
        YostarTargetPackage Package,
        string GamePath,
        List<YostarManifestFile> DownloadFiles,
        List<string> DeleteFiles);
}
