using System;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.Endfield;

[GeneratedComClass]
// ReSharper disable once InconsistentNaming
internal partial class SelfUpdate : PluginSelfUpdateBase
{
    private const string ExCdnFileSuffix = "Endfield/";
    private const string ExCdn1Url =
        "https://cl-plugins.sakurakoi.top/" + ExCdnFileSuffix;

    private const string ExCdn2Url =
        "https://github.com/misaka10843/CollapsePlugin-ReleaseRepo/raw/main/" + ExCdnFileSuffix;

    protected readonly string[] BaseCdnUrl = [ExCdn1Url, ExCdn2Url];

    internal SelfUpdate()
    {
        UpdateHttpClient = new PluginHttpClientBuilder()
            .AllowRedirections()
            .AllowUntrustedCert()
            .AllowCookies()
            .Create();
    }

    protected override ReadOnlySpan<string> BaseCdnUrlSpan => BaseCdnUrl;
    protected override HttpClient UpdateHttpClient { get; }
}