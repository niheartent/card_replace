using System.Text.Json;
using STS2RitsuLib;
using STS2RitsuLib.Settings;

namespace CardReplace.Scripts;

public static class RitsuSettingsBridge
{
    private const string FinalManifestFileName = "manifest.final.cardreplace";
    private const string ConflictReportFileName = "conflicts.report.cardreplace";

    public static void Register(string modRoot, string pckPath, bool pckLoaded, IModLog log)
    {
        var summary = ReadBuildSummary(modRoot, log);
        LogBuildSummary(pckPath, pckLoaded, summary, log);

        RitsuLibFramework.RegisterModSettings(
            Entry.ModId,
            page => BuildPage(page, pckPath, pckLoaded, summary),
            "Card Replace");
    }

    private static void BuildPage(
        ModSettingsPageBuilder page,
        string pckPath,
        bool pckLoaded,
        BuildSummary summary)
    {
        page
            .WithModDisplayName(Text("Card Replace"))
            .WithTitle(Text("Card Replace"))
            .WithDescription(Text("Generated card art PCK status and build report."))
            .AddSection("status", section => BuildStatusSection(section, pckPath, pckLoaded, summary))
            .AddSection("packs", section => BuildPacksSection(section, summary))
            .AddSection("coverage", section => BuildCoverageSection(section, summary))
            .AddSection("conflicts", section => BuildConflictsSection(section, summary));
    }

    private static void BuildStatusSection(
        ModSettingsSectionBuilder section,
        string pckPath,
        bool pckLoaded,
        BuildSummary summary)
    {
        section
            .WithTitle(Text("Generated Texture Pack"))
            .WithDescription(Text("This page is informational. Change pack priority in the builder, regenerate the PCK, then restart the game."));

        section.AddParagraph(
            "pck_status",
            Text(pckLoaded ? "PCK loaded" : "PCK not loaded"),
            Text($"{pckPath} ({FormatFileSize(pckPath)})"),
            null);

        section.AddParagraph(
            "build_summary",
            Text("Build summary"),
            Text(
                $"Generated: {summary.GeneratedAt}; packs: {summary.EnabledPackCount}; " +
                $"replacements: {summary.ReplacementCount}; unique source paths: {summary.UniqueSourceCount}; conflicts: {summary.ConflictCount}"),
            null);

        section.AddParagraph(
            "load_rule",
            Text("Load rule"),
            Text("Static generated assets. The PCK contains imported textures and a replacement map; the DLL applies those textures when card UI nodes refresh."),
            null);
    }

    private static void BuildPacksSection(ModSettingsSectionBuilder section, BuildSummary summary)
    {
        section
            .WithTitle(Text("Input Packs"))
            .WithDescription(Text("Lower file sequence wins. For example, 1-*.cardartpack.json has higher priority than 2-*.cardartpack.json."));

        if (summary.Packs.Count == 0)
        {
            section.AddParagraph("packs_empty", Text("No pack report"), Text("manifest.final.cardreplace was not found or could not be parsed."), null);
            return;
        }

        var rank = 1;
        foreach (var pack in summary.Packs.OrderByDescending(pack => pack.Priority).ThenBy(pack => pack.Sequence ?? int.MaxValue))
        {
            var sequence = pack.Sequence is null ? "unknown" : pack.Sequence.Value.ToString();
            section.AddParagraph(
                $"pack_{rank}",
                Text($"#{rank} sequence {sequence}: {pack.Id}"),
                Text($"Priority value: {pack.Priority}; targets: {pack.ReplacementCount}; file: {pack.Path}"),
                null);
            rank++;
        }
    }

    private static void BuildCoverageSection(ModSettingsSectionBuilder section, BuildSummary summary)
    {
        section
            .WithTitle(Text("Generated Targets"))
            .WithDescription(Text("Each winning card art source is exported once under generated/card_replace/cards and mapped back to its original source path."));

        section.AddParagraph(
            "coverage_counts",
            Text("Replacement count"),
            Text($"Generated images: {summary.ReplacementCount}; unique source paths: {summary.UniqueSourceCount}; runtime map entries: {summary.RuntimeMapCount}"),
            null);

        foreach (var sample in summary.ReplacementSamples.Select((item, index) => (item, index)))
        {
            section.AddParagraph(
                $"sample_{sample.index}",
                Text(sample.item.SourcePath),
                Text($"{sample.item.PackId} -> {sample.item.StagedPath}; card id: {sample.item.CardId}"),
                null);
        }
    }

    private static void BuildConflictsSection(ModSettingsSectionBuilder section, BuildSummary summary)
    {
        section
            .WithTitle(Text("Conflict Resolution"))
            .WithDescription(Text("When multiple packs replace the same source path, the highest priority pack wins."));

        section.AddParagraph(
            "conflict_count",
            Text("Conflict count"),
            Text(summary.ConflictCount.ToString()),
            null);

        foreach (var conflict in summary.ConflictSamples.Select((item, index) => (item, index)))
        {
            section.AddParagraph(
                $"conflict_{conflict.index}",
                Text(conflict.item.SourcePath),
                Text($"Winner: {conflict.item.WinnerPackId} (priority {conflict.item.WinnerPriority}); overridden: {conflict.item.OverriddenPackIds}"),
                null);
        }
    }

    private static ModSettingsText Text(string value)
    {
        return ModSettingsText.Literal(value);
    }

    private static BuildSummary ReadBuildSummary(string modRoot, IModLog log)
    {
        var manifestPath = Path.Combine(modRoot, FinalManifestFileName);
        var conflictsPath = Path.Combine(modRoot, ConflictReportFileName);
        var summary = new BuildSummary();

        try
        {
            if (File.Exists(manifestPath))
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (manifest.RootElement.TryGetProperty("enabled_pack_count", out var packCount))
                {
                    summary.EnabledPackCount = packCount.GetInt32();
                }

                if (manifest.RootElement.TryGetProperty("replacement_count", out var replacementCount))
                {
                    summary.ReplacementCount = replacementCount.GetInt32();
                }

                if (manifest.RootElement.TryGetProperty("generated_at", out var generatedAt))
                {
                    summary.GeneratedAt = generatedAt.GetString() ?? "";
                }

                if (manifest.RootElement.TryGetProperty("packs", out var packs) && packs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pack in packs.EnumerateArray())
                    {
                        var path = GetString(pack, "path");
                        summary.Packs.Add(new PackSummary(
                            GetString(pack, "id"),
                            path,
                            GetInt32(pack, "priority"),
                            GetFileSequence(path)));
                    }
                }

                if (manifest.RootElement.TryGetProperty("replacements", out var replacements)
                    && replacements.ValueKind == JsonValueKind.Array)
                {
                    var uniqueSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var packCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    foreach (var replacement in replacements.EnumerateArray())
                    {
                        var sourcePath = GetString(replacement, "source_path");
                        var stagedPath = GetString(replacement, "staged_path");
                        var cardId = GetString(replacement, "card_id");
                        var packId = GetString(replacement, "pack_id");

                        if (!string.IsNullOrWhiteSpace(sourcePath))
                        {
                            uniqueSources.Add(sourcePath);
                        }

                        if (!string.IsNullOrWhiteSpace(packId))
                        {
                            packCounts.TryGetValue(packId, out var count);
                            packCounts[packId] = count + 1;
                        }

                        if (summary.ReplacementSamples.Count < 12)
                        {
                            summary.ReplacementSamples.Add(new ReplacementSample(sourcePath, stagedPath, cardId, packId));
                        }
                    }

                    summary.UniqueSourceCount = uniqueSources.Count;
                    summary.RuntimeMapCount = summary.ReplacementCount;

                    foreach (var pack in summary.Packs)
                    {
                        if (packCounts.TryGetValue(pack.Id, out var count))
                        {
                            pack.ReplacementCount = count;
                        }
                    }
                }
            }

            if (File.Exists(conflictsPath))
            {
                using var conflicts = JsonDocument.Parse(File.ReadAllText(conflictsPath));
                if (conflicts.RootElement.TryGetProperty("conflict_count", out var conflictCount))
                {
                    summary.ConflictCount = conflictCount.GetInt32();
                }

                if (conflicts.RootElement.TryGetProperty("conflicts", out var conflictItems)
                    && conflictItems.ValueKind == JsonValueKind.Array)
                {
                    foreach (var conflict in conflictItems.EnumerateArray())
                    {
                        if (summary.ConflictSamples.Count >= 12)
                        {
                            break;
                        }

                        var sourcePath = GetString(conflict, "source_path");
                        var winner = conflict.TryGetProperty("winner", out var winnerElement)
                            ? winnerElement
                            : default;
                        var winnerPackId = winner.ValueKind == JsonValueKind.Object
                            ? GetString(winner, "PackId")
                            : "";
                        var winnerPriority = winner.ValueKind == JsonValueKind.Object
                            ? GetInt32(winner, "Priority")
                            : 0;
                        var overriddenPackIds = new List<string>();

                        if (conflict.TryGetProperty("overridden", out var overridden)
                            && overridden.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in overridden.EnumerateArray())
                            {
                                var overriddenPackId = GetString(item, "PackId");
                                if (!string.IsNullOrWhiteSpace(overriddenPackId))
                                {
                                    overriddenPackIds.Add(overriddenPackId);
                                }
                            }
                        }

                        summary.ConflictSamples.Add(new ConflictSample(
                            sourcePath,
                            winnerPackId,
                            winnerPriority,
                            string.Join(", ", overriddenPackIds.Distinct(StringComparer.OrdinalIgnoreCase))));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to read generated build reports: {ex.Message}");
        }

        return summary;
    }

    private static void LogBuildSummary(string pckPath, bool pckLoaded, BuildSummary summary, IModLog log)
    {
        log.Info(
            $"{Entry.ModId}: report pckLoaded={pckLoaded}, pck={pckPath}, generated={summary.GeneratedAt}, " +
            $"packs={summary.EnabledPackCount}, targets={summary.ReplacementCount}, uniqueSources={summary.UniqueSourceCount}, " +
            $"runtimeMapEntries={summary.RuntimeMapCount}, conflicts={summary.ConflictCount}");

        foreach (var pack in summary.Packs.OrderByDescending(pack => pack.Priority).ThenBy(pack => pack.Sequence ?? int.MaxValue))
        {
            var sequence = pack.Sequence is null ? "unknown" : pack.Sequence.Value.ToString();
            log.Info(
                $"{Entry.ModId}: input pack sequence={sequence}, priority={pack.Priority}, targets={pack.ReplacementCount}, " +
                $"id={pack.Id}, path={pack.Path}");
        }
    }

    private static string FormatFileSize(string path)
    {
        if (!File.Exists(path))
        {
            return "missing";
        }

        var bytes = new FileInfo(path).Length;
        return $"{bytes / 1024d / 1024d:0.0} MB";
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
    }

    private static int? GetFileSequence(string path)
    {
        var fileName = Path.GetFileName(path);
        var dashIndex = fileName.IndexOf('-');
        if (dashIndex <= 0)
        {
            return null;
        }

        return int.TryParse(fileName[..dashIndex], out var sequence) ? sequence : null;
    }

    private sealed class BuildSummary
    {
        public string GeneratedAt { get; set; } = "";

        public int EnabledPackCount { get; set; }

        public int ReplacementCount { get; set; }

        public int UniqueSourceCount { get; set; }

        public int RuntimeMapCount { get; set; }

        public int ConflictCount { get; set; }

        public List<PackSummary> Packs { get; } = [];

        public List<ReplacementSample> ReplacementSamples { get; } = [];

        public List<ConflictSample> ConflictSamples { get; } = [];
    }

    private sealed record PackSummary(string Id, string Path, int Priority, int? Sequence)
    {
        public int ReplacementCount { get; set; }
    }

    private sealed record ReplacementSample(string SourcePath, string StagedPath, string CardId, string PackId);

    private sealed record ConflictSample(string SourcePath, string WinnerPackId, int WinnerPriority, string OverriddenPackIds);
}
