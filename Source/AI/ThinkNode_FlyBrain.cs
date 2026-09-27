using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace FlyXenotype.AI
{
    /// <summary>Gate for the Fly subtree. Non-Fly pawns pay one dictionary lookup.</summary>
    public class ThinkNode_ConditionalFlyBrain : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn) => FlyRegistry.IsFly(pawn);
    }

    /// <summary>
    /// Crisis job giver, inserted above the vanilla work branch for Fly pawns.
    /// 1. Makes sure the brain is fresh (evaluates in-sim if the staggered tick was missed, e.g.
    ///    right after spawning or a map transfer).
    /// 2. If the brain flagged a crisis, scans ONLY the crisis work types (Firefighter, Doctor, ...)
    ///    instead of every work giver, and returns the nearest valid job.
    /// Everything else is left to vanilla JobGiver_Work, which already reads the brain's priorities
    /// and schedule through the Harmony postfixes.
    /// </summary>
    public class JobGiver_FlyCrisis : ThinkNode_JobGiver
    {
        public int staleTicks = 2 * MapComponent_FlyColonySense.Interval;

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!FlyRegistry.TryGet(pawn, out Gene_FlyBrain g) || !pawn.Spawned) return null;

            int tick = Find.TickManager.TicksGame;
            if (tick - g.lastEvalTick > staleTicks)
            {
                var sense = pawn.Map.GetComponent<MapComponent_FlyColonySense>();
                if (sense != null) g.Evaluate(sense, tick); // think tree runs in the sim: deterministic
            }

            if (!g.crisis || g.crisisWork.Count == 0) return null;
            if (pawn.Downed || pawn.InMentalState || pawn.health.hediffSet.InLabor()) return null;

            List<WorkTypeDef> works = g.crisisWork; // already in def-list order
            for (int i = 0; i < works.Count; i++)
            {
                WorkTypeDef wt = works[i];
                if (pawn.WorkTypeIsDisabled(wt)) continue;
                List<WorkGiverDef> givers = wt.workGiversByPriority;
                for (int j = 0; j < givers.Count; j++)
                {
                    Job job = TryGiver(pawn, givers[j].Worker);
                    if (job != null) return job;
                }
            }
            return null;
        }

        private static Job TryGiver(Pawn pawn, WorkGiver giver)
        {
            if (!CanUse(pawn, giver)) return null;

            Job nonScan = giver.NonScanJob(pawn);
            if (nonScan != null) return nonScan;

            if (!(giver is WorkGiver_Scanner scanner)) return null;
            TraverseParms tp = TraverseParms.For(pawn, scanner.MaxPathDanger(pawn));
            System.Predicate<Thing> valid = t => !t.IsForbidden(pawn) && scanner.HasJobOnThing(pawn, t);

            Thing target;
            IEnumerable<Thing> global = scanner.PotentialWorkThingsGlobal(pawn);
            if (global != null)
                target = GenClosest.ClosestThing_Global_Reachable(pawn.Position, pawn.Map, global, scanner.PathEndMode, tp, 9999f, valid);
            else if (scanner.PotentialWorkThingRequest.group != ThingRequestGroup.Undefined)
                target = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, scanner.PotentialWorkThingRequest,
                    scanner.PathEndMode, tp, 9999f, valid, null, 0, scanner.MaxRegionsToScanBeforeGlobalSearch);
            else
                return null; // cell-based scanners are left to vanilla

            return target != null ? scanner.JobOnThing(pawn, target) : null;
        }

        // Mirrors the private JobGiver_Work.PawnCanUseWorkGiver (1.6).
        private static bool CanUse(Pawn pawn, WorkGiver giver)
        {
            if (!giver.def.nonColonistsCanDo && !pawn.IsColonist) return false;
            if (pawn.WorkTagIsDisabled(giver.def.workTags)) return false;
            if (giver.def.workType != null && pawn.WorkTypeIsDisabled(giver.def.workType)) return false;
            if (giver.ShouldSkip(pawn)) return false;
            if (giver.MissingRequiredCapacity(pawn) != null) return false;
            return true;
        }
    }
}
