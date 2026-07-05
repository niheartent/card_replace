using System.Reflection;
using Godot;
using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CardReplace.Scripts;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public const string ModId = "card_replace";
    private const string GeneratedPckFileName = "card_replace.pck";
    private const string LegacyGeneratedPckFileName = "card_replace_generated.pck";

    public static void Init()
    {
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);

        var modRoot = GetModRoot(Assembly.GetExecutingAssembly());
        var log = new DelegateModLog(
            message => Log.Info(message),
            message => Log.Warn(message),
            message => Log.Error(message));

        var pckPath = ResolveGeneratedPckPath(modRoot);
        var loaded = LoadGeneratedPack(pckPath, log);
        CardReplacementRegistry.EnsureLoaded(log);
        ApplyHarmonyPatches(log);

        RitsuSettingsBridge.Register(modRoot, pckPath, loaded, log);

        log.Info($"{ModId}: initialized from {modRoot}; replacementEntries={CardReplacementRegistry.Count}");
    }

    private static string ResolveGeneratedPckPath(string modRoot)
    {
        var officialPath = Path.Combine(modRoot, GeneratedPckFileName);
        if (File.Exists(officialPath))
        {
            return officialPath;
        }

        return Path.Combine(modRoot, LegacyGeneratedPckFileName);
    }

    private static bool LoadGeneratedPack(string pckPath, IModLog log)
    {
        if (!File.Exists(pckPath))
        {
            log.Warn($"{ModId}: generated pck was not found: {pckPath}");
            return false;
        }

        if (!ProjectSettings.LoadResourcePack(pckPath, replaceFiles: true))
        {
            log.Error($"{ModId}: failed to load generated pck: {pckPath}");
            return false;
        }

        log.Info($"{ModId}: loaded generated pck: {pckPath}");
        return true;
    }

    private static void ApplyHarmonyPatches(IModLog log)
    {
        try
        {
            new Harmony(ModId).PatchAll(typeof(Entry).Assembly);
            log.Info($"{ModId}: Harmony patches applied");
        }
        catch (Exception ex)
        {
            log.Error($"{ModId}: failed to apply Harmony patches: {ex}");
        }
    }

    private static string GetModRoot(Assembly assembly)
    {
        var assemblyLocation = assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
        {
            var directory = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return AppContext.BaseDirectory;
    }
}
