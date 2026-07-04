using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace CardReplace.Scripts.Patches;

[HarmonyPatch]
internal static class PortraitPatch
{
    private sealed record PortraitState(Texture2D? Texture, TextureRect.StretchModeEnum StretchMode);

    private static readonly FieldInfo? PortraitField =
        typeof(NCard).GetField("_portrait", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo? AncientPortraitField =
        typeof(NCard).GetField("_ancientPortrait", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly Dictionary<string, PortraitState> DefaultPortraitStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PortraitState> DefaultAncientPortraitStates = new(StringComparer.OrdinalIgnoreCase);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCard), "UpdateVisuals")]
    private static void UpdateVisualsPostfix(NCard __instance)
    {
        TryReplacePortrait(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NCard), "_EnterTree")]
    private static void EnterTreePostfix(NCard __instance)
    {
        TryReplacePortrait(__instance);
    }

    private static void TryReplacePortrait(NCard card)
    {
        var model = card.Model;
        if (model is null)
        {
            return;
        }

        var cardId = model.GetType().FullName ?? model.GetType().Name;
        var portrait = PortraitField?.GetValue(card) as TextureRect;
        var ancientPortrait = AncientPortraitField?.GetValue(card) as TextureRect;

        RememberDefaultState(cardId, portrait, DefaultPortraitStates);
        RememberDefaultState(cardId, ancientPortrait, DefaultAncientPortraitStates);

        if (!CardReplacementRegistry.TryGetTexture(model, out var texture) || texture is null)
        {
            RestoreDefaultState(cardId, portrait, DefaultPortraitStates);
            RestoreDefaultState(cardId, ancientPortrait, DefaultAncientPortraitStates);
            ClearGeneratedTextureIfUnrestored(cardId, portrait, DefaultPortraitStates);
            ClearGeneratedTextureIfUnrestored(cardId, ancientPortrait, DefaultAncientPortraitStates);
            return;
        }

        ApplyTexture(portrait, texture);
        ApplyTexture(ancientPortrait, texture);
    }

    private static void ApplyTexture(TextureRect? rect, Texture2D texture)
    {
        if (rect is null)
        {
            return;
        }

        rect.Texture = texture;
        rect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
    }

    private static void RememberDefaultState(
        string cardId,
        TextureRect? rect,
        Dictionary<string, PortraitState> cache)
    {
        if (string.IsNullOrWhiteSpace(cardId)
            || rect is null
            || cache.ContainsKey(cardId)
            || CardReplacementRegistry.IsReplacementTexture(rect.Texture))
        {
            return;
        }

        cache[cardId] = new PortraitState(rect.Texture, rect.StretchMode);
    }

    private static void RestoreDefaultState(
        string cardId,
        TextureRect? rect,
        Dictionary<string, PortraitState> cache)
    {
        if (string.IsNullOrWhiteSpace(cardId) || rect is null || !cache.TryGetValue(cardId, out var state))
        {
            return;
        }

        rect.Texture = state.Texture;
        rect.StretchMode = state.StretchMode;
    }

    private static void ClearGeneratedTextureIfUnrestored(
        string cardId,
        TextureRect? rect,
        Dictionary<string, PortraitState> cache)
    {
        if (rect is not null && !cache.ContainsKey(cardId) && CardReplacementRegistry.IsReplacementTexture(rect.Texture))
        {
            rect.Texture = null;
        }
    }
}
