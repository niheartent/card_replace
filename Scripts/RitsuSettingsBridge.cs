using System.Text.Json;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils;

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
            .WithDescription(Text(
                "View the generated static card art pack status, input sources, final replacement count, and conflict results.",
                "查看当前静态卡面包的加载状态、输入来源、最终替换数量和冲突处理结果。"))
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
            .WithTitle(Text("Build Result", "生成结果"))
            .WithDescription(Text(
                "This is a read-only status page. After changing inputs or priorities, rerun the Builder and restart the game.",
                "这里是只读信息页。修改输入包或优先级后，需要重新运行 Builder 并重启游戏。"));

        section.AddParagraph(
            "pck_status",
            Text(pckLoaded ? "PCK loaded" : "PCK not loaded", pckLoaded ? "PCK 已加载" : "PCK 未加载"),
            Text($"{pckPath} ({FormatFileSize(pckPath)})"),
            null);

        section.AddParagraph(
            "build_summary",
            Text("Build Summary", "构建摘要"),
            Text(
                $"Generated at: {summary.GeneratedAt}; inputs: {summary.EnabledPackCount}; " +
                $"final replacements: {summary.ReplacementCount}; unique card art: {summary.UniqueSourceCount}; conflicts: {summary.ConflictCount}",
                $"生成时间: {summary.GeneratedAt}; 输入: {summary.EnabledPackCount}; " +
                $"最终替换: {summary.ReplacementCount}; 唯一卡面: {summary.UniqueSourceCount}; 冲突: {summary.ConflictCount}"),
            null);

        section.AddParagraph(
            "load_rule",
            Text("Runtime Behavior", "运行方式"),
            Text(
                "The game loads the generated PCK and replacement map on startup. It does not read external art packs or recalculate priorities at runtime.",
                "游戏启动时加载已经生成好的 PCK 和 replacement map；运行时不读取外部素材包，不重新计算优先级。"),
            null);
    }

    private static void BuildPacksSection(ModSettingsSectionBuilder section, BuildSummary summary)
    {
        section
            .WithTitle(Text("Input Sources", "输入来源"))
            .WithDescription(Text(
                "Higher priority wins. If you sort by filename prefixes, 1-* is usually configured with the highest priority.",
                "数字越大的 priority 越优先。若用文件名前缀排序，通常 1-* 会被配置成最高 priority。"));

        if (summary.Packs.Count == 0)
        {
            section.AddParagraph(
                "packs_empty",
                Text("No input report", "没有输入报告"),
                Text("manifest.final.cardreplace was not found or could not be parsed.", "未找到或无法解析 manifest.final.cardreplace。"),
                null);
            return;
        }

        var rank = 1;
        foreach (var pack in summary.Packs.OrderByDescending(pack => pack.Priority).ThenBy(pack => pack.Sequence ?? int.MaxValue))
        {
            var currentRank = rank;
            section.AddParagraph(
                $"pack_{currentRank}",
                Text($"#{currentRank}: {pack.Id}"),
                Text(
                    $"priority: {pack.Priority}; final wins: {pack.ReplacementCount}; source: {pack.Path}",
                    $"priority: {pack.Priority}; 最终胜出: {pack.ReplacementCount}; 来源: {pack.Path}"),
                null);
            rank++;
        }
    }

    private static void BuildCoverageSection(ModSettingsSectionBuilder section, BuildSummary summary)
    {
        section
            .WithTitle(Text("Final Replacements", "最终替换"))
            .WithDescription(Text(
                "These are the card art entries that made it into the final replacement map. Only a small sample is shown.",
                "这里展示最终进入 replacement map 的卡面。列表只显示部分样例。"));

        section.AddParagraph(
            "coverage_counts",
            Text("Replacement Counts", "替换数量"),
            Text(
                $"final replacements: {summary.ReplacementCount}; unique card art: {summary.UniqueSourceCount}; runtime map entries: {summary.RuntimeMapCount}",
                $"最终替换: {summary.ReplacementCount}; 唯一卡面: {summary.UniqueSourceCount}; 运行时映射: {summary.RuntimeMapCount}"),
            null);

        foreach (var sample in summary.ReplacementSamples.Select((item, index) => (item, index)))
        {
            var title = string.IsNullOrWhiteSpace(sample.item.CardId) ? sample.item.SourcePath : sample.item.CardId;
            section.AddParagraph(
                $"sample_{sample.index}",
                Text(title),
                Text(
                    $"{sample.item.PackId} -> {sample.item.Image}; original path: {sample.item.SourcePath}",
                    $"{sample.item.PackId} -> {sample.item.Image}; 原始路径: {sample.item.SourcePath}"),
                null);
        }
    }

    private static void BuildConflictsSection(ModSettingsSectionBuilder section, BuildSummary summary)
    {
        section
            .WithTitle(Text("Conflicts", "冲突处理"))
            .WithDescription(Text(
                "When multiple sources replace the same card, the highest priority source wins. Only a small sample is shown.",
                "多个来源替换同一张卡时，priority 最高的来源胜出。列表只显示部分冲突样例。"));

        section.AddParagraph(
            "conflict_count",
            Text("Conflict Count", "冲突数量"),
            Text(summary.ConflictCount.ToString()),
            null);

        foreach (var conflict in summary.ConflictSamples.Select((item, index) => (item, index)))
        {
            var title = string.IsNullOrWhiteSpace(conflict.item.CardId) ? conflict.item.SourcePath : conflict.item.CardId;
            section.AddParagraph(
                $"conflict_{conflict.index}",
                Text(title),
                Text(
                    $"winner: {conflict.item.WinnerPackId} (priority {conflict.item.WinnerPriority}); overridden: {conflict.item.OverriddenPackIds}",
                    $"胜出: {conflict.item.WinnerPackId} (priority {conflict.item.WinnerPriority}); 被覆盖: {conflict.item.OverriddenPackIds}"),
                null);
        }
    }

    private static ModSettingsText Text(string value)
    {
        return ModSettingsText.Literal(value);
    }

    private static ModSettingsText Text(string english, string chinese)
    {
        return ModSettingsText.Dynamic(() => IsChineseLanguage() ? chinese : english);
    }

    private static bool IsChineseLanguage()
    {
        var language = I18N.ResolveCurrentLanguageCode();
        return language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("zho", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("chi", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("cmn", StringComparison.OrdinalIgnoreCase)
            || language.Equals("chs", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zhs", StringComparison.OrdinalIgnoreCase)
            || language.Contains("hans", StringComparison.OrdinalIgnoreCase)
            || language.Contains("hant", StringComparison.OrdinalIgnoreCase);
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
                        var image = GetString(replacement, "image");
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
                            summary.ReplacementSamples.Add(new ReplacementSample(sourcePath, image, cardId, packId));
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
                        var cardId = GetString(conflict, "card_id");
                        var winner = conflict.TryGetProperty("winner", out var winnerElement)
                            ? winnerElement
                            : default;
                        var winnerPackId = winner.ValueKind == JsonValueKind.Object
                            ? FirstNonEmpty(GetString(winner, "pack_id"), GetString(winner, "PackId"))
                            : "";
                        var winnerPriority = winner.ValueKind == JsonValueKind.Object
                            ? FirstNonZero(GetInt32(winner, "priority"), GetInt32(winner, "Priority"))
                            : 0;
                        var overriddenPackIds = new List<string>();

                        if (conflict.TryGetProperty("overridden", out var overridden)
                            && overridden.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in overridden.EnumerateArray())
                            {
                                var overriddenPackId = FirstNonEmpty(GetString(item, "pack_id"), GetString(item, "PackId"));
                                if (!string.IsNullOrWhiteSpace(overriddenPackId))
                                {
                                    overriddenPackIds.Add(overriddenPackId);
                                }
                            }
                        }

                        summary.ConflictSamples.Add(new ConflictSample(
                            sourcePath,
                            cardId,
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

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static int FirstNonZero(params int[] values)
    {
        return values.FirstOrDefault(value => value != 0);
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

    private sealed record ReplacementSample(string SourcePath, string Image, string CardId, string PackId);

    private sealed record ConflictSample(string SourcePath, string CardId, string WinnerPackId, int WinnerPriority, string OverriddenPackIds);
}
