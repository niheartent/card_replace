using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CardReplaceBuilder;

public static class Program
{
    private const string DefaultPckName = "card_replace.pck";
    private const string FinalManifestName = "manifest.final.cardreplace";
    private const string ConflictReportName = "conflicts.report.cardreplace";
    private const string ReplacementMapPath = "generated/card_replace/card_replacements.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        var configPath = args.Length > 0 ? args[0] : "build_config.json";
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Config not found: {Path.GetFullPath(configPath)}");
            Console.Error.WriteLine("Create one from build_config.example.json, then run:");
            Console.Error.WriteLine("  dotnet run --project tools/CardReplaceBuilder -- build_config.json");
            return 2;
        }

        try
        {
            var config = LoadConfig(configPath);
            var result = Build(config, Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Directory.GetCurrentDirectory());
            Console.WriteLine($"Built {result.ReplacementCount} replacement(s).");
            Console.WriteLine($"PCK: {result.PckPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static BuildConfig LoadConfig(string configPath)
    {
        var config = JsonSerializer.Deserialize<BuildConfig>(File.ReadAllText(configPath), JsonOptions)
            ?? throw new InvalidOperationException($"Invalid config: {configPath}");

        if (config.Packs.Count == 0)
        {
            throw new InvalidOperationException("Config must contain at least one .cardartpack.json input.");
        }

        return config;
    }

    private static BuildResult Build(BuildConfig config, string configRoot)
    {
        var outputRoot = ResolvePath(configRoot, config.OutputRoot);
        var stagingRoot = ResolvePath(configRoot, config.StagingRoot);
        var pckPath = ResolvePath(outputRoot, string.IsNullOrWhiteSpace(config.PckName) ? DefaultPckName : config.PckName);
        var enabledPacks = config.Packs
            .Where(pack => pack.Enabled)
            .Select((pack, index) => PackContext.Create(pack, index, configRoot))
            .OrderBy(pack => pack.Priority)
            .ThenBy(pack => pack.Order)
            .ToList();

        if (enabledPacks.Count == 0)
        {
            throw new InvalidOperationException("No enabled input packs.");
        }

        Directory.CreateDirectory(outputRoot);
        ResetDirectory(stagingRoot);
        WriteGodotProjectFiles(stagingRoot);

        var winners = ResolveWinners(enabledPacks, out var conflicts);
        var replacements = ExtractWinningAssets(enabledPacks, winners, stagingRoot);
        WriteJson(
            Path.Combine(stagingRoot, ReplacementMapPath.Replace('/', Path.DirectorySeparatorChar)),
            new ReplacementDocument(
                replacements
                    .Select(item => new RuntimeReplacementEntry(item.CardId, item.SourcePath, $"res://{item.StagedPath}"))
                    .OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ToList()));

        var finalManifest = new FinalManifest(
            DateTimeOffset.Now,
            enabledPacks.Count,
            replacements.Count,
            enabledPacks.Select(pack => new FinalPack(pack.Id, pack.Path, pack.Priority)).ToList(),
            replacements.OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase).ToList());

        var conflictReport = new ConflictReport(
            conflicts.Count,
            conflicts.Values.OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase).ToList());

        WriteJson(Path.Combine(outputRoot, FinalManifestName), finalManifest);
        WriteJson(Path.Combine(outputRoot, ConflictReportName), conflictReport);

        if (!string.IsNullOrWhiteSpace(config.GodotExe))
        {
            ExportPck(config.GodotExe, stagingRoot, pckPath);
        }
        else
        {
            Console.WriteLine("godot_exe is empty; staged resources were written but no PCK was exported.");
        }

        if (!string.IsNullOrWhiteSpace(config.OutputModDir))
        {
            CopyModOutputs(config, configRoot, outputRoot, ResolvePath(configRoot, config.OutputModDir), pckPath);
        }

        return new BuildResult(pckPath, replacements.Count);
    }

    private static Dictionary<string, Winner> ResolveWinners(List<PackContext> packs, out Dictionary<string, ConflictEntry> conflicts)
    {
        var winners = new Dictionary<string, Winner>(StringComparer.OrdinalIgnoreCase);
        conflicts = new Dictionary<string, ConflictEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in packs)
        {
            foreach (var item in ReadOverrideIndex(pack))
            {
                if (!IsSupportedSourcePath(item.SourcePath))
                {
                    continue;
                }

                var candidate = new Winner(pack.Id, pack.Path, pack.Priority, item.Type, item.SourcePath);
                if (winners.TryGetValue(item.SourcePath, out var previous))
                {
                    if (!conflicts.TryGetValue(item.SourcePath, out var conflict))
                    {
                        conflict = new ConflictEntry(item.SourcePath, previous);
                        conflicts[item.SourcePath] = conflict;
                    }

                    conflict.Overridden.Add(previous);
                    conflict.Winner = candidate;
                }

                winners[item.SourcePath] = candidate;
            }
        }

        return winners;
    }

    private static List<ReplacementEntry> ExtractWinningAssets(
        List<PackContext> packs,
        Dictionary<string, Winner> winners,
        string stagingRoot)
    {
        var replacements = new List<ReplacementEntry>();

        foreach (var pack in packs)
        {
            var packWinnerSources = winners.Values
                .Where(winner => string.Equals(winner.PackId, pack.Id, StringComparison.OrdinalIgnoreCase))
                .Select(winner => winner.SourcePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (packWinnerSources.Count == 0)
            {
                continue;
            }

            foreach (var item in ReadOverrideData(pack))
            {
                if (!packWinnerSources.Contains(item.SourcePath))
                {
                    continue;
                }

                var base64 = FirstNonEmpty(item.PngBase64, item.EditSourcePngBase64);
                if (string.IsNullOrWhiteSpace(base64))
                {
                    Console.WriteLine($"Skipped {item.SourcePath}: no png_base64 or edit_source_png_base64 in {pack.Path}");
                    continue;
                }

                var relativePath = ToGeneratedCardRelativePath(item.SourcePath);
                var outputPath = Path.Combine(stagingRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllBytes(outputPath, Convert.FromBase64String(base64));

                replacements.Add(new ReplacementEntry(
                    item.SourcePath,
                    relativePath.Replace('\\', '/'),
                    ToCardId(item.SourcePath),
                    pack.Id,
                    pack.Priority,
                    item.Type));
            }
        }

        return replacements;
    }

    private static IEnumerable<OverrideIndex> ReadOverrideIndex(PackContext pack)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(pack.Path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (!doc.RootElement.TryGetProperty("overrides", out var overrides) || overrides.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in overrides.EnumerateArray())
        {
            var sourcePath = GetString(item, "source_path");
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            yield return new OverrideIndex(sourcePath, GetString(item, "type") ?? "static");
        }
    }

    private static IEnumerable<OverrideData> ReadOverrideData(PackContext pack)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(pack.Path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (!doc.RootElement.TryGetProperty("overrides", out var overrides) || overrides.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in overrides.EnumerateArray())
        {
            var sourcePath = GetString(item, "source_path");
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            yield return new OverrideData(
                sourcePath,
                GetString(item, "type") ?? "static",
                GetString(item, "png_base64"),
                GetString(item, "edit_source_png_base64"));
        }
    }

    private static void ExportPck(string godotExe, string stagingRoot, string pckPath)
    {
        if (!File.Exists(godotExe))
        {
            throw new FileNotFoundException($"Godot executable not found: {godotExe}");
        }

        RunProcess(godotExe, $"--headless --path \"{stagingRoot}\" --import", stagingRoot);
        RunProcess(godotExe, $"--headless --path \"{stagingRoot}\" --export-pack \"Card Replace PCK\" \"{pckPath}\"", stagingRoot);
    }

    private static void RunProcess(string exe, string arguments, string workingDirectory)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start process: {exe}");
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Console.WriteLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Console.Error.WriteLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Process failed with exit code {process.ExitCode}: {exe} {arguments}");
        }
    }

    private static void CopyModOutputs(
        BuildConfig config,
        string configRoot,
        string outputRoot,
        string outputModDir,
        string pckPath)
    {
        var loaderDllPath = ResolvePath(configRoot, config.LoaderDllPath);
        var modManifestPath = ResolvePath(configRoot, config.ModManifestPath);

        RequireFile(loaderDllPath, "Loader DLL was not found. Build the loader first with: dotnet build .\\card_replace.csproj -p:SkipModDeploy=true");
        RequireFile(modManifestPath, "Mod manifest was not found.");
        RequireFile(pckPath, "Generated PCK was not found.");

        ResetDirectory(outputModDir);
        File.Copy(loaderDllPath, Path.Combine(outputModDir, Path.GetFileName(loaderDllPath)), overwrite: true);
        File.Copy(modManifestPath, Path.Combine(outputModDir, Path.GetFileName(modManifestPath)), overwrite: true);
        File.Copy(pckPath, Path.Combine(outputModDir, Path.GetFileName(pckPath)), overwrite: true);
        CopyIfExists(Path.Combine(outputRoot, FinalManifestName), Path.Combine(outputModDir, FinalManifestName));
        CopyIfExists(Path.Combine(outputRoot, ConflictReportName), Path.Combine(outputModDir, ConflictReportName));
        Console.WriteLine($"Generated mod folder: {outputModDir}");
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static void RequireFile(string path, string message)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{message} Path: {path}");
        }
    }

    private static void WriteGodotProjectFiles(string stagingRoot)
    {
        File.WriteAllText(
            Path.Combine(stagingRoot, "project.godot"),
            """
            ; Generated by CardReplaceBuilder.
            config_version=5

            [application]

            config/name="Card Replace Generated Pack"
            config/features=PackedStringArray("4.5")
            """);

        File.WriteAllText(
            Path.Combine(stagingRoot, "export_presets.cfg"),
            """
            [preset.0]

            name="Card Replace PCK"
            platform="Windows Desktop"
            runnable=false
            dedicated_server=false
            custom_features=""
            export_filter="all_resources"
            include_filter=""
            exclude_filter=""
            export_path="card_replace.pck"
            encryption_include_filters=""
            encryption_exclude_filters=""
            encrypt_pck=false
            encrypt_directory=false

            [preset.0.options]

            custom_template/debug=""
            custom_template/release=""
            debug/export_console_wrapper=1
            binary_format/embed_pck=false
            texture_format/bptc=true
            texture_format/s3tc=true
            texture_format/etc=false
            texture_format/etc2=false
            """);
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static bool IsSupportedSourcePath(string sourcePath)
    {
        return sourcePath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            && sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToGeneratedCardRelativePath(string sourcePath)
    {
        const string prefix = "res://images/packed/card_portraits/";
        var tail = sourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? sourcePath[prefix.Length..]
            : sourcePath["res://".Length..];
        return Path.Combine("generated", "card_replace", "cards", tail.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ToCardId(string sourcePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        return string.IsNullOrWhiteSpace(fileName)
            ? ""
            : $"MegaCrit.Sts2.Core.Models.Cards.{ToCardModelName(fileName)}";
    }

    private static string ToCardModelName(string fileName)
    {
        var words = fileName
            .Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Concat(words.Select(ToCardModelWord));
    }

    private static string ToCardModelWord(string word)
    {
        return word.ToLowerInvariant() switch
        {
            "ai" => "AI",
            "ftl" => "FTL",
            _ => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
        };
    }

    private static string ResolvePath(string root, string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}

public sealed class BuildConfig
{
    [JsonPropertyName("godot_exe")]
    public string GodotExe { get; set; } = "";

    [JsonPropertyName("output_root")]
    public string OutputRoot { get; set; } = "build/generated";

    [JsonPropertyName("output_mod_dir")]
    public string OutputModDir { get; set; } = "dist/card_replace";

    [JsonPropertyName("loader_dll_path")]
    public string LoaderDllPath { get; set; } = ".godot/mono/temp/bin/Debug/card_replace.dll";

    [JsonPropertyName("mod_manifest_path")]
    public string ModManifestPath { get; set; } = "card_replace.json";

    [JsonPropertyName("staging_root")]
    public string StagingRoot { get; set; } = "build/staging_godot_project";

    [JsonPropertyName("pck_name")]
    public string PckName { get; set; } = "card_replace.pck";

    [JsonPropertyName("packs")]
    public List<InputPack> Packs { get; set; } = [];
}

public sealed class InputPack
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 100;

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

public sealed record PackContext(string Id, string Path, int Priority, int Order)
{
    public static PackContext Create(InputPack pack, int order, string configRoot)
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(pack.Path)
            ? pack.Path
            : System.IO.Path.Combine(configRoot, pack.Path));

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Input .cardartpack.json not found: {path}");
        }

        var id = string.IsNullOrWhiteSpace(pack.Id)
            ? System.IO.Path.GetFileNameWithoutExtension(System.IO.Path.GetFileNameWithoutExtension(path))
            : pack.Id.Trim();

        return new PackContext(id, path, pack.Priority, order);
    }
}

public sealed record OverrideIndex(string SourcePath, string Type);

public sealed record OverrideData(string SourcePath, string Type, string? PngBase64, string? EditSourcePngBase64);

public sealed record Winner(string PackId, string PackPath, int Priority, string Type, string SourcePath);

public sealed record ReplacementEntry(
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("staged_path")] string StagedPath,
    [property: JsonPropertyName("card_id")] string CardId,
    [property: JsonPropertyName("pack_id")] string PackId,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("type")] string Type);

public sealed record ReplacementDocument(
    [property: JsonPropertyName("entries")] List<RuntimeReplacementEntry> Entries);

public sealed record RuntimeReplacementEntry(
    [property: JsonPropertyName("cardId")] string CardId,
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("image")] string Image);

public sealed class ConflictEntry
{
    public ConflictEntry(string sourcePath, Winner winner)
    {
        SourcePath = sourcePath;
        Winner = winner;
    }

    [JsonPropertyName("source_path")]
    public string SourcePath { get; }

    [JsonPropertyName("winner")]
    public Winner Winner { get; set; }

    [JsonPropertyName("overridden")]
    public List<Winner> Overridden { get; } = [];
}

public sealed record FinalPack(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("priority")] int Priority);

public sealed record FinalManifest(
    [property: JsonPropertyName("generated_at")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("enabled_pack_count")] int EnabledPackCount,
    [property: JsonPropertyName("replacement_count")] int ReplacementCount,
    [property: JsonPropertyName("packs")] List<FinalPack> Packs,
    [property: JsonPropertyName("replacements")] List<ReplacementEntry> Replacements);

public sealed record ConflictReport(
    [property: JsonPropertyName("conflict_count")] int ConflictCount,
    [property: JsonPropertyName("conflicts")] List<ConflictEntry> Conflicts);

public sealed record BuildResult(string PckPath, int ReplacementCount);
