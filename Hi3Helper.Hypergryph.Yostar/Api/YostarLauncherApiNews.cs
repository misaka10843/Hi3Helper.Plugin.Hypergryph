using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Yostar.Api;

/// <summary>
/// Implements ILauncherApiNews on top of Yostar's launcher API.
/// Banners and news come from <c>GET /api/launcher/operations/resource</c>,
/// social media entries from <c>GET /api/launcher/social/media/resource</c>.
/// Both endpoints only return links/URLs (no embedded icon data), so social
/// media entries are emitted without icons and the host falls back to its
/// default rendering for null icon images.
/// </summary>
[GeneratedComClass]
public partial class YostarLauncherApiNews : LauncherApiNewsBase
{
    private readonly YostarLauncherOptions _options;

    private YostarApiClient? _apiClient;
    private YostarOperationsResource? _operationsResource;
    private YostarSocialMediaResource? _socialMediaResource;

    public YostarLauncherApiNews(YostarLauncherOptions options) => _options = options;

    [field: AllowNull]
    [field: MaybeNull]
    protected override HttpClient ApiResponseHttpClient { get; set; } = new PluginHttpClientBuilder()
        .SetAllowedDecompression(DecompressionMethods.None)
        .AllowCookies()
        .AllowRedirections()
        .Create();

    protected override string? ApiResponseBaseUrl => _options.ApiBaseUri.ToString();

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        try
        {
            _apiClient = new YostarApiClient(_options);
            Task<YostarOperationsResource> operationsTask = _apiClient.GetOperationsResourceAsync(token);
            Task<YostarSocialMediaResource?> socialTask = _apiClient.GetSocialMediaResourceAsync(token);
            await Task.WhenAll(operationsTask, socialTask).ConfigureAwait(false);
            _operationsResource = operationsTask.Result;
            _socialMediaResource = socialTask.Result;
            return 0;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError($"[YostarNews] Failed to init news: {ex}");
            return -1;
        }
    }

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        var categories = _operationsResource?.NewsList?.Data?.News;
        if (categories == null || categories.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        var flatItems = categories
            .SelectMany(category => (category.Rows ?? [])
                .Select(row => (Category: category.TypeLabel, Item: row)))
            .Where(x => !string.IsNullOrEmpty(x.Item.Title) && !string.IsNullOrEmpty(x.Item.Link))
            .ToList();

        if (flatItems.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = flatItems.Count;
        var memory = PluginDisposableMemory<LauncherNewsEntry>.Alloc(count);
        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;

        for (var i = 0; i < count; i++)
        {
            var (category, item) = flatItems[i];

            string postDate = string.Empty;
            if (item.PublishTime is > 0)
            {
                try
                {
                    postDate = DateTimeOffset.FromUnixTimeMilliseconds(item.PublishTime.Value).ToLocalTime()
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            ref var entry = ref memory[i];
            entry.Write(item.Title, null, item.Link, postDate, ResolveNewsType(category));
        }
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        var banners = _operationsResource?.Banners;
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
            ref var entry = ref memory[i];
            entry.Write(null, banners[i].Image, banners[i].JumpUrl);
        }
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        var items = _socialMediaResource?.Items;
        if (items == null || items.Count == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        count = items.Count;
        var memory = PluginDisposableMemory<LauncherSocialMediaEntry>.Alloc(count);
        handle = memory.AsSafePointer();
        isDisposable = true;
        isAllocated = true;

        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            ref var entry = ref memory[i];

            entry.WriteDescription(item.Channel);
            if (!string.IsNullOrWhiteSpace(item.JumpUrl)) entry.WriteClickUrl(item.JumpUrl);
            if (!string.IsNullOrWhiteSpace(item.QrImage))
            {
                entry.WriteQrImage(item.QrImage);
                entry.WriteQrImageDescription(item.Channel);
            }
        }
    }

    public override void Dispose()
    {
        if (IsDisposed) return;
        _apiClient?.Dispose();
        ApiResponseHttpClient?.Dispose();
        base.Dispose();
    }

    private static LauncherNewsEntryType ResolveNewsType(string? typeLabel)
    {
        var label = typeLabel?.ToLowerInvariant() ?? string.Empty;

        if (label.Contains("notice") ||
            label.Contains("announcement") ||
            label.Contains("公告") ||
            label.Contains("お知らせ") ||
            label.Contains("공지"))
            return LauncherNewsEntryType.Notice;

        if (label.Contains("event") ||
            label.Contains("活动") ||
            label.Contains("活動") ||
            label.Contains("イベント") ||
            label.Contains("이벤트"))
            return LauncherNewsEntryType.Event;

        return LauncherNewsEntryType.Info;
    }

    private static void InitializeEmpty(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }
}
