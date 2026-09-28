using FlyXenotype.Compat;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FlyXenotype.Patches
{
    // =====================================================================================
    // Simulation-side: make the brain authoritative. These run on every client identically.
    // =====================================================================================

    /// <summary>Schedule: every vanilla consumer reads this getter, so this is the whole override.</summary>
    [HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.CurrentAssignment), MethodType.Getter)]
    public static class Timetable_CurrentAssignment_Patch
    {
        static void Postfix(Pawn ___pawn, ref TimeAssignmentDef __result)
        {
            if (!___pawn.IsColonist || ___pawn.IsPrisonerOfColony) return; // keep vanilla's early-out
            if (FlyRegistry.TryGet(___pawn, out Gene_FlyBrain g)) __result = g.CurrentAssignmentDef;
        }
    }

    /// <summary>Honour brain buckets even when the global "manual priorities" checkbox is off.</summary>
    [HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.GetPriority))]
    public static class WorkSettings_GetPriority_Patch
    {
        static void Postfix(Pawn ___pawn, WorkTypeDef w, ref int __result)
        {
            // While the brain is writing, report the real stored value. MP's WorkPrioritySameValue prefix
            // cancels SetPriority when GetPriority(w) == priority; without this it sees the brain's new
            // target and silently skips every brain write.
            if (FlyWriter.Active) return;
            if (FlyRegistry.TryGet(___pawn, out Gene_FlyBrain g) && g.HasDecided)
                __result = ___pawn.WorkTypeIsDisabled(w) ? 0 : g.PriorityFor(w);
        }
    }

    /// <summary>
    /// Backstop. Rejects priority writes for Flies that don't come from the brain.
    /// In MP a click that slipped past the UI arrives as a synced SetPriority command; this prefix
    /// runs inside that command on every client and drops it on every client, so no desync.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority))]
    public static class WorkSettings_SetPriority_Guard
    {
        public static bool Initializing;

        [HarmonyPriority(Priority.First)]
        static bool Prefix(Pawn ___pawn, int priority)
        {
            if (FlyWriter.Active || Initializing) return true;
            if (!FlyRegistry.IsFly(___pawn)) return true;
            // Vanilla disables newly incapable work types with SetPriority(w, 0) from sim code; allow it.
            if (priority == 0 && !MultiplayerCompat.IsExecutingSyncCommand) return true;
            FlyDiag.GuardRejected(___pawn, priority);
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.EnableAndInitialize))]
    public static class WorkSettings_EnableAndInitialize_Patch
    {
        static void Prefix() => WorkSettings_SetPriority_Guard.Initializing = true;
        static void Finalizer() => WorkSettings_SetPriority_Guard.Initializing = false;
    }

    [HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.SetAssignment))]
    public static class Timetable_SetAssignment_Guard
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(Pawn ___pawn) => FlyWriter.Active || !FlyRegistry.IsFly(___pawn);
    }

    // =====================================================================================
    // UI-side: draw read-only cells. Read the gene's snapshot only — no stat, capacity or
    // skill getters (MP's tick-keyed caches can be polluted from UI; see RimHUD desyncs).
    // =====================================================================================

    [HarmonyPatch(typeof(PawnColumnWorker_WorkPriority), nameof(PawnColumnWorker_WorkPriority.DoCell))]
    public static class WorkTab_DoCell_Lock
    {
        static bool Prefix(Rect rect, Pawn pawn, PawnColumnWorker_WorkPriority __instance)
        {
            if (!FlyRegistry.TryGet(pawn, out Gene_FlyBrain g)) return true;
            if (FlyMod.Settings.hideLockedCells) return false;
            FlyUI.DrawLockedWorkCell(rect, pawn, __instance.def.workType, g);
            return false;
        }
    }

    [HarmonyPatch(typeof(PawnColumnWorker_CopyPasteWorkPriorities), nameof(PawnColumnWorker_CopyPasteWorkPriorities.DoCell))]
    public static class WorkTab_CopyPaste_Lock
    {
        static bool Prefix(Rect rect, Pawn pawn)
        {
            if (!FlyRegistry.IsFly(pawn)) return true;
            FlyUI.DrawLockIcon(rect);
            return false;
        }
    }

    [HarmonyPatch(typeof(PawnColumnWorker_Timetable), nameof(PawnColumnWorker_Timetable.DoCell))]
    public static class ScheduleTab_DoCell_Lock
    {
        static bool Prefix(Rect rect, Pawn pawn)
        {
            if (!FlyRegistry.TryGet(pawn, out Gene_FlyBrain g)) return true;
            if (FlyMod.Settings.hideLockedCells) return false;
            FlyUI.DrawBrainScheduleRow(rect, pawn, g);
            return false;
        }
    }

    [HarmonyPatch(typeof(PawnColumnWorker_CopyPasteTimetable), nameof(PawnColumnWorker_CopyPasteTimetable.DoCell))]
    public static class ScheduleTab_CopyPaste_Lock
    {
        // Must stop the button here: MP also prefixes PasteTo, and Harmony runs every prefix,
        // so a PasteTo prefix alone would not stop MP from sending the timetable field sync.
        static bool Prefix(Rect rect, Pawn pawn)
        {
            if (!FlyRegistry.IsFly(pawn)) return true;
            FlyUI.DrawLockIcon(rect);
            return false;
        }
    }

    [StaticConstructorOnStartup]
    public static class FlyUI
    {
        private static readonly Texture2D LockTex = ContentFinder<Texture2D>.Get("UI/FB/Lock", false) ?? BaseContent.BadTex;

        public static void DrawLockIcon(Rect rect)
        {
            Rect icon = new Rect(rect.x + (rect.width - 16f) / 2f, rect.y + (rect.height - 16f) / 2f, 16f, 16f);
            GUI.color = new Color(1f, 1f, 1f, 0.5f);
            GUI.DrawTexture(icon, LockTex);
            GUI.color = Color.white;
            TooltipHandler.TipRegion(rect, "FB.LockedTip".Translate());
        }

        public static void DrawLockedWorkCell(Rect rect, Pawn pawn, WorkTypeDef work, Gene_FlyBrain g)
        {
            Rect box = new Rect(rect.x + (rect.width - 25f) / 2f, rect.y + 2.5f, 25f, 25f);
            Widgets.DrawBoxSolid(box, new Color(0.2f, 0.2f, 0.2f, 0.6f));
            int prio = g.PriorityFor(work);
            if (prio > 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = WidgetsWork.ColorOfPriority(prio) * new Color(1f, 1f, 1f, 0.6f);
                Widgets.Label(box.ContractedBy(-3f), prio.ToStringCached());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
            }
            if (Mouse.IsOver(box))
            {
                // Built from the brain's last-evaluation snapshot only.
                int dn = Brain.FlyBrainTopology.Current == null || g.brain.Counts == null ? 0
                    : g.brain.Counts[Brain.FlyBrainTopology.Current.WorkStart + work.index];
                TooltipHandler.TipRegion(box, () => "FB.WorkTip".Translate(work.gerundLabel, prio, dn, g.mode.ToString()),
                    pawn.thingIDNumber ^ work.index ^ 0x46);
            }
        }

        public static void DrawBrainScheduleRow(Rect rect, Pawn pawn, Gene_FlyBrain g)
        {
            float w = rect.width / 24f;
            for (int h = 0; h < 24; h++)
            {
                Rect cell = new Rect(rect.x + h * w, rect.y, w, rect.height).ContractedBy(1f);
                TimeAssignmentDef def = ModeToDef((FlyMode)g.modeHistory[h]);
                GUI.color = new Color(1f, 1f, 1f, 0.45f);
                GUI.DrawTexture(cell, def.ColorTexture);
                GUI.color = Color.white;
            }
            DrawLockIcon(new Rect(rect.xMax - 20f, rect.y, 20f, rect.height));
            TooltipHandler.TipRegion(rect, "FB.ScheduleTip".Translate(g.mode.ToString()));
        }

        private static TimeAssignmentDef ModeToDef(FlyMode m)
        {
            switch (m)
            {
                case FlyMode.Work: return TimeAssignmentDefOf.Work;
                case FlyMode.Sleep: return TimeAssignmentDefOf.Sleep;
                case FlyMode.Joy: return TimeAssignmentDefOf.Joy;
                case FlyMode.Meditate: return ModsConfig.RoyaltyActive ? TimeAssignmentDefOf.Meditate : TimeAssignmentDefOf.Joy;
                default: return TimeAssignmentDefOf.Anything;
            }
        }
    }
}

namespace FlyXenotype
{
    /// <summary>
    /// The skill tooltip lists GlobalLearningFactor contributors by hand (hediffs, traits, gene statOffsets),
    /// so stat parts like hive learning never appear there. Append our line. UI-only: no simulation state touched.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(RimWorld.SkillUI), "ListGlobalLearningSpeedOffsets")]
    public static class SkillUI_HiveLearningLine
    {
        public static void Postfix(RimWorld.SkillRecord sk, System.Text.StringBuilder sb)
        {
            Verse.Pawn p = sk?.Pawn;
            Gene_HiveLearning g = p?.genes?.GetFirstGeneOfType<Gene_HiveLearning>();
            if (g == null || !g.Active) return;
            float off = g.LearningOffset;
            sb.AppendLine("  - " + Verse.TranslatorFormattedStringExtensions.Translate("FB.StatHive", g.others)
                + ": " + (off >= 0f ? "+" : "") + Verse.GenText.ToStringPercent(off));
        }
    }
}

namespace FlyXenotype
{
    /// <summary>
    /// Escape reflex hook. Prefix on the attacker's melee swing: if the target Fly escapes, skip the swing
    /// entirely (no hit, no stagger) and report "not cast", exactly like a vanilla miss. Simulation-side.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(RimWorld.Verb_MeleeAttack), "TryCastShot")]
    public static class MeleeAttack_EscapeReflex
    {
        public static bool Prefix(RimWorld.Verb_MeleeAttack __instance, ref bool __result)
        {
            Verse.Pawn attacker = __instance.CasterPawn;
            if (attacker == null || !attacker.Spawned || attacker.stances.FullBodyBusy) return true;
            if (!(__instance.CurrentTarget.Thing is Verse.Pawn target)) return true;
            Gene_EscapeReflex g = target.genes?.GetFirstGeneOfType<Gene_EscapeReflex>();
            if (g == null || !g.TryEscape(attacker)) return true;
            if (attacker.Spawned) attacker.Drawer.Notify_MeleeAttackOn(target);
            __result = false;
            return false;
        }
    }
}
