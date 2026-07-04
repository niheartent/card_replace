using System.Text.Json.Serialization;

namespace CardReplace.Scripts.ArtPacks;

public sealed class ArtPackManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("enabledDefault")]
    public bool EnabledDefault { get; set; } = true;

    [JsonPropertyName("priorityDefault")]
    public int PriorityDefault { get; set; } = 100;

    [JsonPropertyName("overrides")]
    public List<ArtOverride> Overrides { get; set; } = [];
}
