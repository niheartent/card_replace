using System.Text.Json.Serialization;

namespace CardReplace.Scripts.ArtPacks;

public sealed class ArtPackConfig
{
    [JsonPropertyName("packs")]
    public List<ArtPackConfigEntry> Packs { get; set; } = [];

    public ArtPackConfigEntry EnsurePack(ArtPackManifest manifest)
    {
        var existing = Packs.FirstOrDefault(pack => string.Equals(pack.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var entry = new ArtPackConfigEntry
        {
            Id = manifest.Id,
            Enabled = manifest.EnabledDefault,
            Priority = manifest.PriorityDefault
        };
        Packs.Add(entry);
        return entry;
    }
}

public sealed class ArtPackConfigEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 100;
}
