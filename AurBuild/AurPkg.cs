using System.Text.Json.Serialization;

namespace AurBuild;

internal sealed class AurPkg {
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("install")]
    public bool Install { get; init; }
}
