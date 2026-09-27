using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FlyXenotype
{
    /// <summary>
    /// Per-pawn cosmetic variety for Flies. Colours are picked from a fixed palette by hashing the
    /// pawn's thingIDNumber, so a pawn always looks the same (across saves and MP clients) without
    /// storing anything. Visual only: nothing here touches simulation state.
    /// </summary>
    public static class FlyLooks
    {
        public static readonly Color[] EyeColors =
        {
            new Color(0.78f, 0.20f, 0.17f), // red (classic Drosophila)
            new Color(0.88f, 0.55f, 0.15f), // amber
            new Color(0.38f, 0.66f, 0.24f), // green
            new Color(0.20f, 0.60f, 0.64f), // teal
            new Color(0.27f, 0.42f, 0.82f), // blue
            new Color(0.52f, 0.30f, 0.72f), // violet
            new Color(0.86f, 0.76f, 0.26f), // gold
        };

        public static readonly Color[] ChitinColors =
        {
            new Color(0.30f, 0.22f, 0.16f), // dark brown
            new Color(0.21f, 0.21f, 0.25f), // blue-charcoal
            new Color(0.25f, 0.29f, 0.19f), // dark olive
            new Color(0.34f, 0.21f, 0.20f), // dark maroon
            new Color(0.19f, 0.18f, 0.17f), // near black
            new Color(0.39f, 0.31f, 0.21f), // bronze
        };

        public static Color Pick(Color[] palette, Pawn p, int salt) =>
            palette[(int)(Brain.FlyNoise.Hash(p.thingIDNumber, salt, 0x5EED, 7) % (uint)palette.Length)];
    }

    /// <summary>Compound-eye overlay: grayscale texture tinted with the pawn's eye colour.</summary>
    public class PawnRenderNode_FlyEyes : PawnRenderNode_AttachmentHead
    {
        public PawnRenderNode_FlyEyes(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree) { }

        public override Color ColorFor(Pawn pawn) => FlyLooks.Pick(FlyLooks.EyeColors, pawn, 1);
    }

    /// <summary>Marker gene class for chitin skin; the colour itself comes from the Harmony postfix below.</summary>
    public class Gene_ChitinSkin : Gene
    {
        public override void PostAdd()
        {
            base.PostAdd();
            pawn.Drawer?.renderer?.SetAllGraphicsDirty();
        }

        public override void PostRemove()
        {
            base.PostRemove();
            pawn.Drawer?.renderer?.SetAllGraphicsDirty();
        }
    }

    /// <summary>Chitin skin: each carrier gets one of several dark shades.</summary>
    [HarmonyPatch(typeof(Pawn_StoryTracker), nameof(Pawn_StoryTracker.SkinColor), MethodType.Getter)]
    public static class StoryTracker_SkinColor_Patch
    {
        static void Postfix(Pawn ___pawn, ref Color __result)
        {
            if (___pawn?.genes == null) return;
            Gene_ChitinSkin g = ___pawn.genes.GetFirstGeneOfType<Gene_ChitinSkin>();
            if (g != null && g.Active) __result = FlyLooks.Pick(FlyLooks.ChitinColors, ___pawn, 2);
        }
    }
}
