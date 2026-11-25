using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Hypergryph.Core.Management.Api;
using Hi3Helper.Hypergryph.Core.Utils;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Microsoft.Extensions.Logging;
using SevenZipExtractor;
using SevenZipExtractor.Event;
using SharpHDiffPatch.Core;
using Hi3Helper.Hypergryph.Shared;

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
            using var log = PluginDiagnostics.BeginGameOperation(_owner.GameManager);
            var timer = Stopwatch.StartNew();
            var timings = new Dictionary<string, TimeSpan>();
            string phase = "prepare";
            TimeSpan phaseStart = TimeSpan.Zero;
            string outcome = "failed";

            void BeginPhase(string next)
            {
                TimeSpan elapsed = timer.Elapsed;
                timings[phase] = timings.GetValueOrDefault(phase) + elapsed - phaseStart;
                phaseStart = elapsed;
                phase = next;
            }

            try
            {
                await RunCoreAsync(progressDelegate, progressStateDelegate, token, BeginPhase).ConfigureAwait(false);
                outcome = "completed";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
            finally
            {
                BeginPhase("end");
                var summary = string.Join(", ", timings.Select(item => $"{item.Key}={item.Value.TotalSeconds:F2}s"));
                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] Update {Outcome} in {Seconds:F2}s ({Phases})", outcome, timer.Elapsed.TotalSeconds, summary);
            }
        }

        private async Task RunCoreAsync(InstallProgressDelegate? progressDelegate,
            InstallProgressStateDelegate? progressStateDelegate, CancellationToken token, Action<string> beginPhase)
        {
            if (_owner.GameManager is not HgGameManager manager)
                throw new InvalidOperationException("GameManager is not HgGameManager.");

            manager.GetGamePath(out var installPath);
            if (string.IsNullOrEmpty(installPath)) throw new InvalidOperationException("Install path is missing.");
            if (await manager.InitAsyncInner(true, token, expectedGamePath: installPath).ConfigureAwait(false) != 0)
                throw new InvalidOperationException("Cannot refresh Hypergryph package information.");
            using var log = PluginDiagnostics.BeginGameOperation(installPath);
            var package = manager.CaptureInstallPackage(installPath);

            var downloadDir = GetDownloadDirectory(installPath);
            Directory.CreateDirectory(downloadDir);

            InstallProgress progress = default;

            void Report(InstallProgressState state)
            {
                progressDelegate?.Invoke(in progress);
                progressStateDelegate?.Invoke(state);
            }

            void ResetSingleStageProgress()
            {
                progress.TotalCountToDownload = 1;
                progress.DownloadedCount = 0;

                progress.TotalStateToComplete = 1;
                progress.StateCount = 0;

                progress.TotalBytesToDownload = 0;
                progress.DownloadedBytes = 0;
            }

            void CompleteSingleStageProgress()
            {
                progress.DownloadedCount = progress.TotalCountToDownload;
                progress.StateCount = progress.TotalStateToComplete;

                if (progress.TotalBytesToDownload > 0)
                    progress.DownloadedBytes = progress.TotalBytesToDownload;
            }

            var lastDownloadReportTicks = DateTime.UtcNow.Ticks;
            var reportIntervalTicks = TimeSpan.FromMilliseconds(300).Ticks;

            void ReportDownloadThrottled(InstallProgressState state)
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                var oldTicks = Interlocked.Read(ref lastDownloadReportTicks);

                if (nowTicks - oldTicks >= reportIntervalTicks &&
                    Interlocked.CompareExchange(
                        ref lastDownloadReportTicks,
                        nowTicks,
                        oldTicks) == oldTicks)
                    Report(state);
            }

            Report(InstallProgressState.Preparing);
            SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Verifying existing packages...");

            var packsToDownload = new ConcurrentBag<HgPack>();
            long totalBytesToDownload = 0;
            long alreadyDownloadedBytes = 0;

            foreach (var pack in package.Packs)
                if (long.TryParse(pack.PackageSize, out var s))
                    totalBytesToDownload += s;

            progress.TotalCountToDownload = package.Packs.Count;
            progress.DownloadedCount = 0;
            progress.TotalBytesToDownload = totalBytesToDownload;
            progress.DownloadedBytes = 0;
            progress.TotalStateToComplete = package.Packs.Count;
            progress.StateCount = 0;
            Report(InstallProgressState.Verify);

            await Parallel.ForEachAsync(package.Packs,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (pack, innerToken) =>
                {
                    if (string.IsNullOrEmpty(pack.Url)) return;
                    long.TryParse(pack.PackageSize, out var size);

                    var fileName = Path.GetFileName(new Uri(pack.Url!).LocalPath);
                    var filePath = InstallPath.Resolve(downloadDir, fileName);
                    var tempPath = InstallPath.Resolve(downloadDir, fileName + ".tmp");

                    var needsDownload = true;

                    if (File.Exists(filePath))
                    {
                        var fi = new FileInfo(filePath);
                        if (fi.Length == size)
                        {
                            var isMatch = string.IsNullOrEmpty(pack.Md5) ||
                                          await HgFileDownloader.CheckMd5Async(filePath, pack.Md5, innerToken);
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
            progress.TotalCountToDownload = package.Packs.Count;
            progress.DownloadedCount = package.Packs.Count - downloadTasks.Count;
            progress.TotalBytesToDownload = totalBytesToDownload;
            progress.DownloadedBytes = alreadyDownloadedBytes;
            progress.TotalStateToComplete = package.Packs.Count;
            progress.StateCount = package.Packs.Count - downloadTasks.Count;

            beginPhase("download");
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
                    var filePath = InstallPath.Resolve(downloadDir, fileName);
                    var tempPath = InstallPath.Resolve(downloadDir, fileName + ".tmp");
                    var expectedSize = long.Parse(pack.PackageSize ?? "0");

                    await DownloadFileAsync(pack.Url!, tempPath, expectedSize, innerToken, delta =>
                    {
                        Interlocked.Add(ref progress.DownloadedBytes, delta);
                        ReportDownloadThrottled(InstallProgressState.Download);
                    }).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(pack.Md5))
                    {
                        var isMatch = await HgFileDownloader.CheckMd5Async(tempPath, pack.Md5, innerToken);
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

                    File.Move(tempPath, filePath, true);

                    Interlocked.Increment(ref progress.DownloadedCount);
                    Interlocked.Increment(ref progress.StateCount);
                    Report(InstallProgressState.Download);
                });
            }

            if (package.IsDelta)
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] Delta update mechanism confirmed. Initializing sandbox extraction...");

                await EnsureHpatchzAsync(installPath, token).ConfigureAwait(false);
                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] hpatchz.exe is ready.");

                var tempExtractDir = Hi3Helper.Hypergryph.Shared.PluginPaths.In(installPath, "delta-temp");
                var stageDir = Hi3Helper.Hypergryph.Shared.PluginPaths.In(installPath, "apply-stage");
                // DEBUG
                var skipExtractForDebug = false;

                if (!skipExtractForDebug)
                {
                    ResetSingleStageProgress();
                    Report(InstallProgressState.Updating);

                    if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true);
                    if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);

                    Directory.CreateDirectory(tempExtractDir);
                    Directory.CreateDirectory(stageDir);

                    beginPhase("extract");
                    await ExtractPackagesAsync(
                        downloadDir,
                        tempExtractDir,
                        package.PatchCdKey,
                        token,
                        (extractedBytes, totalBytes) =>
                        {
                            progress.DownloadedBytes = extractedBytes;
                            progress.TotalBytesToDownload = totalBytes;
                            Report(InstallProgressState.Updating);
                        });

                    CompleteSingleStageProgress();
                    Report(InstallProgressState.Updating);
                }
                else
                {
                    Directory.CreateDirectory(stageDir);
                }

                Report(InstallProgressState.Updating);
                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] Copying static update files to staging directory...");

                var staticFiles = Directory.GetFiles(tempExtractDir, "*.*", SearchOption.AllDirectories)
                    .Where(newPath =>
                    {
                        var relPath = Path.GetRelativePath(tempExtractDir, newPath);

                        return !relPath.StartsWith("vfs_files", StringComparison.OrdinalIgnoreCase) &&
                               !relPath.StartsWith("diff_", StringComparison.OrdinalIgnoreCase) &&
                               !relPath.Equals("patch.json", StringComparison.OrdinalIgnoreCase) &&
                               !relPath.Equals("delete_files.txt", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                progress.TotalCountToDownload = staticFiles.Count;
                progress.DownloadedCount = 0;
                progress.TotalStateToComplete = staticFiles.Count;
                progress.StateCount = 0;
                progress.TotalBytesToDownload = staticFiles.Sum(x => new FileInfo(x).Length);
                progress.DownloadedBytes = 0;

                Report(InstallProgressState.Updating);

                foreach (var newPath in staticFiles)
                {
                    var relPath = Path.GetRelativePath(tempExtractDir, newPath);

                    if (relPath.StartsWith("vfs_files", StringComparison.OrdinalIgnoreCase) ||
                        relPath.StartsWith("diff_", StringComparison.OrdinalIgnoreCase) ||
                        relPath.Equals("patch.json", StringComparison.OrdinalIgnoreCase) ||
                        relPath.Equals("delete_files.txt", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fileSize = new FileInfo(newPath).Length;
                    if (relPath.Equals("config.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(newPath, Path.Combine(stageDir, "config.ini.new"), true);
                        progress.DownloadedBytes += fileSize;
                        progress.DownloadedCount++;
                        progress.StateCount++;
                        Report(InstallProgressState.Updating);
                        continue;
                    }

                    var destPath = InstallPath.Resolve(stageDir, relPath);
                    var destDir = Path.GetDirectoryName(destPath)!;
                    if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                    ForceDeleteFile(destPath);
                    File.Copy(newPath, destPath, true);
                    progress.DownloadedBytes += fileSize;
                    progress.DownloadedCount++;
                    progress.StateCount++;
                    Report(InstallProgressState.Updating);
                }

                ResetSingleStageProgress();
                Report(InstallProgressState.Updating);

                beginPhase("patch");
                await ApplyDeltaPatchAsync(tempExtractDir, installPath, stageDir, package.PatchManifestUrl, token,
                    (patchedBytes, totalBytes) =>
                    {
                        progress.DownloadedBytes = patchedBytes;
                        progress.TotalBytesToDownload = totalBytes;
                        Report(InstallProgressState.Updating);
                    });

                CompleteSingleStageProgress();
                Report(InstallProgressState.Updating);

                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] Applying staged files to game directory...");

                var stagedFiles = Directory.GetFiles(
                    stageDir,
                    "*",
                    SearchOption.AllDirectories);

                var stagedFileSet = stagedFiles
                    .Select(file => Path.GetRelativePath(stageDir, file)
                        .Replace("\\", "/"))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var stagedPlan = stagedFiles.Select(file =>
                {
                    var relativePath = Path.GetRelativePath(stageDir, file);
                    var source = InstallPath.Resolve(stageDir, relativePath);
                    InstallPath.Resolve(installPath, relativePath);
                    return (Source: source, RelativePath: relativePath);
                }).ToArray();

                var deleteListPath = Path.Combine(tempExtractDir, "delete_files.txt");
                InstallPath.RejectLink(deleteListPath);
                var deletePlan = File.Exists(deleteListPath)
                    ? File.ReadAllLines(deleteListPath)
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .Select(line => line.Trim().Replace('\\', '/'))
                        .Where(path => !path.Equals("config.ini", StringComparison.OrdinalIgnoreCase))
                        .Where(path => !stagedFileSet.Contains(path))
                        .Select(path => (RelativePath: path, Target: InstallPath.Resolve(installPath, path)))
                        .ToArray()
                    : [];

                InstallPath.Resolve(installPath, "config.ini");
                progress.TotalCountToDownload = stagedFiles.Length;
                progress.DownloadedCount = 0;
                progress.TotalStateToComplete = stagedFiles.Length;
                progress.StateCount = 0;
                progress.TotalBytesToDownload = stagedFiles.Sum(x => new FileInfo(x).Length);
                progress.DownloadedBytes = 0;

                Report(InstallProgressState.Updating);

                beginPhase("commit");
                CommitStageDirectory(
                    stagedPlan,
                    installPath,
                    token,
                    (committedBytes, committedCount) =>
                    {
                        progress.DownloadedBytes = committedBytes;
                        progress.DownloadedCount = committedCount;
                        progress.StateCount = committedCount;
                        Report(InstallProgressState.Updating);
                    });

                Report(InstallProgressState.Removing);
                SharedStatic.InstanceLogger.LogInformation(
                    "[HgInstaller] Cleaning up legacy files after staged apply...");

                foreach (var entry in deletePlan)
                {
                    token.ThrowIfCancellationRequested();
                    InstallPath.Resolve(installPath, entry.RelativePath);
                    ForceDeleteFile(entry.Target);
                }

                // 调试模式下保留沙盒目录，非调试模式下执行清理
                if (!skipExtractForDebug)
                {
                    try
                    {
                        Directory.Delete(tempExtractDir, true);
                    }
                    catch (Exception ex)
                    {
                        SharedStatic.InstanceLogger.LogDebug(
                            $"[HgCore] Temp dir cleanup failed: {ex.Message}");
                    }

                    try
                    {
                        Directory.Delete(stageDir, true);
                    }
                    catch (Exception ex)
                    {
                        SharedStatic.InstanceLogger.LogDebug(
                            $"[HgCore] Stage dir cleanup failed: {ex.Message}");
                    }
                }
            }
            else
            {
                ResetSingleStageProgress();
                Report(InstallProgressState.Install);
                SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Full update mechanism confirmed.");
                var fullStage = PluginPaths.In(installPath, $"apply-stage-{Guid.NewGuid():N}");
                Directory.CreateDirectory(fullStage);
                try
                {
                    beginPhase("extract");
                    await ExtractPackagesAsync(downloadDir, fullStage, null, token, (extractedBytes, totalBytes) =>
                    {
                        progress.DownloadedBytes = extractedBytes;
                        progress.TotalBytesToDownload = totalBytes;
                        Report(InstallProgressState.Install);
                    });
                    var metadata = InstallPath.Resolve(fullStage, "config.ini");
                    if (File.Exists(metadata))
                        File.Move(metadata, InstallPath.Resolve(fullStage, "config.ini.new"), true);
                    InstallPath.Resolve(installPath, "config.ini");
                    var fullPlan = Directory.GetFiles(fullStage, "*", SearchOption.AllDirectories).Select(file =>
                    {
                        var path = Path.GetRelativePath(fullStage, file);
                        InstallPath.Resolve(installPath, path);
                        return (Source: InstallPath.Resolve(fullStage, path), RelativePath: path);
                    }).ToArray();
                    beginPhase("commit");
                    CommitStageDirectory(fullPlan, installPath, token);
                }
                finally { Directory.Delete(fullStage, true); }

                CompleteSingleStageProgress();
                Report(InstallProgressState.Install);
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
                var resourceUrl = package.ResourceUrl;
                if (string.IsNullOrEmpty(resourceUrl) &&
                    await manager.InitAsyncInner(true, token, requestFullPackage: true, expectedGamePath: installPath).ConfigureAwait(false) == 0)
                    resourceUrl = manager.GetFullResourceBaseUrl(installPath);
                var repairer = new HgGameRepairer(_owner._downloadHttpClient, resourceUrl, installPath);
                beginPhase("verify/repair");
                await repairer.StartRepairAsync(progressDelegate, progressStateDelegate, token);
                var newConfigPath = Path.Combine(installPath, "config.ini.new");
                var targetConfigPath = InstallPath.Resolve(installPath, "config.ini");
                if (File.Exists(newConfigPath))
                {
                    InstallPath.RejectLink(newConfigPath);
                    InstallPath.ReplaceVerifiedFile(newConfigPath, targetConfigPath);
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

        private async Task ApplyDeltaPatchAsync(string tempExtractDir, string targetGameRoot, string stageRoot,
            string? patchJsonUrl, CancellationToken token, Action<long, long>? progressCallback = null)
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
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
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

            var vfsRelativePath = (manifest.VfsBasePath ?? "Hg_Data/StreamingAssets/VFS")
                .Replace("/", "\\");

            string vfsPrefix = vfsRelativePath.Length == 0 ? string.Empty : vfsRelativePath + "/";
            if (vfsPrefix.Length != 0)
            {
                InstallPath.Resolve(targetGameRoot, vfsRelativePath);
                InstallPath.Resolve(stageRoot, vfsRelativePath);
            }
            foreach (var entry in manifest.Files)
            {
                if (!string.IsNullOrEmpty(entry.Name))
                    InstallPath.Resolve(stageRoot, vfsPrefix + entry.Name);
                if (!string.IsNullOrEmpty(entry.LocalPath))
                    InstallPath.Resolve(tempExtractDir, entry.LocalPath);
                if (entry.Patches is { Count: > 0 })
                {
                    var patch = entry.Patches[0];
                    if (!string.IsNullOrEmpty(patch.BaseFile))
                        InstallPath.Resolve(targetGameRoot, vfsPrefix + patch.BaseFile);
                    if (!string.IsNullOrEmpty(patch.PatchPath))
                        InstallPath.Resolve(tempExtractDir, patch.PatchPath);
                }
            }

            var totalPatchSize = manifest.Files
                .Where(f => !string.IsNullOrEmpty(f.LocalPath) ||
                            (f.Patches != null && f.Patches.Count > 0))
                .Sum(f => f.Size);
            long currentPatchedSize = 0;

            SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Building temporary extraction file map...");
            var extractFileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                var allExtractedFiles = Directory.GetFiles(tempExtractDir, "*", SearchOption.AllDirectories);
                foreach (var file in allExtractedFiles) extractFileMap[Path.GetFileName(file)] = file;
            }, token);

            var copyCount = 0;
            var patchCount = 0;
            var verifyOnlyCount = 0;
            var skippedNodes = new ConcurrentQueue<string>();

            SharedStatic.InstanceLogger.LogInformation("[HgInstaller] Starting VFS delta patch pipeline...");

            var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(node.Name)) continue;
                ReserveOutput(node.Name);
                if (string.IsNullOrEmpty(node.LocalPath) && node.Patches is { Count: > 0 })
                    ReserveOutput(node.Name + ".tmp");
            }

            void ReserveOutput(string name)
            {
                if (!outputs.Add(InstallPath.Resolve(stageRoot, vfsPrefix + name)))
                    throw new InvalidDataException($"Conflicting patch output: {name}");
            }

            foreach (string output in outputs)
                for (string? parent = Path.GetDirectoryName(output); parent != null; parent = Path.GetDirectoryName(parent))
                    if (outputs.Contains(parent))
                        throw new InvalidDataException($"Patch output is also a parent directory: {parent}");

            var progressGate = new object();
            var activeOutputBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            progressCallback?.Invoke(0, totalPatchSize);

            void ReportCompleted(long size, string? output = null)
            {
                lock (progressGate)
                {
                    if (output != null) activeOutputBytes.Remove(output);
                    currentPatchedSize += size;
                    progressCallback?.Invoke(currentPatchedSize + activeOutputBytes.Values.Sum(), totalPatchSize);
                }
            }

            void ReportOutput(string output, long bytes, long size)
            {
                lock (progressGate)
                {
                    long observed = Math.Clamp(bytes, 0L, Math.Max(0L, size - 1));
                    if (observed <= activeOutputBytes.GetValueOrDefault(output)) return;
                    activeOutputBytes[output] = observed;
                    progressCallback?.Invoke(currentPatchedSize + activeOutputBytes.Values.Sum(), totalPatchSize);
                }
            }

            int workers = Environment.ProcessorCount >= 4 ? 2 : 1;
            await Parallel.ForEachAsync(manifest.Files, new ParallelOptions
            {
                MaxDegreeOfParallelism = workers,
                CancellationToken = token
            }, async (fileNode, patchToken) =>
            {
                patchToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(fileNode.Name))
                {
                    skippedNodes.Enqueue("<empty name>");
                    return;
                }

                var targetFilePath = InstallPath.Resolve(stageRoot, vfsPrefix + fileNode.Name);

                var targetDir = Path.GetDirectoryName(targetFilePath)!;
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                if (!string.IsNullOrEmpty(fileNode.LocalPath))
                {
                    var sourceExtractedFile = InstallPath.Resolve(tempExtractDir, fileNode.LocalPath);

                    if (!File.Exists(sourceExtractedFile) &&
                        extractFileMap.TryGetValue(
                            Path.GetFileName(fileNode.LocalPath),
                            out var foundPath))
                        sourceExtractedFile = foundPath;

                    if (!File.Exists(sourceExtractedFile))
                    {
                        skippedNodes.Enqueue(
                            $"{fileNode.Name} | local_path missing: {fileNode.LocalPath}");
                        return;
                    }

                    ForceDeleteFile(targetFilePath);
                    File.Copy(sourceExtractedFile, targetFilePath, true);

                    if (!string.IsNullOrEmpty(fileNode.Md5))
                    {
                        var isTargetMatch = await HgFileDownloader.CheckMd5Async(
                            targetFilePath,
                            fileNode.Md5,
                            patchToken).ConfigureAwait(false);

                        if (!isTargetMatch)
                        {
                            skippedNodes.Enqueue(
                                $"{fileNode.Name} | copied file md5 mismatch");

                            return;
                        }
                    }

                    ReportCompleted(fileNode.Size);

                    SharedStatic.InstanceLogger.LogDebug(
                        $"[HgInstaller] [Copy] {fileNode.Name}");

                    Interlocked.Increment(ref copyCount);
                }
                else if (fileNode.Patches != null && fileNode.Patches.Count > 0)
                {
                    var patchInfo = fileNode.Patches[0];

                    if (string.IsNullOrEmpty(patchInfo.BaseFile) ||
                        string.IsNullOrEmpty(patchInfo.PatchPath))
                    {
                        skippedNodes.Enqueue(
                            $"{fileNode.Name} | invalid patch node");
                        return;
                    }

                    var baseFilePath = InstallPath.Resolve(targetGameRoot, vfsPrefix + patchInfo.BaseFile);

                    var diffFilePath = InstallPath.Resolve(tempExtractDir, patchInfo.PatchPath);

                    if (!File.Exists(diffFilePath) &&
                        extractFileMap.TryGetValue(
                            Path.GetFileName(patchInfo.PatchPath),
                            out var foundDiffPath))
                        diffFilePath = foundDiffPath;

                    if (!File.Exists(baseFilePath))
                    {
                        skippedNodes.Enqueue(
                            $"{fileNode.Name} | base file missing: {patchInfo.BaseFile}");
                        return;
                    }

                    if (!File.Exists(diffFilePath))
                    {
                        skippedNodes.Enqueue(
                            $"{fileNode.Name} | diff file missing: {patchInfo.PatchPath}");
                        return;
                    }

                    if (!string.IsNullOrEmpty(patchInfo.BaseMd5))
                    {
                        var isBaseMatch = await HgFileDownloader.CheckMd5Async(
                            baseFilePath,
                            patchInfo.BaseMd5,
                            patchToken).ConfigureAwait(false);

                        if (!isBaseMatch)
                        {
                            throw new InvalidDataException(
                                $"[HgInstaller] Base file MD5 mismatch: {patchInfo.BaseFile} -> target {fileNode.Name}");
                        }
                    }

                    if (new FileInfo(diffFilePath).Length == 0)
                    {
                        if (!string.Equals(
                                baseFilePath,
                                targetFilePath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            ForceDeleteFile(targetFilePath);
                            File.Copy(baseFilePath, targetFilePath, true);
                        }

                        if (!string.IsNullOrEmpty(fileNode.Md5))
                        {
                            var isTargetMatch = await HgFileDownloader.CheckMd5Async(
                                targetFilePath,
                                fileNode.Md5,
                                patchToken).ConfigureAwait(false);

                            if (!isTargetMatch)
                            {
                                skippedNodes.Enqueue(
                                    $"{fileNode.Name} | empty patch target md5 mismatch");

                                return;
                            }
                        }

                        ReportCompleted(fileNode.Size);

                        SharedStatic.InstanceLogger.LogDebug(
                            $"[HgInstaller] [Skip Empty Patch] {fileNode.Name}");

                        Interlocked.Increment(ref patchCount);
                    }
                    else
                    {
                        var tempOutPath = InstallPath.Resolve(stageRoot, vfsPrefix + fileNode.Name + ".tmp");

                        try
                        {
                            var header = HDiffPatch.GetHDiffHeaderInfo(diffFilePath);

                            SharedStatic.InstanceLogger.LogDebug(
                                $"[HgInstaller] HDiff header: oldSize={header.headerInfo.oldDataSize}, newSize={header.headerInfo.newDataSize}, " +
                                $"baseSize={new FileInfo(baseFilePath).Length}, targetSize={fileNode.Size}, diff={diffFilePath}");

                            SharedStatic.InstanceLogger.LogDebug(
                                $"[HgInstaller] HDiff header detail: magic={header.headerInfo.headerMagic}, " +
                                $"comp={header.headerInfo.compMode}, checksum={header.headerInfo.checksumMode}, " +
                                $"singleCompressed={header.headerInfo.isSingleCompressedDiff}, " +
                                $"isInputDir={header.headerInfo.isInputDir}, isOutputDir={header.headerInfo.isOutputDir}");

                            ForceDeleteFile(tempOutPath);

                            await RunHpatchzAsync(
                                targetGameRoot,
                                baseFilePath,
                                diffFilePath,
                                tempOutPath,
                                patchToken,
                                bytes => ReportOutput(tempOutPath, bytes, fileNode.Size)).ConfigureAwait(false);

                            if (!string.IsNullOrEmpty(fileNode.Md5))
                            {
                                var isTargetMatch = await HgFileDownloader.CheckMd5Async(
                                    tempOutPath,
                                    fileNode.Md5,
                                    patchToken).ConfigureAwait(false);

                                if (!isTargetMatch)
                                {
                                    ForceDeleteFile(tempOutPath);

                                    throw new InvalidDataException(
                                        $"[HgInstaller] Patched file MD5 mismatch: {fileNode.Name}");
                                }
                            }

                            ForceDeleteFile(targetFilePath);
                            File.Move(tempOutPath, targetFilePath, true);
                            ReportCompleted(fileNode.Size, tempOutPath);

                            SharedStatic.InstanceLogger.LogDebug(
                                $"[HgInstaller] [Patch] {fileNode.Name}");

                            Interlocked.Increment(ref patchCount);
                        }
                        catch (Exception ex)
                        {
                            SharedStatic.InstanceLogger.LogError(
                                $"[HgInstaller] Delta patch failed for {fileNode.Name}. Error: {ex.Message}");

                            if (File.Exists(tempOutPath))
                                ForceDeleteFile(tempOutPath);

                            throw;
                        }
                    }
                }
                else
                {
                    SharedStatic.InstanceLogger.LogDebug(
                        $"[HgInstaller] [VerifyOnly] {fileNode.Name}");

                    Interlocked.Increment(ref verifyOnlyCount);
                }
            }).ConfigureAwait(false);

            if (skippedNodes.Count > 0)
            {
                var preview = string.Join(
                    Environment.NewLine,
                    skippedNodes.Take(20));

                SharedStatic.InstanceLogger.LogError(
                    $"[HgInstaller] Delta patch skipped {skippedNodes.Count} files. First entries:{Environment.NewLine}{preview}");

                throw new InvalidDataException(
                    $"[HgInstaller] Delta patch incomplete. {skippedNodes.Count} files were skipped. " +
                    "The cached preload package may not match the official patch, or patch.json path mapping is not supported correctly.");
            }

            SharedStatic.InstanceLogger.LogInformation(
                $"[HgInstaller] VFS delta patch pipeline executed successfully. " +
                $"Workers={workers}, Copy={copyCount}, Patch={patchCount}, VerifyOnly={verifyOnlyCount}, Total={manifest.Files.Count}");
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

        private static HashSet<string> BuildStageRelativeFileSet(string stageDir)
        {
            return Directory.GetFiles(stageDir, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(stageDir, file)
                    .Replace("\\", "/"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static void CommitStageDirectory(
            (string Source, string RelativePath)[] stagedFiles,
            string targetRoot,
            CancellationToken token,
            Action<long, int>? progressCallback = null)
        {
            long committedBytes = 0;
            var committedCount = 0;

            foreach (var stagedFile in stagedFiles)
            {
                token.ThrowIfCancellationRequested();
                var fileSize = new FileInfo(stagedFile.Source).Length;
                var targetPath = InstallPath.Resolve(targetRoot, stagedFile.RelativePath);
                var targetDir = Path.GetDirectoryName(targetPath)!;
                Directory.CreateDirectory(targetDir);

                InstallPath.RejectLink(stagedFile.Source);
                InstallPath.Resolve(targetRoot, stagedFile.RelativePath);
                InstallPath.ReplaceVerifiedFile(stagedFile.Source, targetPath, sameVolumeOnly: true);

                committedBytes += fileSize;
                committedCount++;

                progressCallback?.Invoke(committedBytes, committedCount);
            }
        }

        private static async Task<string> EnsureHpatchzAsync(
            string targetGameRoot,
            CancellationToken token)
        {
            var toolsDir = Hi3Helper.Hypergryph.Shared.PluginPaths.In(targetGameRoot, "tools");
            Directory.CreateDirectory(toolsDir);

            var hpatchzPath = Path.Combine(toolsDir, "hpatchz.exe");

            if (File.Exists(hpatchzPath) && new FileInfo(hpatchzPath).Length > 0)
                return hpatchzPath;

            var resourceNames = new List<string>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string assemblyName;

                try
                {
                    assemblyName = assembly.GetName().Name ?? "<unknown>";
                }
                catch
                {
                    assemblyName = "<unknown>";
                }

                string[] names;

                try
                {
                    names = assembly.GetManifestResourceNames();
                }
                catch
                {
                    continue;
                }

                foreach (var name in names)
                {
                    resourceNames.Add($"{assemblyName}: {name}");

                    if (!name.Equals("hpatchz.exe", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith(".hpatchz.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    await using var resourceStream = assembly.GetManifestResourceStream(name);

                    if (resourceStream == null)
                        continue;

                    await using var fileStream = new FileStream(
                        hpatchzPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 1024,
                        true);

                    await resourceStream.CopyToAsync(fileStream, token).ConfigureAwait(false);
                    await fileStream.FlushAsync(token).ConfigureAwait(false);

                    SharedStatic.InstanceLogger.LogInformation(
                        $"[HgInstaller] Extracted embedded hpatchz.exe from resource: {assemblyName}:{name}");

                    return hpatchzPath;
                }
            }

            var availableResources = resourceNames.Count == 0
                ? "<no manifest resources found>"
                : string.Join(Environment.NewLine, resourceNames);

            throw new FileNotFoundException(
                "[HgInstaller] Embedded hpatchz.exe resource was not found. Available resources:" +
                Environment.NewLine +
                availableResources);
        }


        private static async Task RunHpatchzAsync(
            string targetGameRoot,
            string oldFile,
            string diffFile,
            string outputFile,
            CancellationToken token,
            Action<long> reportOutput)
        {
            var hpatchzPath = await EnsureHpatchzAsync(
                targetGameRoot,
                token).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = hpatchzPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(hpatchzPath)
            };

            psi.ArgumentList.Add(oldFile);
            psi.ArgumentList.Add(diffFile);
            psi.ArgumentList.Add(outputFile);

            using var process = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);

            try
            {
                var exit = process.WaitForExitAsync(token);
                while (await Task.WhenAny(exit, Task.Delay(500, token)).ConfigureAwait(false) != exit)
                {
                    token.ThrowIfCancellationRequested();
                    var output = new FileInfo(outputFile);
                    if (output.Exists) reportOutput(output.Length);
                }
                await exit.ConfigureAwait(false);
                var finishedOutput = new FileInfo(outputFile);
                if (process.ExitCode == 0 && finishedOutput.Exists) reportOutput(finishedOutput.Length);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"[HgInstaller] hpatchz failed. ExitCode={process.ExitCode}" +
                    Environment.NewLine +
                    "stdout:" +
                    Environment.NewLine +
                    stdout +
                    Environment.NewLine +
                    "stderr:" +
                    Environment.NewLine +
                    stderr);
            }

            SharedStatic.InstanceLogger.LogDebug(
                $"[HgInstaller] hpatchz completed. stdout:{Environment.NewLine}{stdout}");
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
                    if (!string.IsNullOrEmpty(password)) archiveFile.SetArchivePassword(password);

                    var entryPaths = archiveFile.Entries.ToDictionary(entry => entry, entry =>
                    {
                        var path = entry.FileName ?? string.Empty;
                        if (entry.IsFolder) path = path.TrimEnd('/', '\\');
                        return InstallPath.Resolve(destDir, path);
                    });
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
                        await archiveFile.ExtractAsync(entry => entryPaths[entry], true, 1 << 20, token);
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
