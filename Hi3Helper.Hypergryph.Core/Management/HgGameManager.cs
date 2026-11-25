using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Hypergryph.Core.Management.Api;
using Hi3Helper.Hypergryph.Core.Utils;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Core.Management;

[GeneratedComClass]
public partial class HgGameManager : GameManagerBase
{

    private readonly string _apiUrl;
    private readonly string _appCode;
    private readonly string _channel;
    private readonly string _launcherAppCode;
    private readonly string _seq;
    private readonly string _subChannel;
    private readonly string _webApiUrl;

    private volatile HgGetLatestGameRsp? _latestGameInfo;
    private readonly object _refreshGate = new();
    private string? _boundGamePath;
    private long _normalRefresh;
    private long _fullRefresh;
    private string? _fullResourceBaseUrl;

    public HgGameManager(string gameExecutableNameByPreset, string apiUrl, string webApiUrl, string appCode,
        string launcherAppCode, string channel, string subChannel, string seq)
    {
        CurrentGameExecutableByPreset = gameExecutableNameByPreset;
        _apiUrl = apiUrl;
        _webApiUrl = webApiUrl;
        _appCode = appCode;
        _launcherAppCode = launcherAppCode;
        _channel = channel;
        _subChannel = subChannel;
        _seq = seq;
    }

    internal string? GetFullResourceBaseUrl(string gamePath)
    {
        lock (_refreshGate)
        {
            RequireGamePath(gamePath);
            return _fullResourceBaseUrl;
        }
    }
    private bool IsInitialized { get; set; }

    private bool IsDeltaUpdate => IsInstalled && _latestGameInfo?.Patch?.Patches is { Count: > 0 };

    internal (List<HgPack> Packs, bool IsDelta, string? PatchManifestUrl, string? PatchCdKey, string? ResourceUrl)
        CaptureInstallPackage(string gamePath)
    {
        lock (_refreshGate)
        {
            RequireGamePath(gamePath);
            bool delta = IsDeltaUpdate;
            var patch = _latestGameInfo?.Patch;
            var package = _latestGameInfo?.Pkg;
            var packs = delta ? patch?.Patches : package?.Packs;
            if (packs is not { Count: > 0 })
                throw new InvalidOperationException("No download packs found in API response.");
            return (packs, delta, patch?.V2PatchInfoUrl, patch?.CdKey, package?.FilePath);
        }
    }

    private void RequireGamePath(string gamePath)
    {
        if (!string.Equals(gamePath, CurrentGameInstallPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game path changed while obtaining Hypergryph package information.");
    }

    private string CurrentGameExecutableByPreset { get; }

    protected override HttpClient ApiResponseHttpClient { get; set; } = new();

    protected override bool IsInstalled
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentGameInstallPath)) return false;

            var exePath = Path.Combine(CurrentGameInstallPath, CurrentGameExecutableByPreset);
            var configPath = Path.Combine(CurrentGameInstallPath, "config.ini");

            return File.Exists(exePath) && File.Exists(configPath);
        }
    }

    protected override bool HasUpdate
    {
        get
        {
            if (!IsInstalled) return false;

            return IsManualVerifyRequested || (ApiGameVersion != GameVersion.Empty && CurrentGameVersion != ApiGameVersion);
        }
    }

    protected override bool HasPreload => !IsManualVerifyRequested && IsInstalled && ApiPreloadGameVersion != GameVersion.Empty;

    protected override GameVersion ApiGameVersion { get; set; }
    protected override GameVersion ApiPreloadGameVersion { get; set; }

    internal bool IsManualVerifyRequested => Hi3Helper.Hypergryph.Shared.PluginConfiguration.For(this).HasVerificationRequest;

    internal List<HgPack>? GetGamePacks(GameInstallerKind kind)
    {
        lock (_refreshGate)
        {
            if (kind == GameInstallerKind.Preload) return _latestGameInfo?.PrePatch?.Patches;
            if (IsDeltaUpdate) return _latestGameInfo?.Patch?.Patches;
            return _latestGameInfo?.Pkg?.Packs;
        }
    }

    protected override void SetGamePathInner(string gamePath)
    {
        using var log = Hi3Helper.Hypergryph.Shared.PluginDiagnostics.BeginGameOperation(gamePath);
        Hi3Helper.Hypergryph.Shared.PluginPaths.Migrate(gamePath);
        SharedStatic.InstanceLogger.LogInformation("[PluginLog] Game path set: {GamePath}", gamePath);
        SharedStatic.InstanceLogger.LogInformation($"[HgCore] SetGamePathInner called! Input path: '{gamePath}'");
        lock (_refreshGate)
        {
            string path = string.IsNullOrEmpty(gamePath) ? string.Empty : Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
            CurrentGameInstallPath = path;
            string localVersion = ReadLocalVersion(path);
            CurrentGameVersion = GameVersion.TryParse(localVersion, out var version) ? version : GameVersion.Empty;

            if (string.Equals(_boundGamePath, path, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsInitialized && !string.IsNullOrEmpty(path)) _ = InitAsyncInner(true);
                return;
            }

            _boundGamePath = path;
            ++_normalRefresh;
            ++_fullRefresh;
            _latestGameInfo = null;
            _fullResourceBaseUrl = null;
            ApiGameVersion = GameVersion.Empty;
            ApiPreloadGameVersion = GameVersion.Empty;
            IsInitialized = false;
            if (!string.IsNullOrEmpty(path)) _ = InitAsyncInner(true);
        }
    }

    internal async Task<int> InitAsyncInner(bool forceInit = false, CancellationToken token = default,
        bool requestFullPackage = false, string? expectedGamePath = null)
    {
        string? gamePath;
        long refresh;
        lock (_refreshGate)
        {
            if (expectedGamePath != null) RequireGamePath(expectedGamePath);
            if (!forceInit && !requestFullPackage && IsInitialized) return 0;
            gamePath = CurrentGameInstallPath;
            refresh = requestFullPackage ? ++_fullRefresh : ++_normalRefresh;
        }

        using var log = Hi3Helper.Hypergryph.Shared.PluginDiagnostics.BeginGameOperation(gamePath);
        string requestVersion = requestFullPackage ? "" : ReadLocalVersion(gamePath);

        var requestBody = new HgBatchRequest
        {
            Seq = _seq,
            ProxyReqs = new List<HgProxyRequest>
            {
                new()
                {
                    Kind = "get_latest_game",
                    GetLatestGameReq = new HgGetLatestGameReq
                    {
                        AppCode = _appCode,
                        LauncherAppCode = _launcherAppCode,
                        Channel = _channel,
                        SubChannel = _subChannel,
                        Version = requestFullPackage ? "" : requestVersion
                    }
                }
            }
        };

        try
        {
            using var response = await ApiResponseHttpClient!.PostAsJsonAsync(_apiUrl, requestBody,
                HgApiContext.Default.HgBatchRequest, token);
            response.EnsureSuccessStatusCode();

            var responseBody =
                await response.Content.ReadFromJsonAsync(HgApiContext.Default.HgBatchResponse, token);
            var latest = responseBody?.ProxyRsps?.FirstOrDefault(x => x.Kind == "get_latest_game")
                ?.GetLatestGameRsp;
            if (latest == null)
            {
                SharedStatic.InstanceLogger.LogError("[HgCore] API data error: get_latest_game_rsp is null.");
                return -1;
            }

            lock (_refreshGate)
            {
                if (refresh != (requestFullPackage ? _fullRefresh : _normalRefresh) ||
                    !string.Equals(gamePath, CurrentGameInstallPath, StringComparison.OrdinalIgnoreCase))
                    return -1;

                if (requestFullPackage)
                {
                    _fullResourceBaseUrl = latest.Pkg?.FilePath;
                }
                else
                {
                    CurrentGameVersion = GameVersion.TryParse(requestVersion, out var version) ? version : GameVersion.Empty;
                    ApiGameVersion = string.IsNullOrEmpty(latest.Version) ? GameVersion.Empty : new GameVersion(latest.Version);
                    ApiPreloadGameVersion = string.IsNullOrEmpty(latest.PrePatch?.Version)
                        ? GameVersion.Empty : new GameVersion(latest.PrePatch.Version);
                    _latestGameInfo = latest;
                    IsInitialized = true;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError($"[HgCore] API request failed: {ex}");
            return -1;
        }

        return 0;
    }

    private string ReadLocalVersion(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || !File.Exists(Path.Combine(gamePath, CurrentGameExecutableByPreset)))
            return "";
        try
        {
            string configPath = Path.Combine(gamePath, "config.ini");
            return File.Exists(configPath) ? ConfigTool.ParseVersion(ConfigTool.ReadConfig(configPath)) ?? "" : "";
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(ex, "[HgCore] Failed to read the local version.");
            return "";
        }
    }

    protected override Task<int> InitAsync(CancellationToken token)
    {
        return InitAsyncInner(true, token);
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
    {
        CurrentGameVersion = gameVersion;
    }

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token)
    {
        return Task.FromResult<string?>(null);
    }

    public override void LoadConfig()
    {
        _ = IsManualVerifyRequested;
    }

    public override void SaveConfig()
    {
    }

    public override void Dispose()
    {
        base.Dispose();
        ApiResponseHttpClient?.Dispose();
    }
}
