using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Shared;

public static class PluginCulture
{
    public static string Locale { get; private set; } = "en-US";

    public static bool TrySet(string? value, out string coreLocale)
    {
        coreLocale = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        string locale = value.Trim().Replace('_', '-');
        string[] parts = locale.Split('-');
        bool valid = parts[0].Length is >= 2 and <= 3;
        for (int index = 0; index < parts.Length && valid; index++)
        {
            if (parts[index].Length is < 1 or > 8) { valid = false; break; }
            foreach (char character in parts[index])
                if (!(index == 0 ? char.IsAsciiLetter(character) : char.IsAsciiLetterOrDigit(character))) { valid = false; break; }
        }
        if (!valid)
        {
            SharedStatic.InstanceLogger.LogWarning("[Locale] Invalid locale: {Locale}", value);
            return false;
        }
        // Keep the full tag for our UI, but pass an upstream-compatible language/region pair to Core.
        string? region = parts.Skip(1).FirstOrDefault(part =>
            part.Length == 2 && part.All(char.IsAsciiLetter) || part.Length == 3 && part.All(char.IsAsciiDigit));
        region ??= parts[0].ToLowerInvariant() switch
        {
            "en" => "US", "ja" => "JP", "zh" => parts.Any(p => p.Equals("Hant", StringComparison.OrdinalIgnoreCase)) ? "TW" : "CN", _ => "001"
        };
        coreLocale = parts[0] + "-" + region;
        Locale = locale;
        return true;
    }
}
