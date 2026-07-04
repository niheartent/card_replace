using System.Text.Json;

namespace CardReplace.Scripts.ArtPacks;

public sealed class ArtPackRegistry
{
    private readonly IModLog _log;

    private ArtPackRegistry(List<LoadedArtPack> packs, IModLog log)
    {
        Packs = packs;
        _log = log;
    }

    public IReadOnlyList<LoadedArtPack> Packs { get; }

    public static ArtPackRegistry Load(string modRoot, ArtPackConfig config, IModLog log)
    {
        var packsRoot = Path.Combine(modRoot, "packs");
        if (!Directory.Exists(packsRoot))
        {
            log.Warn($"No packs directory found: {packsRoot}");
            return new ArtPackRegistry([], log);
        }

        var packs = new List<LoadedArtPack>();
        foreach (var packDir in Directory.EnumerateDirectories(packsRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(packDir, "pack.json");
            if (!File.Exists(manifestPath))
            {
                log.Warn($"Skipping pack directory without pack.json: {packDir}");
                continue;
            }

            var manifest = ReadManifest(manifestPath, log);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                log.Warn($"Skipping invalid pack manifest: {manifestPath}");
                continue;
            }

            var state = config.EnsurePack(manifest);
            packs.Add(new LoadedArtPack(packDir, manifest, state));
            log.Info($"Found art pack '{manifest.Name}' ({manifest.Id}), enabled={state.Enabled}, priority={state.Priority}.");
        }

        return new ArtPackRegistry(packs, log);
    }

    public IReadOnlyList<ResolvedArtOverride> ResolveEffectiveOverrides()
    {
        var result = new Dictionary<string, ResolvedArtOverride>(StringComparer.OrdinalIgnoreCase);
        var enabledPacks = Packs
            .Where(pack => pack.Config.Enabled)
            .OrderBy(pack => pack.Config.Priority)
            .ThenBy(pack => pack.Manifest.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var pack in enabledPacks)
        {
            foreach (var artOverride in pack.Manifest.Overrides)
            {
                if (!IsValidOverride(pack, artOverride, out var assetPath))
                {
                    continue;
                }

                var resolved = new ResolvedArtOverride(pack.Manifest.Id, pack.Manifest.Name, pack.Config.Priority, artOverride, assetPath);
                if (result.TryGetValue(artOverride.SourcePath, out var previous))
                {
                    _log.Info(
                        $"Override conflict for {artOverride.SourcePath}: '{previous.PackId}' was replaced by '{pack.Manifest.Id}' " +
                        $"because priority {pack.Config.Priority} wins.");
                }

                result[artOverride.SourcePath] = resolved;
            }
        }

        return result.Values
            .OrderBy(item => item.Override.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ArtPackManifest? ReadManifest(string manifestPath, IModLog log)
    {
        try
        {
            return JsonSerializer.Deserialize<ArtPackManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to read {manifestPath}: {ex.Message}");
            return null;
        }
    }

    private bool IsValidOverride(LoadedArtPack pack, ArtOverride artOverride, out string assetPath)
    {
        assetPath = Path.GetFullPath(Path.Combine(pack.DirectoryPath, artOverride.File));
        var packRoot = Path.GetFullPath(pack.DirectoryPath);

        if (string.IsNullOrWhiteSpace(artOverride.SourcePath))
        {
            _log.Warn($"Pack {pack.Manifest.Id} contains an override without source_path.");
            return false;
        }

        if (!assetPath.StartsWith(packRoot, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"Pack {pack.Manifest.Id} override escapes its pack directory: {artOverride.File}");
            return false;
        }

        if (!File.Exists(assetPath))
        {
            _log.Warn($"Pack {pack.Manifest.Id} missing asset: {assetPath}");
            return false;
        }

        return true;
    }
}

public sealed record LoadedArtPack(string DirectoryPath, ArtPackManifest Manifest, ArtPackConfigEntry Config);

public sealed record ResolvedArtOverride(
    string PackId,
    string PackName,
    int Priority,
    ArtOverride Override,
    string AssetPath);
