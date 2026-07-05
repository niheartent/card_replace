using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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

        return config;
    }

    private static BuildResult Build(BuildConfig config, string configRoot)
    {
        var outputRoot = ResolvePath(configRoot, config.OutputRoot);
        var stagingRoot = ResolvePath(configRoot, config.StagingRoot);
        var pckPath = ResolvePath(outputRoot, string.IsNullOrWhiteSpace(config.PckName) ? DefaultPckName : config.PckName);

        Directory.CreateDirectory(outputRoot);
        ResetDirectory(stagingRoot);
        WriteGodotProjectFiles(stagingRoot);

        var inputContexts = ResolveInputContexts(config, configRoot)
            .Where(input => input.Enabled)
            .OrderBy(input => input.Priority)
            .ThenBy(input => input.Order)
            .ToList();

        if (inputContexts.Count == 0)
        {
            throw new InvalidOperationException("No enabled inputs.");
        }

        var candidates = LoadCandidates(inputContexts, stagingRoot);
        var winners = ResolveWinners(candidates, out var conflicts);
        var replacements = StageWinningAssets(winners.Values.ToList(), stagingRoot);

        WriteJson(
            Path.Combine(stagingRoot, ReplacementMapPath.Replace('/', Path.DirectorySeparatorChar)),
            new ReplacementDocument(
                replacements
                    .Select(item => new RuntimeReplacementEntry(item.CardId, item.SourcePath, item.Image))
                    .OrderBy(item => item.CardId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ToList()));

        var finalManifest = new FinalManifest(
            DateTimeOffset.Now,
            inputContexts.Count,
            replacements.Count,
            inputContexts.Select(input => new FinalPack(input.Id, input.Path, input.Priority, input.Kind.ToString())).ToList(),
            replacements.OrderBy(item => item.CardId, StringComparer.OrdinalIgnoreCase).ToList());

        var conflictReport = new ConflictReport(
            conflicts.Count,
            conflicts.Values.OrderBy(item => item.ConflictKey, StringComparer.OrdinalIgnoreCase).ToList());

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

    private static List<InputContext> ResolveInputContexts(BuildConfig config, string configRoot)
    {
        var inputs = new List<InputSource>();
        inputs.AddRange(ParseInputs(config.Inputs));
        inputs.AddRange(config.Packs);

        return inputs
            .Select((input, order) => InputContext.Create(input, order, configRoot))
            .ToList();
    }

    private static IEnumerable<InputSource> ParseInputs(JsonElement inputs)
    {
        if (inputs.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            yield break;
        }

        if (inputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in inputs.EnumerateArray())
            {
                var input = item.Deserialize<InputSource>(JsonOptions);
                if (input is not null)
                {
                    yield return input;
                }
            }

            yield break;
        }

        if (inputs.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("inputs must be either an array or an object.");
        }

        foreach (var property in inputs.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                yield return new InputSource
                {
                    Path = property.Name,
                    Priority = property.Value.GetInt32()
                };
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var input = property.Value.Deserialize<InputSource>(JsonOptions) ?? new InputSource();
                input.Path = string.IsNullOrWhiteSpace(input.Path) ? property.Name : input.Path;
                yield return input;
            }
        }
    }

    private static List<ReplacementCandidate> LoadCandidates(List<InputContext> inputs, string stagingRoot)
    {
        var candidates = new List<ReplacementCandidate>();
        var zipRoot = Path.Combine(stagingRoot, "_input_zips");

        foreach (var input in inputs)
        {
            switch (input.Kind)
            {
                case InputKind.CardArtPackJson:
                    candidates.AddRange(LoadCardArtPackCandidates(input));
                    break;
                case InputKind.ModFolder:
                    candidates.AddRange(LoadModFolderCandidates(input));
                    break;
                case InputKind.PckFile:
                    candidates.AddRange(LoadPckCandidates(input, input.Path));
                    break;
                case InputKind.Zip:
                    candidates.AddRange(LoadZipCandidates(input, zipRoot));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(input.Kind), input.Kind, "Unsupported input kind.");
            }
        }

        Console.WriteLine($"Loaded {candidates.Count} replacement candidate(s) from {inputs.Count} input(s).");
        return candidates;
    }

    private static IEnumerable<ReplacementCandidate> LoadCardArtPackCandidates(InputContext input)
    {
        foreach (var item in ReadCardArtPackData(input.Path))
        {
            if (!IsSupportedSourcePath(item.SourcePath))
            {
                continue;
            }

            var base64 = FirstNonEmpty(item.PngBase64, item.EditSourcePngBase64);
            if (string.IsNullOrWhiteSpace(base64))
            {
                Console.WriteLine($"Skipped {item.SourcePath}: no png_base64 or edit_source_png_base64 in {input.Path}");
                continue;
            }

            var cardId = ToCardId(item.SourcePath);
            yield return new ReplacementCandidate(
                ConflictKey(cardId, item.SourcePath),
                item.SourcePath,
                cardId,
                input,
                item.Type,
                new CardArtPackAsset(item.SourcePath, base64));
        }
    }

    private static IEnumerable<ReplacementCandidate> LoadModFolderCandidates(InputContext input)
    {
        foreach (var pckPath in Directory.GetFiles(input.Path, "*.pck", SearchOption.TopDirectoryOnly))
        {
            foreach (var candidate in LoadPckCandidates(input, pckPath))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<ReplacementCandidate> LoadZipCandidates(InputContext input, string zipRoot)
    {
        var extractRoot = Path.Combine(zipRoot, SanitizeFileName(input.Id));
        ResetDirectory(extractRoot);
        ZipFile.ExtractToDirectory(input.Path, extractRoot, overwriteFiles: true);

        foreach (var pckPath in Directory.GetFiles(extractRoot, "*.pck", SearchOption.AllDirectories))
        {
            foreach (var candidate in LoadPckCandidates(input, pckPath))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<ReplacementCandidate> LoadPckCandidates(InputContext input, string pckPath)
    {
        var pck = GodotPck.Read(pckPath);
        var entriesByPath = pck.Entries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var importEntry in pck.Entries.Where(entry => IsCardArtImportPath(entry.Path)))
        {
            var imagePath = importEntry.Path[..^".import".Length];
            var cardId = CardIdFromImportedCardArtPath(imagePath);
            if (string.IsNullOrWhiteSpace(cardId))
            {
                continue;
            }

            var importText = Encoding.UTF8.GetString(pck.ReadEntry(importEntry));
            var ctexPath = ParseImportedTexturePath(importText);
            if (string.IsNullOrWhiteSpace(ctexPath)
                || !entriesByPath.TryGetValue(ctexPath["res://".Length..], out var ctexEntry))
            {
                Console.WriteLine($"Skipped {importEntry.Path}: referenced ctex was not found in {pckPath}");
                continue;
            }

            count++;
            var sourcePath = $"res://{imagePath.Replace('\\', '/')}";
            yield return new ReplacementCandidate(
                ConflictKey(cardId, sourcePath),
                sourcePath,
                cardId,
                input with { Path = pckPath },
                "pck_imported",
                new PckImportedAsset(pckPath, importEntry, ctexEntry, sourcePath));
        }

        foreach (var mapEntry in pck.Entries.Where(entry => IsPckReplacementMapPath(entry.Path)))
        {
            foreach (var candidate in LoadPckReplacementMapCandidates(input, pckPath, pck, entriesByPath, mapEntry))
            {
                count++;
                yield return candidate;
            }
        }

        Console.WriteLine($"Loaded {count} PCK card art candidate(s): {pckPath}");
    }

    private static IEnumerable<ReplacementCandidate> LoadPckReplacementMapCandidates(
        InputContext input,
        string pckPath,
        GodotPck pck,
        Dictionary<string, PckEntry> entriesByPath,
        PckEntry mapEntry)
    {
        PckReplacementDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<PckReplacementDocument>(
                Encoding.UTF8.GetString(pck.ReadEntry(mapEntry)),
                JsonOptions);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Skipped {mapEntry.Path}: invalid replacement map ({ex.Message})");
            yield break;
        }

        if (document?.NormalReplacements is null)
        {
            yield break;
        }

        foreach (var replacement in document.NormalReplacements)
        {
            if (string.IsNullOrWhiteSpace(replacement.CardType)
                || string.IsNullOrWhiteSpace(replacement.PortraitPath)
                || !replacement.PortraitPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var importPath = replacement.PortraitPath["res://".Length..] + ".import";
            if (!entriesByPath.TryGetValue(importPath, out var importEntry))
            {
                Console.WriteLine($"Skipped {replacement.PortraitPath}: import file was not found in {pckPath}");
                continue;
            }

            var importText = Encoding.UTF8.GetString(pck.ReadEntry(importEntry));
            var ctexPath = ParseImportedTexturePath(importText);
            if (string.IsNullOrWhiteSpace(ctexPath)
                || !entriesByPath.TryGetValue(ctexPath["res://".Length..], out var ctexEntry))
            {
                Console.WriteLine($"Skipped {replacement.PortraitPath}: referenced ctex was not found in {pckPath}");
                continue;
            }

            var cardId = $"MegaCrit.Sts2.Core.Models.Cards.{replacement.CardType}";
            yield return new ReplacementCandidate(
                ConflictKey(cardId, replacement.PortraitPath),
                replacement.PortraitPath,
                cardId,
                input with { Path = pckPath },
                "pck_replacement_map",
                new PckImportedAsset(pckPath, importEntry, ctexEntry, replacement.PortraitPath));
        }
    }

    private static Dictionary<string, ReplacementCandidate> ResolveWinners(
        List<ReplacementCandidate> candidates,
        out Dictionary<string, ConflictEntry> conflicts)
    {
        var winners = new Dictionary<string, ReplacementCandidate>(StringComparer.OrdinalIgnoreCase);
        conflicts = new Dictionary<string, ConflictEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates
                     .OrderBy(candidate => candidate.Input.Priority)
                     .ThenBy(candidate => candidate.Input.Order))
        {
            if (winners.TryGetValue(candidate.ConflictKey, out var previous))
            {
                if (!conflicts.TryGetValue(candidate.ConflictKey, out var conflict))
                {
                    conflict = new ConflictEntry(candidate.ConflictKey, previous);
                    conflicts[candidate.ConflictKey] = conflict;
                }

                conflict.Overridden.Add(CandidateSummary.From(previous));
                conflict.Winner = CandidateSummary.From(candidate);
            }

            winners[candidate.ConflictKey] = candidate;
        }

        return winners;
    }

    private static List<ReplacementEntry> StageWinningAssets(List<ReplacementCandidate> winners, string stagingRoot)
    {
        var replacements = new List<ReplacementEntry>();

        foreach (var winner in winners)
        {
            var stagedPath = winner.Asset.Stage(stagingRoot);
            replacements.Add(new ReplacementEntry(
                winner.ConflictKey,
                winner.SourcePath,
                stagedPath,
                winner.Asset.Image,
                winner.CardId,
                winner.Input.Id,
                winner.Input.Path,
                winner.Input.Priority,
                winner.Type,
                winner.Input.Kind.ToString()));
        }

        return replacements;
    }

    private static IEnumerable<CardArtPackOverrideData> ReadCardArtPackData(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
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

            yield return new CardArtPackOverrideData(
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
        IncludeStagedImportedResources(pckPath, stagingRoot);
    }

    private static void IncludeStagedImportedResources(string pckPath, string stagingRoot)
    {
        var extraFiles = Directory.EnumerateFiles(stagingRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".import", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ctex", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                FullPath = path,
                PckPath = Path.GetRelativePath(stagingRoot, path).Replace('\\', '/')
            })
            .ToList();

        if (extraFiles.Count == 0)
        {
            return;
        }

        var pck = GodotPck.Read(pckPath);
        var existing = pck.Entries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var entries = new List<PckWriteEntry>();

        foreach (var entry in pck.Entries)
        {
            entries.Add(new PckWriteEntry(entry.Path, pck.ReadEntry(entry), entry.Flags));
        }

        var added = 0;
        foreach (var extra in extraFiles)
        {
            if (existing.ContainsKey(extra.PckPath))
            {
                continue;
            }

            entries.Add(new PckWriteEntry(extra.PckPath, File.ReadAllBytes(extra.FullPath), 0));
            added++;
        }

        if (added == 0)
        {
            return;
        }

        var tempPath = pckPath + ".tmp";
        GodotPck.Write(tempPath, entries);
        File.Copy(tempPath, pckPath, overwrite: true);
        File.Delete(tempPath);
        Console.WriteLine($"Added {added} imported resource file(s) to PCK.");
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
            include_filter="*.import,*.ctex"
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

    private static bool IsCardArtImportPath(string path)
    {
        return path.EndsWith(".import", StringComparison.OrdinalIgnoreCase)
            && path.Contains("_card_art.", StringComparison.OrdinalIgnoreCase)
            && path.Contains("MegaCrit.Sts2.Core.Models.Cards.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPckReplacementMapPath(string path)
    {
        return path.EndsWith("/card_replacements.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "card_replacements.json", StringComparison.OrdinalIgnoreCase);
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

    private static string CardIdFromImportedCardArtPath(string imagePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(imagePath.Replace('\\', '/'));
        const string suffix = "_card_art";
        return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length]
            : "";
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

    private static string ConflictKey(string cardId, string sourcePath)
    {
        return !string.IsNullOrWhiteSpace(cardId) ? cardId : sourcePath;
    }

    private static string ParseImportedTexturePath(string importText)
    {
        const string marker = "path=\"";
        var index = importText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "";
        }

        var start = index + marker.Length;
        var end = importText.IndexOf('"', start);
        return end > start ? importText[start..end] : "";
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

    private static string SanitizeFileName(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "input" : sanitized;
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

    [JsonPropertyName("inputs")]
    public JsonElement Inputs { get; set; }

    [JsonPropertyName("packs")]
    public List<InputSource> Packs { get; set; } = [];
}

public sealed class InputSource
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 100;

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

public sealed record InputContext(
    string Id,
    string Path,
    int Priority,
    int Order,
    bool Enabled,
    InputKind Kind)
{
    public static InputContext Create(InputSource input, int order, string configRoot)
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(input.Path)
            ? input.Path
            : System.IO.Path.Combine(configRoot, input.Path));

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"Input was not found: {path}");
        }

        var kind = DetectKind(path, input.Type);
        var id = string.IsNullOrWhiteSpace(input.Id)
            ? DefaultId(path)
            : input.Id.Trim();

        return new InputContext(id, path, input.Priority, order, input.Enabled, kind);
    }

    private static InputKind DetectKind(string path, string type)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            return type.Trim().ToLowerInvariant() switch
            {
                "cardartpack" or "cardartpack_json" or "cardartpackjson" => InputKind.CardArtPackJson,
                "folder" or "mod_folder" or "modfolder" => InputKind.ModFolder,
                "zip" => InputKind.Zip,
                "pck" or "pck_file" or "pckfile" => InputKind.PckFile,
                _ => throw new InvalidOperationException($"Unknown input type: {type}")
            };
        }

        if (Directory.Exists(path))
        {
            return InputKind.ModFolder;
        }

        var extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".cardartpack.json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cardartpack.json", StringComparison.OrdinalIgnoreCase))
        {
            return InputKind.CardArtPackJson;
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return InputKind.Zip;
        }

        if (extension.Equals(".pck", StringComparison.OrdinalIgnoreCase))
        {
            return InputKind.PckFile;
        }

        throw new InvalidOperationException($"Could not infer input type: {path}");
    }

    private static string DefaultId(string path)
    {
        var name = Directory.Exists(path)
            ? new DirectoryInfo(path).Name
            : System.IO.Path.GetFileNameWithoutExtension(path);

        if (name.EndsWith(".cardartpack", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".cardartpack".Length];
        }

        return string.IsNullOrWhiteSpace(name) ? "input" : name;
    }
}

public enum InputKind
{
    CardArtPackJson,
    ModFolder,
    Zip,
    PckFile
}

public sealed record CardArtPackOverrideData(string SourcePath, string Type, string? PngBase64, string? EditSourcePngBase64);

public sealed record PckReplacementDocument(
    [property: JsonPropertyName("normalReplacements")] List<PckNormalReplacement> NormalReplacements);

public sealed record PckNormalReplacement(
    [property: JsonPropertyName("cardType")] string CardType,
    [property: JsonPropertyName("portraitPath")] string PortraitPath);

public sealed record ReplacementCandidate(
    string ConflictKey,
    string SourcePath,
    string CardId,
    InputContext Input,
    string Type,
    IReplacementAsset Asset);

public interface IReplacementAsset
{
    string Image { get; }

    string Stage(string stagingRoot);
}

public sealed record CardArtPackAsset(string SourcePath, string Base64) : IReplacementAsset
{
    public string Image => $"res://{StagedPath.Replace('\\', '/')}";

    private string StagedPath => ProgramPrivate.ToGeneratedCardRelativePath(SourcePath);

    public string Stage(string stagingRoot)
    {
        var outputPath = Path.Combine(stagingRoot, StagedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, Convert.FromBase64String(Base64));
        return StagedPath.Replace('\\', '/');
    }
}

public sealed record PckImportedAsset(string PckPath, PckEntry ImportEntry, PckEntry CtexEntry, string Image) : IReplacementAsset
{
    public string Stage(string stagingRoot)
    {
        var pck = GodotPck.Read(PckPath);
        pck.ExtractEntry(ImportEntry.Path, Path.Combine(stagingRoot, ImportEntry.Path.Replace('/', Path.DirectorySeparatorChar)));
        pck.ExtractEntry(CtexEntry.Path, Path.Combine(stagingRoot, CtexEntry.Path.Replace('/', Path.DirectorySeparatorChar)));
        return ImportEntry.Path[..^".import".Length].Replace('\\', '/');
    }
}

public static class ProgramPrivate
{
    public static string ToGeneratedCardRelativePath(string sourcePath)
    {
        const string prefix = "res://images/packed/card_portraits/";
        var tail = sourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? sourcePath[prefix.Length..]
            : sourcePath["res://".Length..];
        return Path.Combine("generated", "card_replace", "cards", tail.Replace('/', Path.DirectorySeparatorChar));
    }
}

public sealed class GodotPck
{
    private const int FileBaseOffset = 112;

    private readonly long _fileBase;

    private GodotPck(string path, long fileBase, List<PckEntry> entries)
    {
        Path = path;
        _fileBase = fileBase;
        Entries = entries;
    }

    public string Path { get; }

    public List<PckEntry> Entries { get; }

    public static GodotPck Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (!string.Equals(magic, "GDPC", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Not a Godot PCK file: {path}");
        }

        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        var fileBase = reader.ReadInt64();
        var directoryOffset = reader.ReadInt64();

        stream.Seek(directoryOffset, SeekOrigin.Begin);
        var fileCount = reader.ReadInt32();
        var entries = new List<PckEntry>(fileCount);

        for (var i = 0; i < fileCount; i++)
        {
            var pathLength = reader.ReadInt32();
            if (pathLength <= 0 || pathLength > 8192)
            {
                throw new InvalidOperationException($"Invalid PCK directory entry path length in {path} at index {i}: {pathLength}");
            }

            var entryPath = Encoding.UTF8.GetString(reader.ReadBytes(pathLength)).TrimEnd('\0');
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();
            _ = reader.ReadBytes(16);
            var flags = reader.ReadInt32();

            entries.Add(new PckEntry(entryPath, offset, size, flags));
        }

        return new GodotPck(path, fileBase, entries);
    }

    public static void Write(string path, List<PckWriteEntry> entries)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        writer.Write(Encoding.ASCII.GetBytes("GDPC"));
        writer.Write(3);
        writer.Write(4);
        writer.Write(5);
        writer.Write(1);
        writer.Write(2);
        writer.Write((long)FileBaseOffset);
        writer.Write(0L);

        while (stream.Position < FileBaseOffset)
        {
            writer.Write((byte)0);
        }

        var writtenEntries = new List<PckWrittenEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var offset = stream.Position - FileBaseOffset;
            writer.Write(entry.Data);
            writtenEntries.Add(new PckWrittenEntry(
                entry.Path.Replace('\\', '/'),
                offset,
                entry.Data.LongLength,
                MD5.HashData(entry.Data),
                entry.Flags));
        }

        var directoryOffset = stream.Position;
        writer.Write(writtenEntries.Count);
        foreach (var entry in writtenEntries)
        {
            var pathBytes = Encoding.UTF8.GetBytes(entry.Path + '\0');
            writer.Write(pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(entry.Offset);
            writer.Write(entry.Size);
            writer.Write(entry.Md5);
            writer.Write(entry.Flags);
        }

        stream.Seek(32, SeekOrigin.Begin);
        writer.Write(directoryOffset);
    }

    public byte[] ReadEntry(PckEntry entry)
    {
        using var stream = File.OpenRead(Path);
        stream.Seek(_fileBase + entry.Offset, SeekOrigin.Begin);
        var data = new byte[entry.Size];
        var read = stream.Read(data, 0, data.Length);
        if (read != data.Length)
        {
            throw new EndOfStreamException($"Failed to read full PCK entry {entry.Path} from {Path}");
        }

        return data;
    }

    public void ExtractEntry(string entryPath, string destination)
    {
        var entry = Entries.FirstOrDefault(item => string.Equals(item.Path, entryPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"PCK entry not found: {entryPath}");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, ReadEntry(entry));
    }
}

public sealed record PckEntry(string Path, long Offset, long Size, int Flags);

public sealed record PckWriteEntry(string Path, byte[] Data, int Flags);

public sealed record PckWrittenEntry(string Path, long Offset, long Size, byte[] Md5, int Flags);

public sealed record ReplacementEntry(
    [property: JsonPropertyName("conflict_key")] string ConflictKey,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("staged_path")] string StagedPath,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("card_id")] string CardId,
    [property: JsonPropertyName("pack_id")] string PackId,
    [property: JsonPropertyName("pack_path")] string PackPath,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("source_type")] string SourceType);

public sealed record ReplacementDocument(
    [property: JsonPropertyName("entries")] List<RuntimeReplacementEntry> Entries);

public sealed record RuntimeReplacementEntry(
    [property: JsonPropertyName("cardId")] string CardId,
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("image")] string Image);

public sealed class ConflictEntry
{
    public ConflictEntry(string conflictKey, ReplacementCandidate winner)
    {
        ConflictKey = conflictKey;
        Winner = CandidateSummary.From(winner);
    }

    [JsonPropertyName("conflict_key")]
    public string ConflictKey { get; }

    [JsonPropertyName("source_path")]
    public string SourcePath => Winner.SourcePath;

    [JsonPropertyName("card_id")]
    public string CardId => Winner.CardId;

    [JsonPropertyName("winner")]
    public CandidateSummary Winner { get; set; }

    [JsonPropertyName("overridden")]
    public List<CandidateSummary> Overridden { get; } = [];
}

public sealed record CandidateSummary(
    [property: JsonPropertyName("conflict_key")]
    string ConflictKey,
    [property: JsonPropertyName("source_path")]
    string SourcePath,
    [property: JsonPropertyName("card_id")]
    string CardId,
    [property: JsonPropertyName("pack_id")]
    string PackId,
    [property: JsonPropertyName("pack_path")]
    string PackPath,
    [property: JsonPropertyName("priority")]
    int Priority,
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("source_type")]
    string SourceType)
{
    public static CandidateSummary From(ReplacementCandidate candidate)
    {
        return new CandidateSummary(
            candidate.ConflictKey,
            candidate.SourcePath,
            candidate.CardId,
            candidate.Input.Id,
            candidate.Input.Path,
            candidate.Input.Priority,
            candidate.Type,
            candidate.Input.Kind.ToString());
    }
}

public sealed record FinalPack(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("source_type")] string SourceType);

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
