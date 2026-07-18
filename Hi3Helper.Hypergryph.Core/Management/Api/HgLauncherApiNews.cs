using System;
using System.Collections.Generic;
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
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Core.Management.Api;

[GeneratedComClass]
public partial class HgLauncherApiNews : LauncherApiNewsBase
{
    private readonly string _appCode;
    private readonly string _channel;
    private readonly string _seq;
    private readonly string _subChannel;
    private readonly string _webApiUrl;

    private HgGetBannerRsp? _bannerResponse;
    private HgGetAnnouncementRsp? _newsResponse;
    private HgGetSidebarRsp? _sidebarResponse;

    public HgLauncherApiNews(string webApiUrl, string appCode, string channel, string subChannel, string seq)
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
            .SetAllowedDecompression(DecompressionMethods.GZip)
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
                new HgProxyRequest { Kind = "get_announcement", GetAnnouncementReq = CreateCommonReq() },
                new HgProxyRequest { Kind = "get_banner", GetBannerReq = CreateCommonReq() },
                new HgProxyRequest { Kind = "get_sidebar", GetSidebarReq = CreateCommonReq() }
            }.ToList()
        };

        try
        {
            var jsonRequest = JsonSerializer.Serialize(requestBody, HgApiContext.Default.HgBatchRequest);
            SharedStatic.InstanceLogger.LogDebug($"[HgNews] Request Body:\n{jsonRequest}");

            using var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            using var response = await ApiResponseHttpClient.PostAsync(_webApiUrl, content, token);

            SharedStatic.InstanceLogger.LogDebug($"[HgNews] API Response Code: {response.StatusCode}");
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync(token);
            SharedStatic.InstanceLogger.LogDebug($"[HgNews] Response Body:\n{jsonResponse}");

            var rspBody = JsonSerializer.Deserialize(jsonResponse, HgApiContext.Default.HgBatchResponse);

            _newsResponse = rspBody?.ProxyRsps?.FirstOrDefault(x => x.Kind == "get_announcement")?.GetAnnouncementRsp;
            _bannerResponse = rspBody?.ProxyRsps?.FirstOrDefault(x => x.Kind == "get_banner")?.GetBannerRsp;
            _sidebarResponse = rspBody?.ProxyRsps?.FirstOrDefault(x => x.Kind == "get_sidebar")?.GetSidebarRsp;

            SharedStatic.InstanceLogger.LogDebug(
                $"[HgNews] Parsed responses: News={_newsResponse != null}, Banner={_bannerResponse != null}, Sidebar={_sidebarResponse != null}");
            return 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError($"[HgNews] Failed to init news: {ex}");
            return -1;
        }
    }

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (_newsResponse?.Tabs == null)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        var flatList = new List<FlatNewsItem>();
        foreach (var tab in _newsResponse.Tabs)
        {
            if (tab.Announcements == null) continue;
            var tName = tab.TabName ?? "Info";

            foreach (var item in tab.Announcements) flatList.Add(new FlatNewsItem { Item = item, TypeName = tName });
        }

        if (flatList.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = flatList.Count;
        var memory = PluginDisposableMemory<LauncherNewsEntry>.Alloc(count);
        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;
        //Todo: i am unable to support all the languages. before the launcher allows custom tabs, this method can only be used temporarily for classification.
        for (var i = 0; i < count; i++)
        {
            var flatItem = flatList[i];
            var item = flatItem.Item;
            // 资讯/新闻/News/Other
            var type = LauncherNewsEntryType.Info;
            var typeNameLower = flatItem.TypeName?.ToLowerInvariant() ?? "";

            // 公告 / Notice
            if (typeNameLower.Contains("公告") ||
                typeNameLower.Contains("notice") ||
                typeNameLower.Contains("announcement") ||
                typeNameLower.Contains("お知らせ") ||
                typeNameLower.Contains("공지"))
                type = LauncherNewsEntryType.Notice;
            // 活动 / Event
            else if (typeNameLower.Contains("活动") ||
                     typeNameLower.Contains("活動") ||
                     typeNameLower.Contains("event") ||
                     typeNameLower.Contains("イベント") ||
                     typeNameLower.Contains("이벤트"))
                type = LauncherNewsEntryType.Event;

            var dateStr = DateTime.Now.ToString("yyyy-MM-dd");
            if (!string.IsNullOrEmpty(item.StartTs) && long.TryParse(item.StartTs, out var ts))
                try
                {
                    dateStr = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("yyyy-MM-dd");
                }
                catch
                {
                }

            var content = item.Content ?? "";
            var jumpUrl = item.JumpUrl ?? "";

            ref var entry = ref memory[i];
            entry.Write(content, null, jumpUrl, dateStr, type);
        }
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        var banners = _bannerResponse?.Banners;
        if (banners == null || banners.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = banners.Count;
        var memory = PluginDisposableMemory<LauncherCarouselEntry>.Alloc(count);
        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;

        for (var i = 0; i < count; i++)
        {
            var banner = banners[i];
            var imgUrl = banner.Url ?? "";
            var jumpUrl = banner.JumpUrl ?? "";

            ref var entry = ref memory[i];
            entry.Write(null, imgUrl, jumpUrl);
        }
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        var sidebars = _sidebarResponse?.Sidebars;
        if (sidebars == null || sidebars.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = sidebars.Count;
        var memory = PluginDisposableMemory<LauncherSocialMediaEntry>.Alloc(count);
        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;

        for (var i = 0; i < count; i++)
        {
            var item = sidebars[i];
            string media = item.Media?.Trim() ?? string.Empty;
            string description = HgSocialMediaIcons.ResolveDisplayName(media);
            
            string jumpUrl = item.JumpUrl
                             ?? item.SidebarLabels?
                                 .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.JumpUrl))?
                                 .JumpUrl
                             ?? string.Empty;

            ReadOnlySpan<byte> iconData = HgSocialMediaIcons.Resolve(media);
            SharedStatic.InstanceLogger.LogInformation(
                "[HgNews][SocialMedia] Index={Index}, Media='{Media}', Description='{Description}', IconBytes={IconBytes}, JumpUrl='{JumpUrl}', QrUrl='{QrUrl}', ChildCount={ChildCount}",
                i, media, description, iconData.Length, jumpUrl, item.Pic?.Url ?? "<none>", item.SidebarLabels?.Count ?? 0);

            ref var entry = ref memory[i];
            entry.WriteIcon(iconData);
            entry.WriteIconHover(iconData);
            entry.WriteDescription(description);

            SharedStatic.InstanceLogger.LogInformation(
                "[HgNews][SocialMedia] Entry written: Index={Index}, Media='{Media}', IconPathLength={IconPathLength}, HoverPathLength={HoverPathLength}",
                i, media, entry.IconPath?.Length ?? 0, entry.IconHoverPath?.Length ?? 0);

            if (!string.IsNullOrWhiteSpace(jumpUrl))
                entry.WriteClickUrl(jumpUrl);

            // pic.url is the hover QR/promo image
            if (!string.IsNullOrWhiteSpace(item.Pic?.Url))
            {
                entry.WriteQrImage(item.Pic.Url);
                entry.WriteQrImageDescription(
                    string.IsNullOrWhiteSpace(item.Pic.Description)
                        ? description
                        : item.Pic.Description);

                SharedStatic.InstanceLogger.LogInformation(
                    "[HgNews][SocialMedia] QR written: Index={Index}, Media='{Media}', QrPath='{QrPath}', QrDescription='{QrDescription}'",
                    i, media, entry.QrPath ?? "<null>", entry.QrDescription ?? "<null>");
            }
        }
    }

    protected override async Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress,
        CancellationToken token)
    {
        SharedStatic.InstanceLogger.LogDebug($"[HgNews] Downloading asset: {fileUrl}");
        try
        {
            await base.DownloadAssetAsyncInner(ApiDownloadHttpClient, fileUrl, outputStream, fileChecksum,
                downloadProgress, token);
            SharedStatic.InstanceLogger.LogDebug(
                $"[HgNews] Download COMPLETED: {fileUrl} (Size: {outputStream.Length} bytes)");
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError($"[HgNews] Download FAILED: {fileUrl}\nException: {ex}");
        }
    }

    private static void InitializeEmpty(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
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

    private struct FlatNewsItem
    {
        public HgAnnouncement Item;
        public string TypeName;
    }
}