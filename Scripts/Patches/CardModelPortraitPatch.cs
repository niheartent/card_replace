using CardReplace.Scripts.ArtPacks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace CardReplace.Scripts.Patches;

[HarmonyPatch(typeof(CardModel), "get_Portrait")]
public static class CardModelPortraitPatch
{
    public static void Postfix(CardModel __instance, ref Texture2D __result)
    {
        if (ArtReplacementService.TryGetReplacement(__instance, out var replacement))
        {
            __result = replacement;
        }
    }
}
