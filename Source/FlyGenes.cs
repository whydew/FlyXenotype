using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FlyXenotype
{
    [DefOf]
    public static class FB_DefOf
    {
        public static GeneDef FB_FlyBrain;
        public static GeneDef FB_SugarDrive;
        public static GeneDef FB_SwarmSense;
        public static ThoughtDef FB_AteSweet;
        static FB_DefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(FB_DefOf));
    }

    // =====================================================================================
    // Sugar drive: mood boost from sweet food; the brain weights Cooking/Growing when sweets run low.
    // Notify_IngestedThing is called by vanilla from the simulation, so this is deterministic.
    // =====================================================================================
    public class Gene_SugarDrive : Gene
    {
        public override void Notify_IngestedThing(Thing thing, int numTaken)
        {
            base.Notify_IngestedThing(thing, numTaken);
            if (FlySweets.IsSweet(thing.def))
                pawn.needs?.mood?.thoughts?.memories?.TryGainMemory(FB_DefOf.FB_AteSweet);
        }
    }

    public static class FlySweets
    {
        // Resolved by defName so missing DLC items are simply skipped. Order is fixed (list, not set).
        private static readonly string[] SweetDefNames = { "RawBerries", "InsectJelly", "Chocolate", "RawAgave", "Ambrosia" };
        private static List<ThingDef> sweets;

        public static List<ThingDef> SweetDefs
        {
            get
            {
                if (sweets == null)
                {
                    sweets = new List<ThingDef>();
                    foreach (string n in SweetDefNames)
                    {
                        ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(n);
                        if (d != null) sweets.Add(d);
                    }
                }
                return sweets;
            }
        }

        public static bool IsSweet(ThingDef def)
        {
            if (def?.ingestible == null) return false;
            if (def.ingestible.preferability >= FoodPreferability.MealFine) return true; // fine / lavish meals
            return SweetDefs.Contains(def);
        }
    }

    // =====================================================================================
    // Swarm sense: work-speed and mood bonus per nearby same-faction swarm carrier; lonely without one.
    // Neighbour counts are computed in MapComponent_FlyColonySense on the 250-tick sensor pass and
    // cached here, so the stat part and thought worker are pure reads of simulation state.
    // =====================================================================================
    public class Gene_SwarmSense : Gene
    {
        public const float Radius = 12f;
        public const int MaxCounted = 4;
        public const float WorkSpeedPerNeighbour = 0.03f;

        public int nearby;   // carriers within Radius
        public int onMap;    // carriers anywhere on the same map (excluding self)

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nearby, "fbNearby");
            Scribe_Values.Look(ref onMap, "fbOnMap");
        }

        private static readonly List<Pawn> carriers = new List<Pawn>();
        private static readonly List<Gene_SwarmSense> carrierGenes = new List<Gene_SwarmSense>();

        /// <summary>Called from MapComponent_FlyColonySense.RefreshCheap. Deterministic iteration order.</summary>
        public static void RefreshMap(Map map)
        {
            carriers.Clear();
            carrierGenes.Clear();
            List<Pawn> humans = map.mapPawns.AllHumanlikeSpawned;
            for (int i = 0; i < humans.Count; i++)
            {
                Gene_SwarmSense g = humans[i].genes?.GetFirstGeneOfType<Gene_SwarmSense>();
                if (g != null && g.Active) { carriers.Add(humans[i]); carrierGenes.Add(g); }
            }
            float r2 = Radius * Radius;
            for (int a = 0; a < carriers.Count; a++)
            {
                int near = 0, all = 0;
                for (int b = 0; b < carriers.Count; b++)
                {
                    if (a == b || carriers[a].Faction != carriers[b].Faction) continue;
                    all++;
                    if ((carriers[a].Position - carriers[b].Position).LengthHorizontalSquared <= r2) near++;
                }
                carrierGenes[a].nearby = near;
                carrierGenes[a].onMap = all;
            }
        }

        public float WorkSpeedFactor => 1f + WorkSpeedPerNeighbour * Mathf.Min(nearby, MaxCounted);
    }

    public class ThoughtWorker_FlySwarm : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            if (!p.Spawned) return ThoughtState.Inactive;
            Gene_SwarmSense g = p.genes?.GetFirstGeneOfType<Gene_SwarmSense>();
            if (g == null || !g.Active) return ThoughtState.Inactive;
            if (g.onMap == 0) return ThoughtState.ActiveAtStage(0);   // alone
            if (g.nearby >= 3) return ThoughtState.ActiveAtStage(2);  // swarming
            if (g.nearby >= 1) return ThoughtState.ActiveAtStage(1);  // company
            return ThoughtState.Inactive;
        }
    }

    public class StatPart_FlySwarm : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            if (req.Thing is Pawn p && Get(p) is Gene_SwarmSense g) val *= g.WorkSpeedFactor;
        }

        public override string ExplanationPart(StatRequest req)
        {
            if (req.Thing is Pawn p && Get(p) is Gene_SwarmSense g && g.nearby > 0)
                return "FB.StatSwarm".Translate(Mathf.Min(g.nearby, Gene_SwarmSense.MaxCounted)) + ": x" + g.WorkSpeedFactor.ToStringPercent();
            return null;
        }

        private static Gene_SwarmSense Get(Pawn p)
        {
            Gene_SwarmSense g = p.genes?.GetFirstGeneOfType<Gene_SwarmSense>();
            return g != null && g.Active ? g : null;
        }
    }

    // =====================================================================================
    // Fly brain upside: +10% work speed while doing a job from one of its own priority-1 work types.
    // =====================================================================================
    public class StatPart_FlyFocus : StatPart
    {
        public const float Factor = 1.10f;

        private static bool Applies(StatRequest req)
        {
            if (!(req.Thing is Pawn p) || !FlyRegistry.TryGet(p, out Gene_FlyBrain g)) return false;
            WorkTypeDef wt = p.CurJob?.workGiverDef?.workType;
            return wt != null && g.PriorityFor(wt) == 1;
        }

        public override void TransformValue(StatRequest req, ref float val)
        {
            if (Applies(req)) val *= Factor;
        }

        public override string ExplanationPart(StatRequest req) =>
            Applies(req) ? "FB.StatFocus".Translate() + ": x" + Factor.ToStringPercent() : null;
    }

    [StaticConstructorOnStartup]
    public static class FlyStatParts
    {
        static FlyStatParts()
        {
            StatDef s = StatDefOf.WorkSpeedGlobal;
            if (s.parts == null) s.parts = new List<StatPart>();
            s.parts.Add(new StatPart_FlySwarm { parentStat = s });
            s.parts.Add(new StatPart_FlyFocus { parentStat = s });
        }
    }
}
