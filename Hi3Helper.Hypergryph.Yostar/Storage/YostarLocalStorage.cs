using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Hypergryph.Yostar.Api;

namespace Hi3Helper.Hypergryph.Yostar.Storage;

internal sealed record YostarLocalGameConfig(
    string Tag,
    string Version,
    string Name,
    IReadOnlyList<string> Parameters,
    string UninstallScript);

internal sealed record YostarLocalManifest(
    string Name,
    string Version,
    string Basis,
    IReadOnlyList<YostarManifestFile> Files);

internal static class YostarLocalStorage
{
    private static readonly object MetadataGate = new();
    private const string JournalName = "yostar-metadata-pending";
    public const string ConfigFileName = "game-launcher-config.json";
    public const string ManifestFileName = "manifest.json";

    public static YostarLocalGameConfig? ReadGameConfig(string gamePath)
    {
        try
        {
            byte[]? bytes = ReadMetadata(gamePath, ConfigFileName);
            if (bytes == null) return null;
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (!ValidateObjectHash(root, "vc")) return null;

            string tag = GetString(root, "tag");
            string version = GetString(root, "version");
            string name = GetString(root, "name");
            string uninstallScript = GetString(root, "gameUninstallScript");
            var parameters = new List<string>();
            if (root.TryGetProperty("params", out JsonElement paramsElement) &&
                paramsElement.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in paramsElement.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        parameters.Add(item.GetString() ?? string.Empty);

            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(version)) return null;
            return new YostarLocalGameConfig(tag, version, name, parameters, uninstallScript);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static YostarLocalManifest? ReadManifest(string gamePath)
    {
        try
        {
            byte[]? bytes = ReadMetadata(gamePath, ManifestFileName);
            if (bytes == null) return null;
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            string name = GetString(root, "name");
            string version = GetString(root, "version");
            string basis = GetString(root, "basis");
            string expectedInfoHash = GetString(root, "vc");
            string actualInfoHash = HashJoinedValues([name, version, basis]);
            if (!string.Equals(expectedInfoHash, actualInfoHash, StringComparison.Ordinal)) return null;

            var files = new List<YostarManifestFile>();
            if (root.TryGetProperty("files", out JsonElement filesElement) &&
                filesElement.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in filesElement.EnumerateArray())
                {
                    if (!ValidateObjectHash(item, "vc")) continue;
                    string filePath = GetString(item, "path");
                    string size = GetString(item, "size");
                    string hash = GetString(item, "hash");
                    if (string.IsNullOrWhiteSpace(filePath)) continue;
                    files.Add(new YostarManifestFile { Path = filePath, Size = size, Hash = hash });
                }

            return new YostarLocalManifest(name, version, basis, files);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static async Task WriteMetadataAsync(string gamePath, YostarLauncherOptions options,
        YostarGameConfig config, IReadOnlyList<YostarManifestFile> files, CancellationToken token)
    {
        string version = config.LatestVersion ?? throw new InvalidDataException("Target version is missing.");
        string basis = config.LatestFilePath ?? throw new InvalidDataException("Target file path is missing.");
        string executableName = string.IsNullOrWhiteSpace(config.StartExecutableName)
            ? options.DefaultExecutableName
            : config.StartExecutableName.Trim();
        string journal = GetJournalPath(gamePath);
        string staging = journal + "-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            await WriteManifestAsync(Path.Combine(staging, ManifestFileName), options.GameTag, version, basis, files, token)
                .ConfigureAwait(false);
            await WriteConfigAsync(Path.Combine(staging, ConfigFileName), options.GameTag, version, executableName,
                config.StartParameters ?? [], config.UninstallScript ?? string.Empty, token).ConfigureAwait(false);

            lock (MetadataGate)
            {
                token.ThrowIfCancellationRequested();
                RecoverMetadata(gamePath, journal);
                Directory.Move(staging, journal);
                RecoverMetadata(gamePath, journal);
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static byte[]? ReadMetadata(string gamePath, string name)
    {
        lock (MetadataGate)
        {
            RecoverMetadata(gamePath, GetJournalPath(gamePath));
            string path = Shared.InstallPath.Resolve(gamePath, name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }

    private static string GetJournalPath(string gamePath)
    {
        string pluginRoot = Path.Combine(gamePath, "_Hi3_Hg");
        Shared.InstallPath.RejectLink(pluginRoot);
        return Shared.InstallPath.Resolve(pluginRoot, JournalName);
    }

    private static void RecoverMetadata(string gamePath, string journal)
    {
        if (!Directory.Exists(journal)) return;
        foreach (string name in new[] { ManifestFileName, ConfigFileName })
        {
            string source = Shared.InstallPath.Resolve(journal, name);
            string target = Shared.InstallPath.Resolve(gamePath, name);
            if (File.Exists(source)) File.Move(source, target, true);
            else if (!File.Exists(target)) throw new IOException($"Missing Yostar metadata: {name}");
        }
        Directory.Delete(journal);
    }

    public static string NormalizeExecutableName(string? value, string fallback)
    {
        string result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return Path.HasExtension(result) ? result : result + ".exe";
    }

    private static async Task WriteManifestAsync(string path, string tag, string version, string basis,
        IReadOnlyList<YostarManifestFile> files, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("name", tag);
        writer.WriteString("version", version);
        writer.WriteString("basis", basis);
        writer.WriteString("vc", HashJoinedValues([tag, version, basis]));
        writer.WritePropertyName("files");
        writer.WriteStartArray();
        foreach (YostarManifestFile file in files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            writer.WriteString("hash", file.Hash);
            writer.WriteString("size", file.Size);
            writer.WriteString("vc", HashJoinedValues([file.Path, file.Hash, file.Size]));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task WriteConfigAsync(string path, string tag, string version, string executableName,
        IReadOnlyList<string> parameters, string uninstallScript, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("tag", tag);
        writer.WriteString("name", executableName);
        writer.WritePropertyName("params");
        writer.WriteStartArray();
        foreach (string parameter in parameters) writer.WriteStringValue(parameter);
        writer.WriteEndArray();
        writer.WriteString("version", version);
        writer.WriteString("gameUninstallScript", uninstallScript);
        writer.WriteString("vc", HashJoinedValues(
            [tag, executableName, string.Join(',', parameters), version, uninstallScript]));
        writer.WriteEndObject();
        await writer.FlushAsync(token).ConfigureAwait(false);
    }

    private static bool ValidateObjectHash(JsonElement element, string hashProperty)
    {
        if (!element.TryGetProperty(hashProperty, out JsonElement hashElement) ||
            hashElement.ValueKind != JsonValueKind.String)
            return false;

        var values = new List<string>();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals(hashProperty)) continue;
            values.Add(ToJavaScriptString(property.Value));
        }

        return string.Equals(hashElement.GetString(), HashJoinedValues(values), StringComparison.Ordinal);
    }

    private static string ToJavaScriptString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Array => string.Join(',', value.EnumerateArray().Select(ToJavaScriptString)),
            _ => "[object Object]"
        };
    }

    private static string HashJoinedValues(IEnumerable<string> values)
    {
        byte[] bytes = MD5.HashData(Encoding.UTF8.GetBytes(string.Join(';', values)));
        return Convert.ToBase64String(bytes);
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }
}
