using System.Text.Json.Serialization;

namespace CardReplace.Scripts.ArtPacks;

public sealed class ArtOverride
{
    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "static";

    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    public bool IsGif => string.Equals(Type, "gif", StringComparison.OrdinalIgnoreCase);
}
