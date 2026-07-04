using System.Text.Json;

namespace CardReplace.Scripts.ArtPacks;

public static class ArtPackConfigStore
{
    private const string ConfigFileName = "card_replace_config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static ArtPackConfig LoadOrCreate(string modRoot, IModLog log)
    {
        var path = Path.Combine(modRoot, ConfigFileName);
        if (!File.Exists(path))
        {
            log.Info($"No {ConfigFileName} found; defaults will be generated.");
            return new ArtPackConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<ArtPackConfig>(File.ReadAllText(path), JsonOptions) ?? new ArtPackConfig();
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to read {path}: {ex.Message}. Defaults will be used.");
            return new ArtPackConfig();
        }
    }

    public static void Save(string modRoot, ArtPackConfig config, IModLog log)
    {
        var path = Path.Combine(modRoot, ConfigFileName);
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to write {path}: {ex.Message}");
        }
    }
}
