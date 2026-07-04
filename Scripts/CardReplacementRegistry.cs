using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace CardReplace.Scripts;

internal static class CardReplacementRegistry
{
    private const string ReplacementMapPath = "res://generated/card_replace/card_replacements.json";

    private static readonly Dictionary<string, ReplacementEntry> EntriesByCardId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ReplacementEntry> EntriesBySourcePath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D?> TextureCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ImagePaths = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static IModLog? _log;

    public static int Count => EntriesBySourcePath.Count;

    public static void EnsureLoaded(IModLog? log = null)
    {
        _log ??= log;
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        EntriesByCardId.Clear();
        EntriesBySourcePath.Clear();
        ImagePaths.Clear();

        try
        {
            var json = Godot.FileAccess.GetFileAsString(ReplacementMapPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                _log?.Warn($"{Entry.ModId}: replacement map is empty or missing: {ReplacementMapPath}");
                return;
            }

            var document = JsonSerializer.Deserialize<ReplacementDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            foreach (var entry in document?.Entries ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Image))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.CardId))
                {
                    EntriesByCardId[Normalize(entry.CardId)] = entry;
                }

                if (!string.IsNullOrWhiteSpace(entry.SourcePath))
                {
                    EntriesBySourcePath[Normalize(entry.SourcePath)] = entry;
                }

                ImagePaths.Add(Normalize(entry.Image));
            }

            _log?.Info($"{Entry.ModId}: loaded replacement map entries={EntriesBySourcePath.Count}, file={ReplacementMapPath}");
        }
        catch (Exception ex)
        {
            _log?.Warn($"{Entry.ModId}: failed to load replacement map: {ex.Message}");
        }
    }

    public static bool TryGetTexture(CardModel? model, out Texture2D? texture)
    {
        EnsureLoaded();
        texture = null;
        if (model is null)
        {
            return false;
        }

        var cardId = model.GetType().FullName ?? model.GetType().Name;
        if (EntriesByCardId.TryGetValue(Normalize(cardId), out var byCardId))
        {
            return TryLoadTexture(byCardId, out texture);
        }

        var sourcePath = GetCardPortraitSourcePath(model);
        return !string.IsNullOrWhiteSpace(sourcePath)
            && EntriesBySourcePath.TryGetValue(Normalize(sourcePath), out var bySourcePath)
            && TryLoadTexture(bySourcePath, out texture);
    }

    public static bool IsReplacementTexture(Texture2D? texture)
    {
        EnsureLoaded();
        var path = texture?.ResourcePath;
        return !string.IsNullOrWhiteSpace(path) && ImagePaths.Contains(Normalize(path));
    }

    private static bool TryLoadTexture(ReplacementEntry entry, out Texture2D? texture)
    {
        texture = null;
        if (TextureCache.TryGetValue(entry.Image, out var cached))
        {
            texture = cached;
            return texture is not null;
        }

        texture = GD.Load<Texture2D>(entry.Image);
        TextureCache[entry.Image] = texture;
        if (texture is null)
        {
            _log?.Warn($"{Entry.ModId}: failed to load texture: {entry.Image}");
            return false;
        }

        return true;
    }

    private static string GetCardPortraitSourcePath(CardModel model)
    {
        try
        {
            return $"res://images/packed/card_portraits/{model.Pool.Title.ToLowerInvariant()}/{model.Id.Entry.ToLowerInvariant()}.png";
        }
        catch (Exception ex)
        {
            _log?.Warn($"{Entry.ModId}: failed to resolve card source path for {model.GetType().FullName}: {ex.Message}");
            return "";
        }
    }

    private static string Normalize(string value)
    {
        return value.Trim();
    }

    private sealed class ReplacementDocument
    {
        [JsonPropertyName("entries")]
        public List<ReplacementEntry> Entries { get; set; } = [];
    }

    private sealed class ReplacementEntry
    {
        [JsonPropertyName("cardId")]
        public string CardId { get; set; } = "";

        [JsonPropertyName("sourcePath")]
        public string SourcePath { get; set; } = "";

        [JsonPropertyName("image")]
        public string Image { get; set; } = "";
    }
}
