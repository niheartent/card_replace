using CardReplace.Scripts.ArtPacks;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace CardReplace.Scripts;

public static class RitsuSettingsBridge
{
    public static void Register(string modRoot, ArtPackConfig config, ArtPackRegistry registry, IModLog log)
    {
        RitsuLibFramework.RegisterModSettings(
            Entry.ModId,
            page => BuildPage(page, modRoot, config, registry, log),
            "Card Replace");
    }

    private static void BuildPage(
        ModSettingsPageBuilder page,
        string modRoot,
        ArtPackConfig config,
        ArtPackRegistry registry,
        IModLog log)
    {
        page
            .WithModDisplayName(Text("Card Replace"))
            .WithTitle(Text("Card Replace"))
            .WithDescription(Text("Enable image packs and set priority. Higher priority wins when packs replace the same card art."))
            .AddSection("packs", section => BuildPacksSection(section, modRoot, config, registry, log));
    }

    private static void BuildPacksSection(
        ModSettingsSectionBuilder section,
        string modRoot,
        ArtPackConfig config,
        ArtPackRegistry registry,
        IModLog log)
    {
        section
            .WithTitle(Text("Image Packs"))
            .WithDescription(Text("Each folder under packs with a pack.json appears here."));

        if (registry.Packs.Count == 0)
        {
            section.AddParagraph(
                "no_packs",
                Text("No packs found"),
                Text("Create folders under packs and add pack.json manifests to make them configurable."),
                null);
            return;
        }

        for (var i = 0; i < registry.Packs.Count; i++)
        {
            var pack = registry.Packs[i];
            var entry = pack.Config;
            var id = $"{i}_{SanitizeId(pack.Manifest.Id)}";

            section.AddHeader(
                $"{id}_header",
                Text(string.IsNullOrWhiteSpace(pack.Manifest.Name) ? pack.Manifest.Id : pack.Manifest.Name),
                Text($"{pack.Manifest.Id}: {pack.Manifest.Overrides.Count} replacement(s)"));

            section.AddToggle(
                $"{id}_enabled",
                Text("Enabled"),
                ModSettingsBindings.Callback(
                    Entry.ModId,
                    $"{pack.Manifest.Id}.enabled",
                    () => entry.Enabled,
                    value =>
                    {
                        entry.Enabled = value;
                        SaveAndReload(modRoot, config, registry, log);
                    },
                    () => SaveAndReload(modRoot, config, registry, log),
                    SaveScope.Global),
                Text("Turn this image pack on or off."),
                null);

            section.AddIntSlider(
                $"{id}_priority",
                Text("Priority"),
                ModSettingsBindings.Callback(
                    Entry.ModId,
                    $"{pack.Manifest.Id}.priority",
                    () => entry.Priority,
                    value =>
                    {
                        entry.Priority = value;
                        SaveAndReload(modRoot, config, registry, log);
                    },
                    () => SaveAndReload(modRoot, config, registry, log),
                    SaveScope.Global),
                0,
                1000,
                1,
                value => value.ToString(),
                Text("When multiple packs replace the same source image, the highest priority wins."));
        }
    }

    private static void SaveAndReload(string modRoot, ArtPackConfig config, ArtPackRegistry registry, IModLog log)
    {
        ArtPackConfigStore.Save(modRoot, config, log);
        ArtReplacementService.Initialize(registry.ResolveEffectiveOverrides(), log);
    }

    private static ModSettingsText Text(string value)
    {
        return ModSettingsText.Literal(value);
    }

    private static string SanitizeId(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_').ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "pack" : sanitized;
    }
}
