using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Core.Management.Api;

internal static class HgSocialMediaIcons
{
    private const string StableResourcePrefix = "Hypergryph.Core.Assets.SocialMedia.";
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static int _manifestLogged;

    internal static ReadOnlySpan<byte> Resolve(string? media)
    {
        string fileName = ResolveFileName(media);
        SharedStatic.InstanceLogger.LogInformation(
            "[HgSocialMediaIcons] Resolve media='{Media}', mappedFile='{FileName}'",
            media ?? "<null>", fileName);

        byte[] data = Cache.GetOrAdd(fileName, LoadEmbeddedSvg);
        if (data.Length != 0)
        {
            SharedStatic.InstanceLogger.LogInformation(
                "[HgSocialMediaIcons] Loaded media='{Media}', file='{FileName}', bytes={Length}",
                media ?? "<null>", fileName, data.Length);
            return data;
        }

        SharedStatic.InstanceLogger.LogWarning(
            "[HgSocialMediaIcons] Embedded SVG missing or empty for media='{Media}', file='{FileName}'. Using built-in paper-plane fallback.",
            media ?? "<null>", fileName);

        return Cache.GetOrAdd("__built_in_fallback.svg", _ => Encoding.UTF8.GetBytes(FallbackPaperPlaneSvg));
    }

    internal static string ResolveDisplayName(string? media)
    {
        return media?.Trim().ToLowerInvariant() switch
        {
            "bilibili" => "Bilibili",
            "douyin" => "抖音",
            "skland" => "森空岛",
            "skland_gl" => "SKPORT",
            "weibo" => "微博",
            "wechat" => "微信",
            "taptap" => "TapTap",
            "xiaohongshu" => "小红书",
            "cs" => "客服中心",
            "cs_gl" => "Customer Support",
            "sklandtool" => "社区工具",
            "x" => "X",
            "tiktok" => "TikTok",
            "facebook" => "Facebook",
            "youtube" => "YouTube",
            "instagram" => "Instagram",
            "discord" => "Discord",
            _ => string.IsNullOrWhiteSpace(media) ? "Media" : media.Trim()
        };
    }

    private static string ResolveFileName(string? media)
    {
        return media?.Trim().ToLowerInvariant() switch
        {
            "bilibili" => "bilibili.svg",
            "douyin" => "tiktok.svg",
            "skland" => "skyland.svg",
            "skland_gl" => "skyland.svg",
            "weibo" => "weibo.svg",
            "wechat" => "weixin.svg",
            "taptap" => "taptap.svg",
            "xiaohongshu" => "小红书.svg",
            "cs" => "客服中心.svg",
            "cs_gl" => "客服中心.svg",
            "sklandtool" => "toolbox.svg",
            "x" => "x.svg",
            "tiktok" => "tiktok.svg",
            "facebook" => "facebook.svg",
            "youtube" => "youtube.svg",
            "instagram" => "ins.svg",
            "discord" => "discord.svg",
            _ => "fallback.svg"
        };
    }

    private static byte[] LoadEmbeddedSvg(string fileName)
    {
        Assembly assembly = typeof(HgSocialMediaIcons).Assembly;
        string[] resourceNames = assembly.GetManifestResourceNames();

        if (System.Threading.Interlocked.Exchange(ref _manifestLogged, 1) == 0)
        {
            SharedStatic.InstanceLogger.LogInformation(
                "[HgSocialMediaIcons] Assembly='{AssemblyName}', FullName='{AssemblyFullName}', ResourceCount={ResourceCount}",
                assembly.GetName().Name ?? "<null>", assembly.FullName ?? "<null>", resourceNames.Length);

            if (resourceNames.Length == 0)
            {
                SharedStatic.InstanceLogger.LogWarning("[HgSocialMediaIcons] No manifest resources found in plugin assembly.");
            }
            else
            {
                foreach (string resourceName in resourceNames)
                    SharedStatic.InstanceLogger.LogInformation("[HgSocialMediaIcons] ManifestResource='{ResourceName}'", resourceName);
            }
        }

        string stableName = StableResourcePrefix + fileName;
        SharedStatic.InstanceLogger.LogInformation(
            "[HgSocialMediaIcons] Looking for stable resource '{StableName}'", stableName);

        Stream? stream = assembly.GetManifestResourceStream(stableName);
        string? matchedName = stream != null ? stableName : null;

        if (stream == null)
        {
            string suffix = ".Assets.SocialMedia." + fileName;
            matchedName = resourceNames.FirstOrDefault(name =>
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

            SharedStatic.InstanceLogger.LogInformation(
                "[HgSocialMediaIcons] Stable lookup missed. Suffix='{Suffix}', matched='{MatchedName}'",
                suffix, matchedName ?? "<none>");

            if (matchedName != null)
                stream = assembly.GetManifestResourceStream(matchedName);
        }

        if (stream == null)
        {
            SharedStatic.InstanceLogger.LogError(
                "[HgSocialMediaIcons] Failed to open embedded SVG. RequestedFile='{FileName}', StableName='{StableName}'",
                fileName, stableName);
            return [];
        }

        try
        {
            using (stream)
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                byte[] originalBytes = memory.ToArray();
                byte[] bytes = NormalizeSvgBytes(originalBytes, matchedName ?? stableName);
                string preview = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 96))
                    .Replace("\r", " ")
                    .Replace("\n", " ");

                SharedStatic.InstanceLogger.LogInformation(
                    "[HgSocialMediaIcons] Resource opened. Name='{ResourceName}', OriginalLength={OriginalLength}, NormalizedLength={NormalizedLength}, Preview='{Preview}'",
                    matchedName ?? stableName, originalBytes.Length, bytes.Length, preview);
                return bytes;
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(ex,
                "[HgSocialMediaIcons] Exception while reading resource '{ResourceName}'", matchedName ?? stableName);
            return [];
        }
    }


    private static byte[] NormalizeSvgBytes(byte[] source, string resourceName)
    {
        if (source.Length == 0)
            return source;
        
        string text;
        try
        {
            text = Encoding.UTF8.GetString(source);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError(ex,
                "[HgSocialMediaIcons] Cannot decode SVG resource '{ResourceName}' as UTF-8.", resourceName);
            return source;
        }

        int svgStart = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (svgStart < 0)
        {
            SharedStatic.InstanceLogger.LogError(
                "[HgSocialMediaIcons] Resource '{ResourceName}' does not contain an <svg element. Keeping original bytes.",
                resourceName);
            return source;
        }

        string normalized = text[svgStart..].TrimEnd();
        normalized = ForceSvgForegroundWhite(normalized, resourceName);
        byte[] result = Encoding.UTF8.GetBytes(normalized);

        SharedStatic.InstanceLogger.LogInformation(
            "[HgSocialMediaIcons] Normalized SVG resource '{ResourceName}': removedPrefixChars={RemovedPrefixChars}, startsWithSvg={StartsWithSvg}, firstCharCode=0x{FirstCharCode:X4}, forcedWhite=True",
            resourceName,
            svgStart,
            normalized.StartsWith("<svg", StringComparison.OrdinalIgnoreCase),
            normalized.Length > 0 ? normalized[0] : 0);

        return result;
    }

    private static string ForceSvgForegroundWhite(string svg, string resourceName)
    {
        int openingTagEnd = svg.IndexOf('>');
        if (openingTagEnd < 0)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[HgSocialMediaIcons] Cannot force white color for '{ResourceName}': opening <svg> tag is incomplete.",
                resourceName);
            return svg;
        }

        // Most exported icons either rely on SVG's default black fill, use currentColor,
        // or contain their own fill declarations. Injecting an !important style after the
        // opening <svg> tag handles all three cases while preserving elements explicitly
        // marked fill="none". Strokes are recolored only when a stroke attribute exists,
        // so ordinary filled icons do not gain an unwanted outline.
        const string whiteStyle =
            "<style>svg *:not([fill=\"none\"]){fill:#fff!important;color:#fff!important;}" +
            "svg [stroke]:not([stroke=\"none\"]){stroke:#fff!important;}</style>";

        string result = svg.Insert(openingTagEnd + 1, whiteStyle);
        SharedStatic.InstanceLogger.LogInformation(
            "[HgSocialMediaIcons] Forced SVG foreground to white for resource '{ResourceName}'.",
            resourceName);
        return result;
    }

    private const string FallbackPaperPlaneSvg = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
  <path fill="currentColor" d="M21.7 2.3a1 1 0 0 0-1.05-.23l-18 7a1 1 0 0 0 .08 1.89l7.1 2.37 2.37 7.1a1 1 0 0 0 .92.68h.03a1 1 0 0 0 .92-.61l7.87-17.1a1 1 0 0 0-.24-1.1ZM11.1 11.5 5.9 9.77l11.32-4.4-6.12 6.13Zm2.97 5.76-1.73-5.2 6.13-6.12-4.4 11.32Z"/>
</svg>
""";
}
