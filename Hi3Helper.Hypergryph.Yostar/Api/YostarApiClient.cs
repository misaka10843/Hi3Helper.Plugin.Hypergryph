using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Hypergryph.Yostar.Api;

internal sealed class YostarApiClient : IDisposable
{
    private readonly HttpClient _apiHttpClient;
    private readonly HttpClient _downloadHttpClient;
    private readonly YostarLauncherOptions _options;

    public YostarApiClient(YostarLauncherOptions options)
    {
        _options = options;
        _apiHttpClient = CreateHttpClient();
        _apiHttpClient.BaseAddress = options.ApiBaseUri;
        _apiHttpClient.Timeout = TimeSpan.FromSeconds(30);

        _downloadHttpClient = CreateHttpClient();
        _downloadHttpClient.Timeout = TimeSpan.FromMinutes(10);
    }

    internal HttpClient DownloadHttpClient => _downloadHttpClient;

    public async Task<YostarBaseConfig> GetBaseConfigAsync(CancellationToken token)
    {
        var response = await GetApiAsync("api/launcher/base/config",
            YostarJsonContext.Default.YostarApiResponseYostarBaseConfig, token).ConfigureAwait(false);
        return EnsureSuccessful(response, "base config");
    }

    public async Task<YostarOperationsResource> GetOperationsResourceAsync(CancellationToken token)
    {
        var response = await GetApiAsync("api/launcher/operations/resource",
            YostarJsonContext.Default.YostarApiResponseYostarOperationsResource, token).ConfigureAwait(false);
        return EnsureSuccessful(response, "operations resource");
    }

    public async Task<YostarSocialMediaResource?> GetSocialMediaResourceAsync(CancellationToken token)
    {
        var response = await GetApiAsync("api/launcher/social/media/resource",
            YostarJsonContext.Default.YostarApiResponseYostarSocialMediaResource, token).ConfigureAwait(false);
        return EnsureSuccessful(response, "social media resource", allowNullData: true);
    }

    public async Task<YostarGameConfig> GetGameConfigAsync(CancellationToken token)
    {
        var response = await GetApiAsync("api/launcher/game/config",
            YostarJsonContext.Default.YostarApiResponseYostarGameConfig, token).ConfigureAwait(false);
        return EnsureSuccessful(response, "game config");
    }

    public async Task<YostarCdnConfig> GetCdnConfigAsync(CancellationToken token)
    {
        var response = await GetApiAsync("api/launcher/advanced/game/download/cdn",
            YostarJsonContext.Default.YostarApiResponseYostarCdnConfig, token).ConfigureAwait(false);
        return EnsureSuccessful(response, "CDN config");
    }

    public async Task<YostarRemoteManifest> GetManifestAsync(string version, string filePath,
        CancellationToken token)
    {
        string path = "api/launcher/game/config/json?version=" + Uri.EscapeDataString(version) +
                      "&file_path=" + Uri.EscapeDataString(filePath);
        var urlResponse = await GetApiAsync(path,
            YostarJsonContext.Default.YostarApiResponseYostarManifestUrl, token).ConfigureAwait(false);
        var manifestUrl = EnsureSuccessful(urlResponse, "manifest URL").Url;
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? uri))
            throw new InvalidDataException("Yostar returned an invalid manifest URL.");

        var builder = new UriBuilder(uri)
        {
            Query = "nocache=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        using var response = await _downloadHttpClient.GetAsync(builder.Uri, HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, YostarJsonContext.Default.YostarRemoteManifest, token)
                   .ConfigureAwait(false) ??
               throw new InvalidDataException("Yostar returned an empty manifest.");
    }

    public async Task<YostarTargetPackage> GetTargetPackageAsync(CancellationToken token)
    {
        YostarGameConfig config = await GetGameConfigAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(config.LatestVersion) || string.IsNullOrWhiteSpace(config.LatestFilePath))
            throw new InvalidDataException("Yostar game config does not contain a target version and file path.");

        Task<YostarRemoteManifest> manifestTask =
            GetManifestAsync(config.LatestVersion, config.LatestFilePath, token);
        Task<YostarCdnConfig> cdnTask = GetCdnConfigAsync(token);
        await Task.WhenAll(manifestTask, cdnTask).ConfigureAwait(false);
        return new YostarTargetPackage(config, await manifestTask, await cdnTask);
    }

    private async Task<YostarApiResponse<T>> GetApiAsync<T>(string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<YostarApiResponse<T>> typeInfo,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", CreateAuthorizationHeader());
        using var response = await _apiHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, token).ConfigureAwait(false) ??
               throw new InvalidDataException("Yostar returned an empty API response.");
    }

    private string CreateAuthorizationHeader()
    {
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string head = $"{{\"game_tag\":\"{_options.GameTag}\",\"time\":{time},\"version\":\"{_options.LauncherVersion}\"}}";
        byte[] signBytes = MD5.HashData(Encoding.UTF8.GetBytes(head + _options.AuthorizationSalt));
        string sign = Convert.ToHexStringLower(signBytes);
        return $"{{\"head\":{head},\"sign\":\"{sign}\"}}";
    }

    private static T EnsureSuccessful<T>(YostarApiResponse<T> response, string operation, bool allowNullData = false)
    {
        if (response.Code != 200 || (!allowNullData && response.Data == null))
            throw new HttpRequestException(
                $"Yostar {operation} request failed with code {response.Code}: {response.Message}");
        return response.Data!;
    }

    private static HttpClient CreateHttpClient()
    {
        return new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.GZip | DecompressionMethods.Deflate)
            .AllowCookies()
            .AllowRedirections()
            .Create();
    }

    public void Dispose()
    {
        _apiHttpClient.Dispose();
        _downloadHttpClient.Dispose();
    }
}
