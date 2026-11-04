using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Hypergryph.Core.Management.Api;
using Hi3Helper.Hypergryph.Core.Utils;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Core.Management;

public class HgGameRepairer
{
    private readonly HttpClient _httpClient;
    private readonly string _installPath;
    private readonly HgGameManager _manager;

    public HgGameRepairer(HttpClient httpClient, HgGameManager manager, string installPath)
    {
        _httpClient = httpClient;
        _manager = manager;
        _installPath = installPath;
    }

    private static void ForceDeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            // 恢复为正常文件
            File.SetAttributes(filePath, FileAttributes.Normal);
        }
        catch
        {
            /* ignored */
        }

        File.Delete(filePath);
    }

    private string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new InvalidDataException($"Invalid repair manifest path: {relativePath}");
        string[] segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment != segment.TrimEnd(' ', '.') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
            segments[0].Equals("_Hi3_Hg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid repair manifest path: {relativePath}");
        string root = Path.GetFullPath(_installPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Repair path escapes the game directory: {relativePath}");
        string current = root;
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Repair path contains a link: {relativePath}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return result;
    }

    private async Task CommitVerifiedDownloadAsync(HgManifestNode node, CancellationToken token)
    {
        string targetPath = ResolvePath(node.Path!);
        string tempPath = ResolvePath(node.Path! + ".tmp");
        if (new FileInfo(tempPath).Length != node.Size || !await CheckMd5Async(tempPath, node.Md5!, token).ConfigureAwait(false))
        {
            ForceDeleteFile(tempPath);
            throw new InvalidDataException($"[HgRepairer] Downloaded file failed verification: {node.Path}");
        }
        token.ThrowIfCancellationRequested();
        if (File.Exists(targetPath)) File.SetAttributes(targetPath, FileAttributes.Normal);
        File.Move(tempPath, targetPath, true);
    }

    public async Task StartRepairAsync(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        var baseUrl = _manager.GameResourceBaseUrl;
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException("The API did not provide a game resource URL for verification.");
        }

        InstallProgress progress = default;

        void Report(InstallProgressState state)
        {
            progressDelegate?.Invoke(in progress);
            progressStateDelegate?.Invoke(state);
        }

        var lastVerifyReportTicks = DateTime.UtcNow.Ticks;
        var reportIntervalTicks = TimeSpan.FromMilliseconds(300).Ticks;

        void ReportVerifyThrottled()
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var oldTicks = Interlocked.Read(ref lastVerifyReportTicks);

            if (nowTicks - oldTicks >= reportIntervalTicks &&
                Interlocked.CompareExchange(
                    ref lastVerifyReportTicks,
                    nowTicks,
                    oldTicks) == oldTicks)
                Report(InstallProgressState.Verify);
        }

        Report(InstallProgressState.Preparing);

        var localManifestPath = ResolvePath("game_files");
        byte[]? encryptedManifest = null;

        if (File.Exists(localManifestPath))
        {
            SharedStatic.InstanceLogger.LogInformation(
                "[HgRepairer] Found local game_files. Reading from disk...");
            try
            {
                encryptedManifest = await File.ReadAllBytesAsync(localManifestPath, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    $"[HgRepairer] Failed to read local game_files: {ex.Message}. Falling back to CDN...");
            }
        }

        if (encryptedManifest == null || encryptedManifest.Length == 0)
        {
            var manifestUrl = $"{baseUrl}/game_files";
            SharedStatic.InstanceLogger.LogInformation(
                $"[HgRepairer] Downloading game_files manifest from CDN: {manifestUrl}");
            try
            {
                encryptedManifest = await _httpClient.GetByteArrayAsync(manifestUrl, token);
                await File.WriteAllBytesAsync(localManifestPath, encryptedManifest, token);
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogError($"[HgRepairer] Failed to download game_files: {ex.Message}");
                throw;
            }
        }

        var decryptedManifest = HgCrypto.DecryptBytesToString(encryptedManifest);
        if (string.IsNullOrEmpty(decryptedManifest))
        {
            throw new InvalidDataException("Cannot decrypt the game_files verification manifest.");
        }

        var manifestNodes = new List<HgManifestNode>();
        var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = new StringReader(decryptedManifest))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var node = JsonSerializer.Deserialize(line, HgApiContext.Default.HgManifestNode);
                    if (node != null && !string.IsNullOrEmpty(node.Path))
                    {
                        if (node.Path.Equals("config.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        if (node.Size < 0 || node.Md5?.Length != 32 || !node.Md5.All(Uri.IsHexDigit))
                            throw new InvalidDataException($"Invalid verification manifest entry: {node.Path}");
                        if (!manifestPaths.Add(ResolvePath(node.Path)))
                            throw new InvalidDataException($"Duplicate verification manifest path: {node.Path}");
                        manifestNodes.Add(node);
                    }
                    else throw new InvalidDataException("Verification manifest entry has no path.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Invalid JSON in the verification manifest.", ex);
                }
            }
        }

        if (manifestNodes.Count == 0) throw new InvalidDataException("The verification manifest contains no game files.");

        var totalVerifyBytes = manifestNodes.Sum(n => n.Size);
        SharedStatic.InstanceLogger.LogInformation(
            $"[HgRepairer] Verifying {manifestNodes.Count} local files...");

        progress.TotalCountToDownload = manifestNodes.Count;
        progress.DownloadedCount = 0;
        progress.TotalBytesToDownload = totalVerifyBytes;
        progress.DownloadedBytes = 0;
        progress.TotalStateToComplete = manifestNodes.Count;
        progress.StateCount = 0;
        Report(InstallProgressState.Verify);

        var brokenFiles = new ConcurrentBag<HgManifestNode>();
        long brokenSize = 0;

        await Parallel.ForEachAsync(manifestNodes,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (node, innerToken) =>
            {
                var isOk = false;
                var localPath = ResolvePath(node.Path!);

                if (File.Exists(localPath))
                {
                    var fi = new FileInfo(localPath);

                    if (fi.Length == node.Size)
                    {
                        isOk = await CheckMd5Async(
                            localPath,
                            node.Md5!,
                            innerToken,
                            delta =>
                            {
                                Interlocked.Add(
                                    ref progress.DownloadedBytes,
                                    delta);
                                ReportVerifyThrottled();
                            }).ConfigureAwait(false);
                    }
                    else
                    {
                        Interlocked.Add(
                            ref progress.DownloadedBytes,
                            node.Size);
                        ReportVerifyThrottled();
                    }
                }
                else
                {
                    Interlocked.Add(
                        ref progress.DownloadedBytes,
                        node.Size);
                    ReportVerifyThrottled();
                }

                if (!isOk)
                {
                    brokenFiles.Add(node);
                    Interlocked.Add(ref brokenSize, node.Size);
                }

                Interlocked.Increment(ref progress.DownloadedCount);
                Interlocked.Increment(ref progress.StateCount);
                Report(InstallProgressState.Verify);
            }).ConfigureAwait(false);

        var downloadList = brokenFiles.ToList();
        if (downloadList.Count == 0)
        {
            SharedStatic.InstanceLogger.LogInformation(
                "[HgRepairer] Verification Passed: All files are perfect.");
            return;
        }


        SharedStatic.InstanceLogger.LogInformation(
            $"[HgRepairer] Found {downloadList.Count} missing/corrupted files. Initiating download...");
        progress.TotalCountToDownload = downloadList.Count;
        progress.DownloadedCount = 0;
        progress.TotalBytesToDownload = brokenSize;
        progress.DownloadedBytes = 0;
        progress.TotalStateToComplete = downloadList.Count;
        progress.StateCount = 0;
        Report(InstallProgressState.Download);

        await Parallel.ForEachAsync(downloadList,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (node, innerToken) =>
            {
                var downloadUrl = $"{baseUrl}/{node.Path}";
                var targetPath = ResolvePath(node.Path!);
                var tempPath = ResolvePath(node.Path! + ".tmp");

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                await DownloadFileAsync(downloadUrl, tempPath, node.Size, innerToken, delta =>
                {
                    Interlocked.Add(ref progress.DownloadedBytes, delta);
                    Report(InstallProgressState.Download);
                });

                await CommitVerifiedDownloadAsync(node, innerToken).ConfigureAwait(false);

                Interlocked.Increment(ref progress.DownloadedCount);
                Interlocked.Increment(ref progress.StateCount);
                Report(InstallProgressState.Download);
            });

        SharedStatic.InstanceLogger.LogInformation(
            $"[HgRepairer] Re-verifying {downloadList.Count} newly downloaded files...");

        progress.TotalCountToDownload = downloadList.Count;
        progress.DownloadedCount = 0;
        progress.TotalBytesToDownload = brokenSize;
        progress.DownloadedBytes = 0;
        progress.TotalStateToComplete = downloadList.Count;
        progress.StateCount = 0;
        Report(InstallProgressState.Verify);

        await Parallel.ForEachAsync(downloadList,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (node, innerToken) =>
            {
                var targetPath = ResolvePath(node.Path!);

                var md5Match = await CheckMd5Async(targetPath, node.Md5!, innerToken);
                if (!md5Match)
                {
                    throw new Exception($"[HgRepairer] MD5 mismatch after downloading {node.Path}");
                }

                Interlocked.Add(ref progress.DownloadedBytes, node.Size);
                Interlocked.Increment(ref progress.DownloadedCount);
                Interlocked.Increment(ref progress.StateCount);
                Report(InstallProgressState.Verify);
            });

        SharedStatic.InstanceLogger.LogInformation(
            "[HgRepairer] Integrity restored and double-verified completely!");
    }

    private async Task DownloadFileAsync(string url, string tempPath, long expectedSize, CancellationToken token,
        Action<long> onProgress)
    {
        long totalReported = 0;
        var maxRetries = 3;

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

                using var response =
                    await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

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

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                await using var fs = new FileStream(tempPath, existingLength > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, true);

                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read, token);
                        onProgress(read);
                        totalReported += read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await fs.FlushAsync(token);
                var finalSize = fs.Length;

                if (finalSize != expectedSize)
                    throw new Exception($"Size mismatch. Expected {expectedSize}, Got {finalSize}");

                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries)
                    throw new Exception($"Download failed after {maxRetries} attempts for {url} | Error: {ex.Message}");

                SharedStatic.InstanceLogger.LogWarning(
                    $"[HgRepairer] Download interrupted, retrying ({attempt}/{maxRetries}) for {Path.GetFileName(url)}...");
                await Task.Delay(1000, token);
            }
    }

    private async Task<bool> CheckMd5Async(
        string filePath,
        string expectedMd5,
        CancellationToken token,
        Action<long>? onProgress = null)
    {
        if (!File.Exists(filePath)) return false;

        using var md5 = MD5.Create();

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            true);

        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);

        try
        {
            int read;

            while ((read = await stream.ReadAsync(
                       buffer.AsMemory(0, buffer.Length),
                       token).ConfigureAwait(false)) > 0)
            {
                md5.TransformBlock(
                    buffer,
                    0,
                    read,
                    null,
                    0);

                onProgress?.Invoke(read);
            }

            md5.TransformFinalBlock(
                Array.Empty<byte>(),
                0,
                0);

            var actualMd5 = BitConverter.ToString(md5.Hash!)
                .Replace("-", "")
                .ToLowerInvariant();

            return actualMd5.Equals(
                expectedMd5,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
