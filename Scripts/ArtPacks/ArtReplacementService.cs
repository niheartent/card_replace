using Godot;
using MegaCrit.Sts2.Core.Models;

namespace CardReplace.Scripts.ArtPacks;

public static class ArtReplacementService
{
    private static readonly Dictionary<string, ResolvedArtOverride> Overrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> TextureCache = new(StringComparer.OrdinalIgnoreCase);
    private static IModLog? _log;

    public static void Initialize(IEnumerable<ResolvedArtOverride> overrides, IModLog log)
    {
        _log = log;
        Overrides.Clear();
        TextureCache.Clear();

        foreach (var resolved in overrides)
        {
            Overrides[NormalizeSourcePath(resolved.Override.SourcePath)] = resolved;
        }
    }

    public static bool TryGetReplacement(CardModel model, out Texture2D texture)
    {
        texture = null!;

        var sourcePath = GetCardPortraitPngPath(model);
        if (!Overrides.TryGetValue(NormalizeSourcePath(sourcePath), out var resolved))
        {
            return false;
        }

        if (resolved.Override.IsGif)
        {
            _log?.Warn($"GIF replacement is indexed but not animated yet: {resolved.AssetPath}");
            return false;
        }

        if (TextureCache.TryGetValue(resolved.AssetPath, out texture!))
        {
            return true;
        }

        var image = Image.LoadFromFile(resolved.AssetPath);
        if (image is null || image.IsEmpty())
        {
            _log?.Warn($"Failed to load replacement image: {resolved.AssetPath}");
            return false;
        }

        texture = ImageTexture.CreateFromImage(image);
        TextureCache[resolved.AssetPath] = texture;
        return true;
    }

    private static string GetCardPortraitPngPath(CardModel model)
    {
        var pool = model.Pool.Title.ToLowerInvariant();
        var entry = model.Id.Entry.ToLowerInvariant();
        return $"res://images/packed/card_portraits/{pool}/{entry}.png";
    }

    private static string NormalizeSourcePath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }
}
