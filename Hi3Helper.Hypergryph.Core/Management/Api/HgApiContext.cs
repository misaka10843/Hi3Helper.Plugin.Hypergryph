using System.Text.Json.Serialization;

namespace Hi3Helper.Hypergryph.Core.Management.Api;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HgBatchRequest))]
[JsonSerializable(typeof(HgBatchResponse))]
[JsonSerializable(typeof(HgPatchManifest))]
[JsonSerializable(typeof(HgManifestNode))]
public partial class HgApiContext : JsonSerializerContext
{
}
