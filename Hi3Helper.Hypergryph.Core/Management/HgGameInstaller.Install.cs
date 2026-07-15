using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Hypergryph.Core.Management.Api;
using Hi3Helper.Hypergryph.Core.Utils;
using Microsoft.Extensions.Logging;
using SevenZipExtractor;
using SevenZipExtractor.Event;
using SharpHDiffPatch.Core;

namespace Hi3Helper.Hypergryph.Core.Management;

public partial class HgGameInstaller
{
    private sealed class Install
    {
        private readonly HgGameInstaller _owner;

        public Install(HgGameInstaller owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
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
                /* ignored */
            }

            File.Delete(filePath);
        }

        public async Task RunAsync(GameInstallerKind kind, InstallProgressDelegate? progressDelegate,
            InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
        {
            _ = kind;
            await _owner.InitAsync(token).ConfigureAwait(false);

            if (_owner.GameManager is not HgGameManager manager || manager.GamePacks == null ||
                manager.GamePacks.Count == 0)
                throw new InvalidOperationException("No download packs found in API response.");

            _owner.GameManager.GetGamePath(out var installPath);
            if (string.IsNullOrEmpty(installPath)) throw new InvalidOperationException("Install path is missing.");

            var downloadDir = Path.Combine(installPath, "Diffs");
            Directory.CreateDirectory(downloadDir);

            InstallProgress progress = default;

            void Report(InstallProgressState state)
            {
                progressDelegate?.Invoke(in progress);
                progressStateDelegate?.Invoke(state);
            }

            Report(InstallProgressState.Preparing);
            SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Verifying existing packages...");

            var packsToDownload = new ConcurrentBag<HgPack>();
            long totalBytesToDownload = 0;
            long alreadyDownloadedBytes = 0;

            foreach (var pack in manager.GamePacks)
                if (long.TryParse(pack.PackageSize, out var s))
                    totalBytesToDownload += s;

            progress.TotalCountToDownload = manager.GamePacks.Count;
            progress.DownloadedCount = 0;
            progress.TotalBytesToDownload = totalBytesToDownload;
            progress.DownloadedBytes = 0;
            progress.TotalStateToComplete = manager.GamePacks.Count;
            progress.StateCount = 0;
            Report(InstallProgressState.Verify);

            await Parallel.ForEachAsync(manager.GamePacks,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (pack, innerToken) =>
                {
                    if (string.IsNullOrEmpty(pack.Url)) return;
                    long.TryParse(pack.PackageSize, out var size);

                    var fileName = Path.GetFileName(new Uri(pack.Url!).LocalPath);
                    var filePath = Path.Combine(downloadDir, fileName);
                    var tempPath = filePath + ".tmp";

                    var needsDownload = true;

                    if (File.Exists(filePath))
                    {
                        var fi = new FileInfo(filePath);
                        if (fi.Length == size)
                        {
                            var isMatch = string.IsNullOrEmpty(pack.Md5) ||
                                          await CheckMd5Async(filePath, pack.Md5, innerToken);
                            if (isMatch)
                            {
                                Interlocked.Add(ref alreadyDownloadedBytes, size);
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
                        if (File.Exists(tempPath))
                            if (new FileInfo(tempPath).Length > size)
                                try
                                {
                                    ForceDeleteFile(tempPath);
                                }
                                catch (Exception ex)
                                {
                                    SharedStatic.InstanceLogger.LogDebug(
                                        $"[HgCore] Temp file delete failed: {ex.Message}");
                                }

                        packsToDownload.Add(pack);
                    }

                    Interlocked.Add(ref progress.DownloadedBytes, size);
                    Interlocked.Increment(ref progress.DownloadedCount);
                    Interlocked.Increment(ref progress.StateCount);
                    Report(InstallProgressState.Verify);
                });

            var downloadTasks = packsToDownload.ToList();
            progress.TotalCountToDownload = downloadTasks.Count;
            progress.DownloadedCount = 0;
            progress.TotalBytesToDownload = totalBytesToDownload;
            progress.DownloadedBytes = alreadyDownloadedBytes;
            progress.TotalStateToComplete = downloadTasks.Count;
            progress.StateCount = 0;

            //断点续传下载
            if (downloadTasks.Count > 0)
            {
                Report(InstallProgressState.Download);
                SharedStatic.InstanceLogger.LogInformation(
                    $"[HgInstaller] Downloading {downloadTasks.Count} files...");

                await Parallel.ForEachAsync(downloadTasks, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = token
                }, async (pack, innerToken) =>
                {
                    var fileName = Path.GetFileName(new Uri(pack.Url!).LocalPath);
                    var filePath = Path.Combine(downloadDir, fileName);
                    var tempPath = filePath + ".tmp";
                    var expectedSize = long.Parse(pack.PackageSize ?? "0");

                    await DownloadFileAsync(pack.Url!, tempPath, expectedSize, innerToken, delta =>
                    {
                        Interlocked.Add(ref progress.DownloadedBytes, delta);
                        Report(InstallProgressState.Download);
                    }).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(pack.Md5))
                    {
                        var isMatch = await CheckMd5Async(tempPath, pack.Md5, innerToken);
                        if (!isMatch)
                        {
                            try
                            {
                                ForceDeleteFile(tempPath);
                            }
                            catch (Exception ex)
                            {
                                SharedStatic.InstanceLogger.LogDebug(
                                    $"[HgCore] Temp file delete failed: {ex.Message}");
                            }

                            throw new Exception($"MD5 Mismatch after downloading {fileName}");
                        }
                    }

                    ForceDeleteFile(filePath);
                    File.Move(tempPath, filePath);

                    Interlocked.Increment(ref progress.DownloadedCount);
                    Interlocked.Increment(ref progress.StateCount);
                    Report(InstallProgressState.Download);
                });
            }

            if (manager.IsDeltaUpdate)
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] Delta update mechanism confirmed. Initializing sandbox extraction...");
                var tempExtractDir = Path.Combine(installPath, "_Hg_DeltaTemp");
                // DEBUG
                var skipExtractForDebug = false;

                if (!skipExtractForDebug)
                {
                    Report(InstallProgressState.Updating);
                    if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true);
                    Directory.CreateDirectory(tempExtractDir);

                    await ExtractPackagesAsync(
                        downloadDir,
                        tempExtractDir,
                        manager.PatchCdKey,
                        token,
                        (extractedBytes, totalBytes) =>
                        {
                            progress.DownloadedBytes = extractedBytes;
                            progress.TotalBytesToDownload = totalBytes;
                            Report(InstallProgressState.Updating);
                        });
                }

                Report(InstallProgressState.Removing);
                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] Cleaning up legacy files and updating core components...");

                var deleteListPath = Path.Combine(tempExtractDir, "delete_files.txt");
                if (File.Exists(deleteListPath))
                {
                    var filesToDelete = File.ReadAllLines(deleteListPath);
                    foreach (var fileLine in filesToDelete)
                    {
                        if (string.IsNullOrWhiteSpace(fileLine)) continue;
                        var targetDeletePath = Path.Combine(installPath, fileLine.Trim().Replace("/", "\\"));
                        if (File.Exists(targetDeletePath))
                            try
                            {
                                ForceDeleteFile(targetDeletePath);
                            }
                            catch (Exception ex)
                            {
                                SharedStatic.InstanceLogger.LogDebug($"[HgCore] File delete failed: {ex.Message}");
                            }
                    }
                }

                Report(InstallProgressState.Updating);
                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] Copying static update files...");
                foreach (var newPath in Directory.GetFiles(tempExtractDir, "*.*", SearchOption.AllDirectories))
                {
                    var relPath = Path.GetRelativePath(tempExtractDir, newPath);

                    if (relPath.StartsWith("vfs_files", StringComparison.OrdinalIgnoreCase) ||
                        relPath.StartsWith("diff_", StringComparison.OrdinalIgnoreCase) ||
                        relPath.Equals("patch.json", StringComparison.OrdinalIgnoreCase) ||
                        relPath.Equals("delete_files.txt", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (relPath.Equals("config.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(newPath, Path.Combine(installPath, "config.ini.new"), true);
                        continue;
                    }

                    var destPath = Path.Combine(installPath, relPath);
                    var destDir = Path.GetDirectoryName(destPath)!;
                    if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                    ForceDeleteFile(destPath);
                    File.Copy(newPath, destPath, true);
                }

                await ApplyDeltaPatchAsync(tempExtractDir, installPath, manager.PatchManifestUrl, token,
                    (patchedBytes, totalBytes) =>
                    {
                        progress.DownloadedBytes = patchedBytes;
                        progress.TotalBytesToDownload = totalBytes;
                        Report(InstallProgressState.Updating);
                    });

                // 调试模式下保留沙盒目录，非调试模式下执行清理
                if (!skipExtractForDebug)
                    try
                    {
                        Directory.Delete(tempExtractDir, true);
                    }
                    catch (Exception ex)
                    {
                        SharedStatic.InstanceLogger.LogDebug($"[HgCore] Temp dir cleanup failed: {ex.Message}");
                    }
            }
            else
            {
                Report(InstallProgressState.Install);
                SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Full update mechanism confirmed.");
                await ExtractPackagesAsync(
                    downloadDir,
                    installPath,
                    null,
                    token,
                    (extractedBytes, totalBytes) =>
                    {
                        progress.DownloadedBytes = extractedBytes;
                        progress.TotalBytesToDownload = totalBytes;
                        Report(InstallProgressState.Install);
                    });
            }

            try
            {
                Directory.Delete(downloadDir, true);
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogDebug($"[HgCore] Download dir cleanup failed: {ex.Message}");
            }

            SharedStatic.InstanceLogger.LogInformation(
                "[HgInstaller] Update phase completed. Initiating post-update integrity verification...");

            try
            {
                var repairer = new HgGameRepairer(_owner._downloadHttpClient, manager, installPath);
                await repairer.StartRepairAsync(progressDelegate, progressStateDelegate, token);
                var newConfigPath = Path.Combine(installPath, "config.ini.new");
                var targetConfigPath = Path.Combine(installPath, "config.ini");
                if (File.Exists(newConfigPath))
                {
                    ForceDeleteFile(targetConfigPath);
                    File.Copy(newConfigPath, targetConfigPath, true);
                    try
                    {
                        ForceDeleteFile(newConfigPath);
                    }
                    catch
                    {
                    }

                    SharedStatic.InstanceLogger.LogInformation(
                        "[HgInstaller] config.ini successfully updated after full verification.");
                }
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogError(
                    $"[HgInstaller] Post-update integrity verification failed: {ex}");
                throw;
            }

            Report(InstallProgressState.Completed);
        }

        private async Task ApplyDeltaPatchAsync(string tempExtractDir, string targetGameRoot, string? patchJsonUrl,
            CancellationToken token, Action<long, long>? progressCallback = null)
        {
            HgPatchManifest? manifest = null;
            var localPatchJsonPath = Path.Combine(tempExtractDir, "patch.json");

            // 检查增量包中是否有修补文件清单
            if (File.Exists(localPatchJsonPath) && new FileInfo(localPatchJsonPath).Length > 0)
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] Found local patch.json in sandbox. Reading manifest...");
                try
                {
                    await using var fs = File.OpenRead(localPatchJsonPath);
                    manifest = await JsonSerializer.DeserializeAsync(fs, HgApiContext.Default.HgPatchManifest,
                        token);
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        $"[HgInstaller] Failed to parse local patch.json: {ex.Message}. Falling back to API if available.");
                }
            }

            // 检查API是否有修补文件清单
            if (manifest == null && !string.IsNullOrEmpty(patchJsonUrl))
            {
                SharedStatic.InstanceLogger.LogInformation(
                    $"[HgInstaller] Fetching delta patch manifest from URL: {patchJsonUrl}");
                using var httpClient = new HttpClient();
                using var response =
                    await httpClient.GetAsync(patchJsonUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                manifest = await response.Content.ReadFromJsonAsync(HgApiContext.Default.HgPatchManifest,
                    token);
            }
            // 如果两边都没有就直接跳过修补，代表仅覆盖即可
            else if (manifest == null)
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] No valid patch.json found and no URL provided. Assuming static-only delta update. Skipping VFS patching.");
                return;
            }

            if (manifest == null || manifest.Files == null)
                throw new InvalidDataException(
                    "[HgInstaller] Failed to load or deserialize Patch Manifest (patch.json).");

            var vfsBasePath = Path.Combine(targetGameRoot,
                (manifest.VfsBasePath ?? "Hg_Data/StreamingAssets/VFS").Replace("/", "\\"));

            var totalPatchSize = manifest.Files.Sum(f => f.Size);
            long currentPatchedSize = 0;

            SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Building temporary extraction file map...");
            var extractFileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                var allExtractedFiles = Directory.GetFiles(tempExtractDir, "*", SearchOption.AllDirectories);
                foreach (var file in allExtractedFiles) extractFileMap[Path.GetFileName(file)] = file;
            }, token);

            SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Starting VFS delta patch pipeline...");

            foreach (var fileNode in manifest.Files)
            {
                token.ThrowIfCancellationRequested();

                var targetFilePath = Path.Combine(vfsBasePath, fileNode.Name!.Replace("/", "\\"));
                var targetDir = Path.GetDirectoryName(targetFilePath)!;
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                if (!string.IsNullOrEmpty(fileNode.LocalPath))
                {
                    var sourceExtractedFile = Path.Combine(tempExtractDir, fileNode.LocalPath.Replace("/", "\\"));

                    if (!File.Exists(sourceExtractedFile) &&
                        extractFileMap.TryGetValue(Path.GetFileName(fileNode.LocalPath), out var foundPath))
                        sourceExtractedFile = foundPath;

                    if (File.Exists(sourceExtractedFile))
                    {
                        ForceDeleteFile(targetFilePath); // 拷贝前先强删目的文件
                        File.Copy(sourceExtractedFile, targetFilePath, true);
                        Interlocked.Add(ref currentPatchedSize, fileNode.Size);
                        progressCallback?.Invoke(currentPatchedSize, totalPatchSize);
                        SharedStatic.InstanceLogger.LogDebug($"[HgInstaller] [Copy] {fileNode.Name}");
                    }
                }
                else if (fileNode.Patches != null && fileNode.Patches.Count > 0)
                {
                    var patchInfo = fileNode.Patches[0];
                    var baseFilePath = Path.Combine(vfsBasePath, patchInfo.BaseFile!.Replace("/", "\\"));
                    var diffFilePath = Path.Combine(tempExtractDir, patchInfo.PatchPath!.Replace("/", "\\"));

                    if (!File.Exists(diffFilePath) && extractFileMap.TryGetValue(Path.GetFileName(patchInfo.PatchPath!),
                            out var foundDiffPath))
                        diffFilePath = foundDiffPath;

                    if (File.Exists(baseFilePath) && File.Exists(diffFilePath))
                    {
                        if (new FileInfo(diffFilePath).Length == 0)
                        {
                            if (!string.Equals(baseFilePath, targetFilePath, StringComparison.OrdinalIgnoreCase))
                            {
                                ForceDeleteFile(targetFilePath);
                                File.Copy(baseFilePath, targetFilePath, true);
                            }

                            Interlocked.Add(ref currentPatchedSize, fileNode.Size);
                            progressCallback?.Invoke(currentPatchedSize, totalPatchSize);
                            SharedStatic.InstanceLogger.LogDebug($"[HgInstaller] [Skip Empty Patch] {fileNode.Name}");
                        }
                        else
                        {
                            var tempOutPath = targetFilePath + ".tmp";
                            try
                            {
                                var hdiffPatcher = new HDiffPatch();
                                hdiffPatcher.Initialize(diffFilePath);

                                Action<long> onPatchProgress = deltaBytes =>
                                {
                                    Interlocked.Add(ref currentPatchedSize, deltaBytes);
                                    progressCallback?.Invoke(currentPatchedSize, totalPatchSize);
                                };

                                hdiffPatcher.Patch(baseFilePath, tempOutPath, true, onPatchProgress, token);

                                ForceDeleteFile(targetFilePath); // 移动前先强删旧文件
                                File.Move(tempOutPath, targetFilePath, true);
                                SharedStatic.InstanceLogger.LogDebug($"[HgInstaller] [Patch] {fileNode.Name}");
                            }
                            catch (Exception ex)
                            {
                                SharedStatic.InstanceLogger.LogError(
                                    $"[HgInstaller] Delta patch failed for {fileNode.Name}. Error: {ex.Message}");
                                if (File.Exists(tempOutPath)) ForceDeleteFile(tempOutPath);
                                throw;
                            }
                        }
                    }
                }
            }

            SharedStatic.InstanceLogger.LogInformation(
                "[HgInstaller] VFS delta patch pipeline executed successfully.");
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

                    using var response = await _owner._downloadHttpClient.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, token);

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
                    await using var fs = new FileStream(tempPath,
                        existingLength > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920,
                        true);

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
                        throw new Exception(
                            $"Download failed after {maxRetries} attempts for {url} | Error: {ex.Message}");
                    SharedStatic.InstanceLogger.LogWarning(
                        $"[HgInstaller] Download interrupted, retrying ({attempt}/{maxRetries}) for {Path.GetFileName(url)}...");
                    await Task.Delay(1000, token);
                }
        }

        private async Task<bool> CheckMd5Async(string filePath, string expectedMd5, CancellationToken token)
        {
            if (!File.Exists(filePath)) return false;
            using var md5 = MD5.Create();
            await using var stream = File.OpenRead(filePath);
            var hashBytes = await md5.ComputeHashAsync(stream, token);
            return BitConverter.ToString(hashBytes).Replace("-", "")
                .Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
        }

        private async Task ExtractPackagesAsync(
            string sourceDir,
            string destDir,
            string? password,
            CancellationToken token,
            Action<long, long>? progressCallback)
        {
            SharedStatic.InstanceLogger.LogInformation(
                $"[HgInstaller] Preparing decompression (Virtual merge stream): {sourceDir} -> {destDir}");

            var partFiles = Directory.GetFiles(sourceDir)
                .Where(f => f.EndsWith(".zip.001", StringComparison.OrdinalIgnoreCase) ||
                            (Path.GetExtension(f).Length == 4 && char.IsDigit(Path.GetExtension(f)[1]) &&
                             f.Contains(".zip.")))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (partFiles.Count == 0)
            {
                var singleZip = Directory.GetFiles(sourceDir, "*.zip").FirstOrDefault();
                if (singleZip != null) partFiles.Add(singleZip);
            }

            if (partFiles.Count == 0)
                throw new FileNotFoundException("No archive found in Downloads folder");

            await Task.Run(async () =>
            {
                try
                {
                    using var multiStream = new MultiVolumeStream(partFiles);
                    using var archiveFile = new ArchiveFile(multiStream);
                    if (!string.IsNullOrEmpty(password))
                    {
                        archiveFile.SetArchivePassword(password);
                    }

                    var totalSize = archiveFile.Entries.Sum(x => (long)x.Size);
                    long currentRead = 0;

                    void ZipProgressAdapter(object? sender, ExtractProgressProp e)
                    {
                        if (token.IsCancellationRequested) return;
                        Interlocked.Add(ref currentRead, (long)e.Read);
                        progressCallback?.Invoke(Math.Min(currentRead, totalSize), totalSize);
                    }

                    archiveFile.ExtractProgress += ZipProgressAdapter;
                    try
                    {
                        await archiveFile.ExtractAsync(entry =>
                        {
                            var safeName = (entry.FileName ?? string.Empty).TrimStart('/', '\\');
                            return Path.Combine(destDir, safeName);
                        }, true, 1 << 20, token);
                    }
                    finally
                    {
                        archiveFile.ExtractProgress -= ZipProgressAdapter;
                    }

                    SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Decompression complete!");
                }
                catch (Exception ex)
                {
                    var message = string.IsNullOrEmpty(password)
                        ? "[HgInstaller] Decompression failed. The archive may be corrupted, or this update package may be encrypted but patch.cd_key was not returned by API."
                        : "[HgInstaller] Decompression failed. The archive may be corrupted, patch.cd_key may be wrong, or the cached preload package may not match the official update package.";

                    SharedStatic.InstanceLogger.LogError($"{message}\n{ex}");

                    throw new InvalidDataException(message, ex);
                }
            }, token);
        }
    }
}