using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Hypergryph.Yostar.Api;
using Hi3Helper.Hypergryph.Yostar.Storage;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Yostar.Management;

[GeneratedComClass]
public partial class YostarGameManager : GameManagerBase
{

    private readonly YostarApiClient _apiClient;
    private readonly YostarLauncherOptions _options;

    public YostarGameManager(YostarLauncherOptions options)
    {
        _options = options;
        _apiClient = new YostarApiClient(options);
    }

    internal YostarLauncherOptions Options => _options;
    internal YostarApiClient ApiClient => _apiClient;

    protected override HttpClient ApiResponseHttpClient { get; set; } = new();

    internal bool IsManualVerifyRequested => Hi3Helper.Hypergryph.Shared.PluginConfiguration.For(this).HasVerificationRequest;

    protected override bool IsInstalled
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CurrentGameInstallPath)) return false;
            YostarLocalGameConfig? config = YostarLocalStorage.ReadGameConfig(CurrentGameInstallPath);
            if (config == null || !string.Equals(config.Tag, _options.GameTag, StringComparison.Ordinal)) return false;

            string executableName = YostarLocalStorage.NormalizeExecutableName(config.Name,
                _options.DefaultExecutableName);
            return File.Exists(Path.Combine(CurrentGameInstallPath, executableName));
        }
    }

    protected override bool HasUpdate => IsInstalled &&
                                         (IsManualVerifyRequested || CurrentGameVersion != ApiGameVersion);

    protected override bool HasPreload => false;

    internal async Task<YostarTargetPackage> RefreshTargetPackageAsync(CancellationToken token)
    {
        var package = await _apiClient.GetTargetPackageAsync(token).ConfigureAwait(false);
        ApplyApiVersion(package.Config.LatestVersion);
        return package;
    }

    internal async Task RefreshVersionAsync(CancellationToken token)
    {
        var config = await _apiClient.GetGameConfigAsync(token).ConfigureAwait(false);
        ApplyApiVersion(config.LatestVersion);
    }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        try
        {
            ReadLocalVersion();
            await RefreshVersionAsync(token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(ex, "[Yostar] Failed to initialize game manager.");
            return -1;
        }
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
    {
        CurrentGameVersion = gameVersion;
    }

    protected override void SetGamePathInner(string gamePath)
    {
        Hi3Helper.Hypergryph.Shared.PluginDiagnostics.SetGamePath(gamePath);
        Hi3Helper.Hypergryph.Shared.PluginPaths.Migrate(gamePath);
        SharedStatic.InstanceLogger.LogInformation("[PluginLog] Game path set: {GamePath}", gamePath);
        CurrentGameInstallPath = gamePath;
        ReadLocalVersion();
    }

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "YostarGames", _options.GameDirectoryName);
        if (Directory.Exists(candidate) && YostarLocalStorage.ReadGameConfig(candidate) is { } config &&
            string.Equals(config.Tag, _options.GameTag, StringComparison.Ordinal))
            return Task.FromResult<string?>(candidate);

        return Task.FromResult<string?>(null);
    }

    public override void LoadConfig()
    {
        ReadLocalVersion();
        _ = IsManualVerifyRequested;
    }

    public override void SaveConfig()
    {
    }

    private void ReadLocalVersion()
    {
        CurrentGameVersion = GameVersion.Empty;
        if (string.IsNullOrWhiteSpace(CurrentGameInstallPath)) return;
        YostarLocalGameConfig? config = YostarLocalStorage.ReadGameConfig(CurrentGameInstallPath);
        if (config == null || !string.Equals(config.Tag, _options.GameTag, StringComparison.Ordinal)) return;
        if (GameVersion.TryParse(config.Version, out GameVersion version)) CurrentGameVersion = version;
    }

    private void ApplyApiVersion(string? versionString)
    {
        if (GameVersion.TryParse(versionString, out GameVersion version)) ApiGameVersion = version;
    }

    public override void Dispose()
    {
        _apiClient.Dispose();
        base.Dispose();
    }
}
