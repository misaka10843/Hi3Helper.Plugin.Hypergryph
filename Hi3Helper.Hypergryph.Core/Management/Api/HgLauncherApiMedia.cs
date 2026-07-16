using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Core.Management.Api;

[GeneratedComClass]
public partial class HgLauncherApiMedia : LauncherApiMediaBase
{
    private readonly string _appCode;
    private readonly string _channel;
    private readonly string _seq;
    private readonly string _subChannel;
    private readonly string _webApiUrl;

    private HgGetMainBgImageRsp? _bgResponse;

    public HgLauncherApiMedia(string webApiUrl, string appCode, string channel, string subChannel, string seq)
    {
        _webApiUrl = webApiUrl;
        _appCode = appCode;
        _channel = channel;
        _subChannel = subChannel;
        _seq = seq;
    }

    [field: AllowNull] [field: MaybeNull] protected override HttpClient ApiResponseHttpClient { get; set; } = new();

    [field: AllowNull]
    [field: MaybeNull]
    protected HttpClient ApiDownloadHttpClient
    {
        get => field ??= new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.None)
            .AllowCookies()
            .AllowRedirections()
            .AllowUntrustedCert()
            .Create();
        set;
    }

    protected override string ApiResponseBaseUrl => _webApiUrl;

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        var requestBody = new HgBatchRequest
        {
            Seq = _seq,
            ProxyReqs = new[]
            {
                new HgProxyRequest { Kind = "get_main_bg_image", GetMainBgImageReq = CreateCommonReq() }
            }.ToList()
        };

        try
        {
            var jsonRequest = JsonSerializer.Serialize(requestBody, HgApiContext.Default.HgBatchRequest);
            SharedStatic.InstanceLogger.LogDebug($"[HgMedia] Request Body:\n{jsonRequest}");

            using var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            using var response = await ApiResponseHttpClient.PostAsync(_webApiUrl, content, token);

            SharedStatic.InstanceLogger.LogDebug($"[HgMedia] API Response Code: {response.StatusCode}");
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync(token);
            SharedStatic.InstanceLogger.LogDebug($"[HgMedia] Response Body:\n{jsonResponse}");

            var rspBody = JsonSerializer.Deserialize(jsonResponse, HgApiContext.Default.HgBatchResponse);

            _bgResponse = rspBody?.ProxyRsps?.FirstOrDefault(x => x.Kind == "get_main_bg_image")?.GetMainBgImageRsp;
            SharedStatic.InstanceLogger.LogDebug(
                $"[HgMedia] Background URL: {_bgResponse?.MainBgImage?.Url}, Video: {_bgResponse?.MainBgImage?.VideoUrl}");
            return 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError($"[HgMedia] Failed to init media: {ex}");
            return -1;
        }
    }

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        var url = !string.IsNullOrEmpty(_bgResponse?.MainBgImage?.VideoUrl)
            ? _bgResponse?.MainBgImage?.VideoUrl
            : _bgResponse?.MainBgImage?.Url;

        SharedStatic.InstanceLogger.LogDebug(
            $"[HgMedia] Background image: {_bgResponse?.MainBgImage?.Url}, Background video: {_bgResponse?.MainBgImage?.VideoUrl}");

        if (string.IsNullOrEmpty(url))
        {
            handle = nint.Zero;
            count = 0;
            isDisposable = false;
            isAllocated = false;
            return;
        }

        var memory = PluginDisposableMemory<LauncherPathEntry>.Alloc();
        ref var entry = ref memory[0];

        entry.Write(url, Span<byte>.Empty);

        handle = memory.AsSafePointer();
        count = 1;
        isDisposable = true;
        isAllocated = true;
    }

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
    {
        result = LauncherBackgroundFlag.TypeIsImage;
        if (!string.IsNullOrEmpty(_bgResponse?.MainBgImage?.VideoUrl)) result |= LauncherBackgroundFlag.TypeIsVideo;
    }

    public override void GetLogoFlag(out LauncherBackgroundFlag result)
    {
        result = LauncherBackgroundFlag.None;
    }

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    public override void GetBackgroundSpriteFps(out float fps)
    {
        fps = 60f;
    }

    protected override async Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress,
        CancellationToken token)
    {
        SharedStatic.InstanceLogger.LogDebug($"[HgMedia] Downloading background: {fileUrl}");
        try
        {
            await base.DownloadAssetAsyncInner(ApiDownloadHttpClient, fileUrl, outputStream, fileChecksum,
                downloadProgress, token);
            SharedStatic.InstanceLogger.LogDebug("[HgMedia] Background download COMPLETED.");
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(
                $"[HgMedia] Background download FAILED: {fileUrl}\nException: {ex}");
        }
    }

    public override void Dispose()
    {
        if (IsDisposed) return;
        ApiResponseHttpClient?.Dispose();
        ApiDownloadHttpClient?.Dispose();
        base.Dispose();
    }

    private HgCommonReq CreateCommonReq()
    {
        return new HgCommonReq
        {
            AppCode = _appCode,
            Channel = _channel,
            SubChannel = _subChannel,
            Language = SharedStatic.PluginLocaleCode?.ToLower() ?? "en-us"
        };
    }
}