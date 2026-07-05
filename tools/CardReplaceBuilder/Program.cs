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
    private const string OutputFolderName = "_generated";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  dotnet run --project tools/CardReplaceBuilder -- <Godot console exe> <target folder> [target folder...]");
            return 2;
        }

        try
        {
            var projectRoot = FindProjectRoot();
            var options = CreateBuildOptions(projectRoot, args[0], args.Skip(1).ToList());
            var result = Build(options);
            Console.WriteLine($"Built {result.ReplacementCount} replacement(s).");
            Console.WriteLine($"PCK: {result.PckPath}");
            Console.WriteLine($"Mod: {result.ModPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static BuildOptions CreateBuildOptions(string projectRoot, string godotExe, List<string> resourcePackDirs)
    {
        if (string.IsNullOrWhiteSpace(godotExe))
        {
            throw new InvalidOperationException("Godot console exe path is empty.");
        }

        var normalizedResourcePackDirs = resourcePackDirs
            .Select(path => Path.GetFullPath(path))
            .ToList();

        if (normalizedResourcePackDirs.Count == 0)
        {
            throw new InvalidOperationException("Pass at least one target folder.");
        }

        var outputRoot = Path.Combine(normalizedResourcePackDirs[0], OutputFolderName);

        return new BuildOptions(
            ProjectRoot: projectRoot,
            GodotExe: Path.GetFullPath(godotExe),
            ResourcePackDirs: normalizedResourcePackDirs,
            OutputRoot: Path.Combine(outputRoot, "build"),
            OutputModDir: Path.Combine(outputRoot, "card_replace"),
            LoaderDllPath: Path.Combine(projectRoot, ".godot", "mono", "temp", "bin", "Debug", "card_replace.dll"),
            ModManifestPath: Path.Combine(projectRoot, "card_replace.json"),
            StagingRoot: Path.Combine(outputRoot, "staging_godot_project"),
            PckName: DefaultPckName);
    }

    private static BuildResult Build(BuildOptions options)
    {
        var outputRoot = options.OutputRoot;
        var stagingRoot = options.StagingRoot;
        var pckPath = Path.Combine(outputRoot, options.PckName);

        Directory.CreateDirectory(outputRoot);
        ResetDirectory(stagingRoot);
        WriteGodotProjectFiles(stagingRoot);

        var sourceContexts = ResolveSourceContexts(options.ResourcePackDirs)
            .Where(source => source.Enabled)
            .OrderBy(source => source.Priority)
            .ThenBy(source => source.Order)
            .ToList();

        if (sourceContexts.Count == 0)
        {
            throw new InvalidOperationException("No enabled entries were found. Check priority.json in the target folder.");
        }

        var candidates = LoadCandidates(sourceContexts, stagingRoot);
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
            sourceContexts.Count,
            replacements.Count,
            sourceContexts.Select(source => new FinalPack(source.Id, source.Path, source.Priority, source.Kind.ToString())).ToList(),
            replacements.OrderBy(item => item.CardId, StringComparer.OrdinalIgnoreCase).ToList());

        var conflictReport = new ConflictReport(
            conflicts.Count,
            conflicts.Values.OrderBy(item => item.ConflictKey, StringComparer.OrdinalIgnoreCase).ToList());

        WriteJson(Path.Combine(outputRoot, FinalManifestName), finalManifest);
        WriteJson(Path.Combine(outputRoot, ConflictReportName), conflictReport);

        BuildLoaderDll(options.ProjectRoot);
        ExportPck(options.GodotExe, stagingRoot, pckPath);

        CopyModOutputs(options, outputRoot, options.OutputModDir, pckPath);

        return new BuildResult(pckPath, options.OutputModDir, replacements.Count);
    }

    private static List<ResourceContext> ResolveSourceContexts(
        List<string> resourcePackDirs)
    {
        var sources = new List<ResourceSource>();
        sources.AddRange(LoadResourcePackSources(resourcePackDirs));

        return sources
            .Select((source, order) => ResourceContext.Create(source, order))
            .ToList();
    }

    private static IEnumerable<ResourceSource> LoadResourcePackSources(
        List<string> resourcePackDirs)
    {
        foreach (var indexFile in EnumerateResourcePackIndexFiles(resourcePackDirs))
        {
            foreach (var source in ReadResourcePackIndex(indexFile))
            {
                yield return source;
            }
        }
    }

    private static IEnumerable<string> EnumerateResourcePackIndexFiles(
        List<string> resourcePackDirs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in resourcePackDirs)
        {
            var root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Resource pack directory was not found: {root}");
            }

            var indexFile = Path.Combine(root, "priority.json");
            if (!File.Exists(indexFile))
            {
                throw new FileNotFoundException($"Resource pack index was not found: {indexFile}");
            }

            if (seen.Add(indexFile))
            {
                yield return indexFile;
            }
        }
    }

    private static IEnumerable<ResourceSource> ReadResourcePackIndex(string indexFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(indexFile), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Resource pack index must be a path-to-priority object: {indexFile}");
        }

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var source = ReadResourcePackIndexEntry(indexFile, property);
            if (source is not null)
            {
                yield return source;
            }
        }
    }

    private static ResourceSource? ReadResourcePackIndexEntry(string indexFile, JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.Number)
        {
            return new ResourceSource
            {
                Path = ResolveResourcePackEntryPath(indexFile, property.Name),
                Priority = property.Value.GetInt32()
            };
        }

        if (property.Value.ValueKind == JsonValueKind.Object)
        {
            var source = property.Value.Deserialize<ResourceSource>(JsonOptions) ?? new ResourceSource();
            source.Path = ResolveResourcePackEntryPath(
                indexFile,
                string.IsNullOrWhiteSpace(source.Path) ? property.Name : source.Path);
            return source;
        }

        throw new InvalidOperationException($"Resource pack index entry must be a number or object: {indexFile} -> {property.Name}");
    }

    private static string ResolveResourcePackEntryPath(string indexFile, string entryPath)
    {
        return Path.GetFullPath(Path.IsPathRooted(entryPath)
            ? entryPath
            : Path.Combine(Path.GetDirectoryName(indexFile) ?? Directory.GetCurrentDirectory(), entryPath));
    }

    private static List<ReplacementCandidate> LoadCandidates(List<ResourceContext> sources, string stagingRoot)
    {
        var candidates = new List<ReplacementCandidate>();
        var zipRoot = Path.Combine(stagingRoot, "_source_zips");

        foreach (var source in sources)
        {
            switch (source.Kind)
            {
                case ResourceKind.CardArtPackJson:
                    candidates.AddRange(LoadCardArtPackCandidates(source));
                    break;
                case ResourceKind.ModFolder:
                    candidates.AddRange(LoadModFolderCandidates(source));
                    break;
                case ResourceKind.PckFile:
                    candidates.AddRange(LoadPckCandidates(source, source.Path));
                    break;
                case ResourceKind.Zip:
                    candidates.AddRange(LoadZipCandidates(source, zipRoot));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(source.Kind), source.Kind, "Unsupported source kind.");
            }
        }

        Console.WriteLine($"Loaded {candidates.Count} replacement candidate(s) from {sources.Count} source(s).");
        return candidates;
    }

    private static IEnumerable<ReplacementCandidate> LoadCardArtPackCandidates(ResourceContext source)
    {
        foreach (var item in ReadCardArtPackData(source.Path))
        {
            if (!IsSupportedSourcePath(item.SourcePath))
            {
                continue;
            }

            var isAnimated = item.Type.Equals("animated_gif", StringComparison.OrdinalIgnoreCase)
                && item.Frames.Count > 0;
            var base64 = FirstNonEmpty(item.PngBase64, item.EditSourcePngBase64);
            if (string.IsNullOrWhiteSpace(base64))
            {
                if (!isAnimated)
                {
                    Console.WriteLine($"Skipped {item.SourcePath}: no png_base64 or edit_source_png_base64 in {source.Path}");
                    continue;
                }
            }

            var cardId = ToCardId(item.SourcePath);
            IReplacementAsset asset = isAnimated
                ? new AnimatedCardArtPackAsset(item.SourcePath, item.Frames)
                : new CardArtPackAsset(item.SourcePath, base64!);

            yield return new ReplacementCandidate(
                ConflictKey(cardId, item.SourcePath),
                item.SourcePath,
                cardId,
                source,
                item.Type,
                asset);
        }
    }

    private static IEnumerable<ReplacementCandidate> LoadModFolderCandidates(ResourceContext source)
    {
        foreach (var pckPath in Directory.GetFiles(source.Path, "*.pck", SearchOption.TopDirectoryOnly))
        {
            foreach (var candidate in LoadPckCandidates(source, pckPath))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<ReplacementCandidate> LoadZipCandidates(ResourceContext source, string zipRoot)
    {
        var extractRoot = Path.Combine(zipRoot, SanitizeFileName(source.Id));
        ResetDirectory(extractRoot);
        ZipFile.ExtractToDirectory(source.Path, extractRoot, overwriteFiles: true);

        foreach (var pckPath in Directory.GetFiles(extractRoot, "*.pck", SearchOption.AllDirectories))
        {
            foreach (var candidate in LoadPckCandidates(source, pckPath))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<ReplacementCandidate> LoadPckCandidates(ResourceContext source, string pckPath)
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
                source with { Path = pckPath },
                "pck_imported",
                new PckImportedAsset(pckPath, importEntry, ctexEntry, sourcePath));
        }

        foreach (var mapEntry in pck.Entries.Where(entry => IsPckReplacementMapPath(entry.Path)))
        {
            foreach (var candidate in LoadPckReplacementMapCandidates(source, pckPath, pck, entriesByPath, mapEntry))
            {
                count++;
                yield return candidate;
            }
        }

        Console.WriteLine($"Loaded {count} PCK card art candidate(s): {pckPath}");
    }

    private static IEnumerable<ReplacementCandidate> LoadPckReplacementMapCandidates(
        ResourceContext source,
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
                source with { Path = pckPath },
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
                     .OrderBy(candidate => candidate.Source.Priority)
                     .ThenBy(candidate => candidate.Source.Order))
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
                winner.Source.Id,
                winner.Source.Path,
                winner.Source.Priority,
                winner.Type,
                winner.Source.Kind.ToString()));
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
                GetString(item, "edit_source_png_base64"),
                ReadCardArtPackFrames(item).ToList());
        }
    }

    private static IEnumerable<CardArtPackFrameData> ReadCardArtPackFrames(JsonElement item)
    {
        if (!item.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var frame in frames.EnumerateArray())
        {
            var base64 = GetString(frame, "png_base64");
            if (string.IsNullOrWhiteSpace(base64))
            {
                continue;
            }

            yield return new CardArtPackFrameData(base64, GetSingle(frame, "delay", 0.1f));
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
        BuildOptions options,
        string outputRoot,
        string outputModDir,
        string pckPath)
    {
        RequireFile(options.LoaderDllPath, "Loader DLL was not found after building the loader.");
        RequireFile(options.ModManifestPath, "Mod manifest was not found.");
        RequireFile(pckPath, "Generated PCK was not found.");

        ResetDirectory(outputModDir);
        File.Copy(options.LoaderDllPath, Path.Combine(outputModDir, Path.GetFileName(options.LoaderDllPath)), overwrite: true);
        File.Copy(options.ModManifestPath, Path.Combine(outputModDir, Path.GetFileName(options.ModManifestPath)), overwrite: true);
        File.Copy(pckPath, Path.Combine(outputModDir, Path.GetFileName(pckPath)), overwrite: true);
        CopyIfExists(Path.Combine(outputRoot, FinalManifestName), Path.Combine(outputModDir, FinalManifestName));
        CopyIfExists(Path.Combine(outputRoot, ConflictReportName), Path.Combine(outputModDir, ConflictReportName));
        Console.WriteLine($"Generated mod folder: {outputModDir}");
    }

    private static void BuildLoaderDll(string projectRoot)
    {
        Console.WriteLine("Building card_replace.dll...");
        RunProcess(
            "dotnet",
            $"build \"{Path.Combine(projectRoot, "card_replace.csproj")}\" -v:minimal -p:SkipModDeploy=true",
            projectRoot);
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

    private static float GetSingle(JsonElement element, string propertyName, float fallback)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : fallback;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string SanitizeFileName(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "source" : sanitized;
    }

    private static string FindProjectRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "card_replace.csproj")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? "";
        }

        throw new DirectoryNotFoundException("Could not find card_replace.csproj. Run the builder from the card_replace repository.");
    }
}

public sealed record BuildOptions(
    string ProjectRoot,
    string GodotExe,
    List<string> ResourcePackDirs,
    string OutputRoot,
    string OutputModDir,
    string LoaderDllPath,
    string ModManifestPath,
    string StagingRoot,
    string PckName);

public sealed class ResourceSource
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

public sealed record ResourceContext(
    string Id,
    string Path,
    int Priority,
    int Order,
    bool Enabled,
    ResourceKind Kind)
{
    public static ResourceContext Create(ResourceSource source, int order)
    {
        var path = System.IO.Path.GetFullPath(source.Path);

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"Resource source was not found: {path}");
        }

        var kind = DetectKind(path, source.Type);
        var id = string.IsNullOrWhiteSpace(source.Id)
            ? DefaultId(path)
            : source.Id.Trim();

        return new ResourceContext(id, path, source.Priority, order, source.Enabled, kind);
    }

    private static ResourceKind DetectKind(string path, string type)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            return type.Trim().ToLowerInvariant() switch
            {
                "cardartpack" or "cardartpack_json" or "cardartpackjson" => ResourceKind.CardArtPackJson,
                "folder" or "mod_folder" or "modfolder" => ResourceKind.ModFolder,
                "zip" => ResourceKind.Zip,
                "pck" or "pck_file" or "pckfile" => ResourceKind.PckFile,
                _ => throw new InvalidOperationException($"Unknown source type: {type}")
            };
        }

        if (Directory.Exists(path))
        {
            return ResourceKind.ModFolder;
        }

        var extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".cardartpack.json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cardartpack.json", StringComparison.OrdinalIgnoreCase))
        {
            return ResourceKind.CardArtPackJson;
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return ResourceKind.Zip;
        }

        if (extension.Equals(".pck", StringComparison.OrdinalIgnoreCase))
        {
            return ResourceKind.PckFile;
        }

        throw new InvalidOperationException($"Could not infer source type: {path}");
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

        return string.IsNullOrWhiteSpace(name) ? "source" : name;
    }
}

public enum ResourceKind
{
    CardArtPackJson,
    ModFolder,
    Zip,
    PckFile
}

public sealed record CardArtPackOverrideData(
    string SourcePath,
    string Type,
    string? PngBase64,
    string? EditSourcePngBase64,
    List<CardArtPackFrameData> Frames);

public sealed record CardArtPackFrameData(string PngBase64, float Delay);

public sealed record PckReplacementDocument(
    [property: JsonPropertyName("normalReplacements")] List<PckNormalReplacement> NormalReplacements);

public sealed record PckNormalReplacement(
    [property: JsonPropertyName("cardType")] string CardType,
    [property: JsonPropertyName("portraitPath")] string PortraitPath);

public sealed record ReplacementCandidate(
    string ConflictKey,
    string SourcePath,
    string CardId,
    ResourceContext Source,
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

public sealed record AnimatedCardArtPackAsset(string SourcePath, List<CardArtPackFrameData> Frames) : IReplacementAsset
{
    public string Image => $"res://{AnimatedTexturePath.Replace('\\', '/')}";

    private string CardPathWithoutExtension => Path.Combine(
        Path.GetDirectoryName(ProgramPrivate.ToGeneratedCardRelativePath(SourcePath)) ?? "",
        Path.GetFileNameWithoutExtension(SourcePath));

    private string FrameDirectory => $"{CardPathWithoutExtension}_frames";

    private string AnimatedTexturePath => $"{CardPathWithoutExtension}.tres";

    public string Stage(string stagingRoot)
    {
        var frameRoot = Path.Combine(stagingRoot, FrameDirectory);
        Directory.CreateDirectory(frameRoot);

        var relativeFramePaths = new List<string>(Frames.Count);
        for (var index = 0; index < Frames.Count; index++)
        {
            var relativePath = Path.Combine(FrameDirectory, $"frame_{index:D3}.png");
            var outputPath = Path.Combine(stagingRoot, relativePath);
            File.WriteAllBytes(outputPath, Convert.FromBase64String(Frames[index].PngBase64));
            relativeFramePaths.Add(relativePath.Replace('\\', '/'));
        }

        var texturePath = Path.Combine(stagingRoot, AnimatedTexturePath);
        Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
        File.WriteAllText(texturePath, BuildAnimatedTextureResource(relativeFramePaths));
        return AnimatedTexturePath.Replace('\\', '/');
    }

    private string BuildAnimatedTextureResource(List<string> relativeFramePaths)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[gd_resource type=\"AnimatedTexture\" load_steps={relativeFramePaths.Count + 1} format=3]");
        builder.AppendLine();

        for (var index = 0; index < relativeFramePaths.Count; index++)
        {
            builder.AppendLine($"[ext_resource type=\"Texture2D\" path=\"res://{relativeFramePaths[index]}\" id=\"{index + 1}\"]");
        }

        builder.AppendLine();
        builder.AppendLine("[resource]");
        builder.AppendLine($"frames = {relativeFramePaths.Count}");

        for (var index = 0; index < relativeFramePaths.Count; index++)
        {
            builder.AppendLine($"frame_{index}/texture = ExtResource(\"{index + 1}\")");
            builder.AppendLine($"frame_{index}/duration = {FormatFloat(Math.Max(Frames[index].Delay, 0.01f))}");
        }

        return builder.ToString();
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
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
            candidate.Source.Id,
            candidate.Source.Path,
            candidate.Source.Priority,
            candidate.Type,
            candidate.Source.Kind.ToString());
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

public sealed record BuildResult(string PckPath, string ModPath, int ReplacementCount);
