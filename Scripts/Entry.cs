using System.Reflection;
using CardReplace.Scripts.ArtPacks;
using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CardReplace.Scripts;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public const string ModId = "card_replace";

    public static void Init()
    {
        var harmony = new Harmony("sts2.reme.card_replace");
        harmony.PatchAll();
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);

        var modRoot = ModPaths.GetModRoot(Assembly.GetExecutingAssembly());
        var log = new DelegateModLog(
            message => Log.Info(message),
            message => Log.Warn(message),
            message => Log.Error(message));

        var config = ArtPackConfigStore.LoadOrCreate(modRoot, log);
        var registry = ArtPackRegistry.Load(modRoot, config, log);
        ArtPackConfigStore.Save(modRoot, config, log);

        var effectiveOverrides = registry.ResolveEffectiveOverrides();
        ArtReplacementService.Initialize(effectiveOverrides, log);
        RitsuSettingsBridge.Register(modRoot, config, registry, log);

        Log.Info($"{ModId}: initialized from {modRoot}");
        Log.Info($"{ModId}: loaded {registry.Packs.Count} pack(s), {effectiveOverrides.Count} effective override(s).");
        foreach (var resolved in effectiveOverrides)
        {
            Log.Info(
                $"{ModId}: {resolved.Override.SourcePath} -> {resolved.AssetPath} " +
                $"[{resolved.Override.Type}, pack={resolved.PackId}, priority={resolved.Priority}]");
        }
    }
}
