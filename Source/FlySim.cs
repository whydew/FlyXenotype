using System.Collections.Generic;
using FlyXenotype.Brain;
using RimWorld;
using UnityEngine;
using Verse;

namespace FlyXenotype
{
    public enum FlyMode : byte { Anything = 0, Work = 1, Sleep = 2, Joy = 3, Meditate = 4 }

    public enum FlySensor
    {
        RestDeficit, FoodDeficit, JoyDeficit, PsyfocusDeficit, LowMood, Pain,
        Crisis, Fire, FoodScarcity, Injured, Blueprints, Haulables, Harvestable, Bills, Research
    }

    /// <summary>XML: which colony sensor pushes which work types, and when a sensor counts as a crisis.</summary>
    public class FlySensorDef : Def
    {
        public FlySensor sensor;
        public float crisisThreshold = 2f; // > 1 means "never a crisis"
        public List<WorkTypeWeight> weights = new List<WorkTypeWeight>();
    }

    public class WorkTypeWeight
    {
        public WorkTypeDef workType;
        public float weight;
    }

    /// <summary>
    /// Pawn -> gene lookup. Keyed by thingIDNumber; only ever looked up, never enumerated,
    /// so dictionary ordering can't leak into the simulation.
    /// </summary>
    public static class FlyRegistry
    {
        private static readonly Dictionary<int, Gene_FlyBrain> byId = new Dictionary<int, Gene_FlyBrain>();

        // A pawn can carry two Gene_FlyBrain (endogene + xenogene); only the active one counts.
        public static void Register(Gene_FlyBrain g)
        {
            if (g?.pawn == null) return;
            int id = g.pawn.thingIDNumber;
            if (!byId.TryGetValue(id, out Gene_FlyBrain cur) || cur == g || cur.pawn != g.pawn || !cur.Active || g.Active)
                byId[id] = g;
        }

        public static void Unregister(Gene_FlyBrain g)
        {
            if (g?.pawn == null) return;
            int id = g.pawn.thingIDNumber;
            if (byId.TryGetValue(id, out Gene_FlyBrain cur) && cur == g)
            {
                byId.Remove(id);
                Gene_FlyBrain other = FindActive(g.pawn, g);
                if (other != null) byId[id] = other;
            }
        }

        public static void Clear() => byId.Clear();

        public static bool TryGet(Pawn p, out Gene_FlyBrain g)
        {
            g = null;
            if (p == null || !byId.TryGetValue(p.thingIDNumber, out g)) return false; // non-Fly fast path
            if (g.pawn == p && g.Active) return true;
            // Cached gene went inactive (overridden, removed): look for another active copy.
            g = FindActive(p, null);
            if (g == null) return false;
            byId[p.thingIDNumber] = g;
            return true;
        }

        private static Gene_FlyBrain FindActive(Pawn p, Gene_FlyBrain except)
        {
            List<Gene> genes = p.genes?.GenesListForReading;
            if (genes == null) return null;
            for (int i = 0; i < genes.Count; i++)
                if (genes[i] is Gene_FlyBrain fb && fb != except && fb.Active) return fb;
            return null;
        }

        public static bool IsFly(Pawn p) => TryGet(p, out _);
    }

    /// <summary>Scope flag: only code inside a FlyWriter scope may change a Fly's priorities or assignment.</summary>
    public static class FlyWriter
    {
        public static int Depth;
        public static bool Active => Depth > 0;
        public struct Scope : System.IDisposable { public void Dispose() => Depth--; }
        public static Scope Open() { Depth++; return new Scope(); }

        // Direct writes to Pawn_WorkSettings. Bypasses SetPriority on purpose: MP's WorkPrioritySameValue
        // prefix compares against GetPriority, which vanilla maps to 3 when manual priorities are off, so
        // some SetPriority calls would be dropped. Runs only in simulation code, identically on all clients.
        private static readonly HarmonyLib.AccessTools.FieldRef<Pawn_WorkSettings, DefMap<WorkTypeDef, int>> PrioField =
            HarmonyLib.AccessTools.FieldRefAccess<Pawn_WorkSettings, DefMap<WorkTypeDef, int>>("priorities");
        private static readonly HarmonyLib.AccessTools.FieldRef<Pawn_WorkSettings, bool> DirtyField =
            HarmonyLib.AccessTools.FieldRefAccess<Pawn_WorkSettings, bool>("workGiversDirty");

        public static int RawPriority(Pawn pawn, WorkTypeDef w) => PrioField(pawn.workSettings)[w];

        public static void WriteRaw(Pawn pawn, WorkTypeDef w, int value)
        {
            PrioField(pawn.workSettings)[w] = value;
            DirtyField(pawn.workSettings) = true;
            if (value == 0) pawn.jobs?.Notify_WorkTypeDisabled(w); // same as vanilla SetPriority(w, 0): drop current job of that type
        }
    }

    public class Gene_FlyBrain : Gene
    {
        // ---- persisted brain + decision state ----
        public FlyBrainState brain = new FlyBrainState();
        public FlyMode mode = FlyMode.Anything;
        public int modeSinceTick;
        public int lastEvalTick = -99999;
        public int temperament;                 // -1 idle, 0 balanced, +1 diligent (synced via FlySync)
        public int[] priorities;                // brain-chosen priority per WorkTypeDef.index
        public byte[] modeHistory = new byte[24]; // last mode seen in each local hour, for the Schedule tab
        public bool crisis;
        public List<WorkTypeDef> crisisWork = new List<WorkTypeDef>();

        private static int[] inputBuffer; // shared scratch; simulation is single-threaded

        public const int ModeMargin = 3;     // spikes a challenger mode needs over the current one
        public const int ModeMinTicks = 1250;
        public const int BucketMargin = 2;

        public override void PostAdd() { base.PostAdd(); FlyRegistry.Register(this); }
        public override void PostRemove() { FlyRegistry.Unregister(this); base.PostRemove(); }

        public TimeAssignmentDef CurrentAssignmentDef
        {
            get
            {
                switch (mode)
                {
                    case FlyMode.Work: return TimeAssignmentDefOf.Work;
                    case FlyMode.Sleep: return TimeAssignmentDefOf.Sleep;
                    case FlyMode.Joy: return TimeAssignmentDefOf.Joy;
                    case FlyMode.Meditate:
                        return ModsConfig.RoyaltyActive && pawn.HasPsylink ? TimeAssignmentDefOf.Meditate : TimeAssignmentDefOf.Joy;
                    default: return TimeAssignmentDefOf.Anything;
                }
            }
        }

        public bool HasDecided => priorities != null;
        public int PriorityFor(WorkTypeDef w) => priorities != null && w.index < priorities.Length ? priorities[w.index] : 0;

        /// <summary>
        /// The only entry point that changes Fly behaviour. Must be called from simulation code
        /// (MapComponentTick or the ThinkTree), never from UI.
        /// </summary>
        public void Evaluate(MapComponent_FlyColonySense sense, int tick)
        {
            FlyBrainTopology t = FlyBrainTopology.Current;
            if (t == null || !pawn.Spawned || pawn.workSettings == null || !pawn.workSettings.EverWork) return;
            if (!sense.Ready) sense.RefreshCheap(); // deterministic: same tick, same state on every client
            if (!brain.Matches(t)) brain.Reset(t);
            if (brain.Jitter == null) brain.BuildJitter(t, pawn.thingIDNumber);
            if (inputBuffer == null || inputBuffer.Length < t.NeuronCount) inputBuffer = new int[t.NeuronCount];

            System.Array.Clear(inputBuffer, 0, inputBuffer.Length);
            FlyPawnSense.Fill(pawn, sense, t, inputBuffer, temperament);
            FlyBrain.Evaluate(t, brain, inputBuffer, pawn.thingIDNumber, tick);

            crisis = sense.CrisisActive;
            crisisWork.Clear();
            if (crisis) crisisWork.AddRange(sense.CrisisWorkTypes);

            DecideMode(t, tick);
            ReconcilePriorities(t);
            WakeForCrisis();

            modeHistory[GenLocalDate.HourOfDay(pawn)] = (byte)mode;
            lastEvalTick = tick;

            if (Prefs.DevMode && FlyMod.Settings.logHashes)
                Log.Message($"[FLYBRAIN] {tick} {pawn.thingIDNumber} {brain.StateHash():X8} {mode}");
        }

        private void DecideMode(FlyBrainTopology t, int tick)
        {
            // Hard overrides first; they use synced need values only.
            float rest = pawn.needs?.rest?.CurLevel ?? 1f;
            if (rest < 0.10f) { SetMode(FlyMode.Sleep, tick); return; }       // exhaustion beats everything
            if (crisis && rest > 0.30f) { SetMode(FlyMode.Work, tick); return; } // acute crisis, only if rested enough

            int best = -1, bestCount = -1;
            for (int m = 0; m < FlyBrainTopology.ModeCount; m++)
            {
                int c = brain.Counts[t.ModeStart + m];
                if (c > bestCount) { bestCount = c; best = m; } // ties keep the lower index: stable
            }
            FlyMode challenger = bestCount <= 0 ? FlyMode.Anything : (FlyMode)(best + 1);
            if (challenger == mode) return;

            int current = mode == FlyMode.Anything ? 0 : brain.Counts[t.ModeStart + (int)mode - 1];
            bool committed = tick - modeSinceTick < ModeMinTicks;
            if (!committed && bestCount >= current + ModeMargin) SetMode(challenger, tick);
        }

        private void SetMode(FlyMode m, int tick)
        {
            if (m == mode) return;
            mode = m;
            modeSinceTick = tick;
        }

        private void ReconcilePriorities(FlyBrainTopology t)
        {
            List<WorkTypeDef> defs = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            bool fresh = priorities == null || priorities.Length != defs.Count;
            if (fresh) priorities = new int[defs.Count];

            // Rank capable work types by DN spike count. Insertion sort over a small list: stable,
            // ties broken by naturalPriority then index. Never by hash code.
            var order = FlyScratch.Order;
            order.Clear();
            for (int i = 0; i < defs.Count && i < t.WorkCount; i++)
                if (!pawn.WorkTypeIsDisabled(defs[i])) order.Add(i);
            order.Sort((a, b) =>
            {
                int ca = brain.Counts[t.WorkStart + a], cb = brain.Counts[t.WorkStart + b];
                if (ca != cb) return cb.CompareTo(ca);
                int na = defs[a].naturalPriority, nb = defs[b].naturalPriority;
                if (na != nb) return nb.CompareTo(na);
                return a.CompareTo(b);
            });

            using (FlyWriter.Open())
            {
                for (int rank = 0; rank < order.Count; rank++)
                {
                    int i = order[rank];
                    WorkTypeDef w = defs[i];
                    int target = rank < 2 ? 1 : rank < 5 ? 2 : rank < 9 ? 3 : 4;
                    bool forced = false;
                    bool selfCare = IsSelfCare(w);
                    if (crisisWork.Contains(w)) { target = 1; forced = true; }
                    else if (selfCare && target > 2) { target = 2; forced = true; } // Patient / bed rest: never below 2
                    int old = priorities[i];
                    // Hysteresis: only move one bucket when the count clearly left the old bucket's range.
                    if (!forced && old != 0 && target != old && System.Math.Abs(target - old) == 1)
                    {
                        int boundaryRank = target < old ? rank + 1 : rank - 1;
                        if (boundaryRank >= 0 && boundaryRank < order.Count)
                        {
                            int diff = System.Math.Abs(brain.Counts[t.WorkStart + i] - brain.Counts[t.WorkStart + order[boundaryRank]]);
                            if (diff < BucketMargin) target = old;
                        }
                    }
                    // Turn work off entirely when its drive is weak relative to this Fly's strongest drive.
                    // Hysteresis: off below 1/4 of the top count, back on only above 1/2. The top MinActive
                    // types and self-care are never turned off, so a Fly always has something to do.
                    if (!forced && !selfCare && rank >= MinActive)
                    {
                        int c = brain.Counts[t.WorkStart + i], max = brain.Counts[t.WorkStart + order[0]];
                        bool wasOff = old == 0 && !fresh;
                        if (wasOff ? c * 2 < max : c * 4 < max) target = 0;
                    }
                    priorities[i] = target;
                    // Reconcile against what vanilla actually stores, not our cache, so stale values heal.
                    if (FlyWriter.RawPriority(pawn, w) != target) FlyWriter.WriteRaw(pawn, w, target);
                }
            }
        }

        public const int MinActive = 2;

        private static bool IsSelfCare(WorkTypeDef w) => w.defName == "Patient" || w.defName == "PatientBedRest";

        private void WakeForCrisis()
        {
            if (!crisis || pawn.jobs?.curDriver == null) return;
            if (pawn.jobs.curDriver.asleep && (pawn.needs?.rest?.CurLevel ?? 0f) > 0.3f)
                pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref mode, "flyMode", FlyMode.Anything);
            Scribe_Values.Look(ref modeSinceTick, "flyModeSince");
            Scribe_Values.Look(ref lastEvalTick, "flyLastEval", -99999);
            Scribe_Values.Look(ref temperament, "flyTemperament");
            Scribe_Values.Look(ref crisis, "flyCrisis");
            FlySave.LookInts(ref priorities, "flyPriorities");
            FlySave.LookInts(ref brain.V, "flyV");
            FlySave.LookInts(ref brain.ISyn, "flyISyn");
            FlySave.LookBytes(ref brain.Refractory, "flyRef");
            FlySave.LookBools(ref brain.Spiked, "flySpk");
            FlySave.LookBytes(ref modeHistory, "flyHistory");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (modeHistory == null || modeHistory.Length != 24) modeHistory = new byte[24];
                if (brain.V != null) brain.Counts = new ushort[brain.V.Length];
                brain.Jitter = null; // rebuilt on next Evaluate
                crisisWork.Clear();  // re-derived on next Evaluate; crisis flag above keeps the ThinkNode consistent
                FlyRegistry.Register(this);
            }
        }
    }

    internal static class FlyScratch
    {
        public static readonly List<int> Order = new List<int>(64);
    }

    internal static class FlySave
    {
        public static void LookInts(ref int[] arr, string label)
        {
            List<int> list = arr != null ? new List<int>(arr) : null;
            Scribe_Collections.Look(ref list, label, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars) arr = list?.ToArray();
        }

        public static void LookBytes(ref byte[] arr, string label)
        {
            int[] tmp = arr == null ? null : System.Array.ConvertAll(arr, b => (int)b);
            LookInts(ref tmp, label);
            if (Scribe.mode == LoadSaveMode.LoadingVars) arr = tmp == null ? null : System.Array.ConvertAll(tmp, i => (byte)i);
        }

        public static void LookBools(ref bool[] arr, string label)
        {
            int[] tmp = arr == null ? null : System.Array.ConvertAll(arr, b => b ? 1 : 0);
            LookInts(ref tmp, label);
            if (Scribe.mode == LoadSaveMode.LoadingVars) arr = tmp == null ? null : System.Array.ConvertAll(tmp, i => i != 0);
        }
    }

    /// <summary>Per-pawn sensors -> external input currents (Q16). Reads synced state only.</summary>
    public static class FlyPawnSense
    {
        private static int Q(float x) => (int)(Mathf.Clamp(x, -4f, 4f) * FlyBrain.One);

        public static void Fill(Pawn pawn, MapComponent_FlyColonySense sense, FlyBrainTopology t, int[] input, int temperament)
        {
            Need_Rest rest = pawn.needs?.rest;
            Need_Food food = pawn.needs?.food;
            Need_Joy joy = pawn.needs?.joy;
            Need_Mood mood = pawn.needs?.mood;

            input[(int)FlySensor.RestDeficit] = Q(rest != null ? 1.6f * (1f - rest.CurLevel) : 0f);
            input[(int)FlySensor.FoodDeficit] = Q(food != null ? 1f - food.CurLevel : 0f);
            input[(int)FlySensor.JoyDeficit] = Q(joy != null ? 1.4f * (1f - joy.CurLevel) : 0f);
            input[(int)FlySensor.PsyfocusDeficit] = Q(ModsConfig.RoyaltyActive && pawn.HasPsylink && pawn.psychicEntropy != null
                ? 1.2f * (1f - pawn.psychicEntropy.CurrentPsyfocus) : 0f);
            input[(int)FlySensor.LowMood] = Q(mood != null ? 1f - mood.CurLevel : 0f);
            input[(int)FlySensor.Pain] = Q(pawn.health.hediffSet.PainTotal);
            for (int s = (int)FlySensor.Crisis; s < t.SensorCount; s++) input[s] = sense.SensorQ16[s];
            // Tonic drive on the Work mode neuron: when no need is pressing, the Fly works.
            input[t.ModeStart] = Q(0.55f + 0.1f * temperament);

            // Work DNs: colony demand + skill + passion - learning saturation + temperament (+ sugar drive).
            bool sugarDrive = pawn.genes != null && pawn.genes.HasActiveGene(FB_DefOf.FB_SugarDrive);
            List<WorkTypeDef> defs = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count && i < t.WorkCount; i++)
            {
                WorkTypeDef w = defs[i];
                if (pawn.WorkTypeIsDisabled(w)) continue;
                float skill = pawn.skills != null ? pawn.skills.AverageOfRelevantSkillsFor(w) / 20f : 0f;
                float passion = 0f, saturation = 0f;
                if (pawn.skills != null)
                    for (int k = 0; k < w.relevantSkills.Count; k++)
                    {
                        SkillRecord rec = pawn.skills.GetSkill(w.relevantSkills[k]);
                        passion = Mathf.Max(passion, rec.passion == Passion.Major ? 1f : rec.passion == Passion.Minor ? 0.5f : 0f);
                        if (rec.LearningSaturatedToday) saturation = 0.25f;
                    }
                float sugar = sugarDrive && (w.defName == "Cooking" || w.defName == "Growing") ? 0.4f * sense.SweetScarcity : 0f;
                input[t.WorkStart + i] = sense.DemandQ16[i] + Q(0.45f + 0.5f * skill + 0.6f * passion - saturation + 0.1f * temperament + sugar);
            }
        }
    }

    /// <summary>
    /// Colony-level sensors, shared by all Flies on the map. Also drives staggered evaluation.
    /// RimWorld instantiates every MapComponent subclass for every map automatically.
    /// </summary>
    public class MapComponent_FlyColonySense : MapComponent
    {
        public const int Interval = 250;
        public readonly int[] SensorQ16 = new int[System.Enum.GetValues(typeof(FlySensor)).Length];
        public int[] DemandQ16 = new int[0];
        public bool CrisisActive;
        public float SweetScarcity; // 0 = plenty of sweet food stored, 1 = none (Sugar drive gene)
        public readonly List<WorkTypeDef> CrisisWorkTypes = new List<WorkTypeDef>();
        private float harvestable, bills;

        public MapComponent_FlyColonySense(Map map) : base(map) { }

        public bool Ready => DemandQ16.Length == DefDatabase<WorkTypeDef>.DefCount;

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RefreshExpensive();
            RefreshCheap(); // sensors valid before any pawn's first staggered evaluation
        }

        public override void MapComponentTick()
        {
            int tick = Find.TickManager.TicksGame; // map-local under MP async time
            if (tick % Interval == 0) RefreshCheap();
            if (tick % 1000 == 0) RefreshExpensive();

            List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned; // deterministic order
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if ((tick + p.thingIDNumber) % Interval != 0) continue;
                if (FlyRegistry.TryGet(p, out Gene_FlyBrain g)) g.Evaluate(this, tick);
            }
        }

        public void RefreshCheap()
        {
            int colonists = Mathf.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            float days = map.resourceCounter.TotalHumanEdibleNutrition / (colonists * 1.6f);
            int injured = 0;
            List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < pawns.Count; i++) if (HealthAIUtility.ShouldBeTendedNowByPlayer(pawns[i])) injured++;

            Set(FlySensor.Fire, Mathf.Clamp01(map.fireWatcher.FireDanger / 20f));
            Set(FlySensor.FoodScarcity, Mathf.Clamp01((4f - days) / 4f));
            Set(FlySensor.Injured, Mathf.Clamp01(injured / 2f));
            Set(FlySensor.Blueprints, Mathf.Clamp01((map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count
                                                     + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count) / 20f));
            Set(FlySensor.Haulables, Mathf.Clamp01(map.listerHaulables.ThingsPotentiallyNeedingHauling().Count / 60f));
            Set(FlySensor.Research, Find.ResearchManager.GetProject() != null ? 0.5f : 0f);
            int sweets = 0;
            List<ThingDef> sweetDefs = FlySweets.SweetDefs;
            for (int i = 0; i < sweetDefs.Count; i++) sweets += map.resourceCounter.GetCount(sweetDefs[i]);
            SweetScarcity = Mathf.Clamp01(1f - sweets / (colonists * 10f));
            Gene_SwarmSense.RefreshMap(map);
            Set(FlySensor.Harvestable, harvestable);
            Set(FlySensor.Bills, bills);
            RecomputeDemand();
        }

        public void RefreshExpensive()
        {
            // Placeholders for the 1,000-tick scans (growing zones, bill stacks). Keep them tick-based.
            harvestable = 0f;
            bills = 0f;
        }

        private void Set(FlySensor s, float v) => SensorQ16[(int)s] = (int)(v * FlyBrain.One);

        private void RecomputeDemand()
        {
            List<WorkTypeDef> works = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            if (DemandQ16.Length != works.Count) DemandQ16 = new int[works.Count];
            System.Array.Clear(DemandQ16, 0, DemandQ16.Length);
            CrisisActive = false;
            CrisisWorkTypes.Clear();
            int crisisLevel = 0;

            List<FlySensorDef> sensors = DefDatabase<FlySensorDef>.AllDefsListForReading;
            for (int s = 0; s < sensors.Count; s++)
            {
                FlySensorDef def = sensors[s];
                int level = SensorQ16[(int)def.sensor];
                bool isCrisis = level >= (int)(def.crisisThreshold * FlyBrain.One);
                if (isCrisis && level > crisisLevel) crisisLevel = level;
                for (int k = 0; k < def.weights.Count; k++)
                {
                    WorkTypeWeight ww = def.weights[k];
                    DemandQ16[ww.workType.index] += (int)(((long)level * (int)(ww.weight * 256f)) >> 8);
                    if (isCrisis && !CrisisWorkTypes.Contains(ww.workType)) CrisisWorkTypes.Add(ww.workType);
                }
            }
            CrisisActive = CrisisWorkTypes.Count > 0;
            SensorQ16[(int)FlySensor.Crisis] = crisisLevel;
        }
    }
}

namespace FlyXenotype
{
    /// <summary>Clears the static registry whenever a game is created or loaded (ctor runs before genes load).</summary>
    public class GameComponent_Fly : GameComponent
    {
        private static bool loggedPatches;
        public GameComponent_Fly(Game game)
        {
            FlyRegistry.Clear();
            if (!loggedPatches)
            {
                loggedPatches = true; // patch list after every mod finished patching
                FlyDiag.LogPatches(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority));
            }
        }
    }
}

namespace FlyXenotype
{
    /// <summary>Temporary diagnostics for the Phase 3 live test. Remove once priority writes are confirmed.</summary>
    public static class FlyDiag
    {
        private static int guardLogs;

        public static void LogPatches(System.Type type, string method)
        {
            if (!Prefs.DevMode) return;
            var info = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(type, method));
            if (info == null) { Log.Message($"[FlyDiag] {type.Name}.{method}: no patches"); return; }
            string Fmt(System.Collections.Generic.IEnumerable<HarmonyLib.Patch> ps)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var p in ps) sb.Append($" [{p.owner} {p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name} prio={p.priority}]");
                return sb.ToString();
            }
            Log.Message($"[FlyDiag] {type.Name}.{method} prefixes:{Fmt(info.Prefixes)} postfixes:{Fmt(info.Postfixes)} transpilers:{Fmt(info.Transpilers)} finalizers:{Fmt(info.Finalizers)}");
        }

        public static void GuardRejected(Pawn pawn, int priority)
        {
            if (Prefs.DevMode && guardLogs++ < 10)
                Log.Message($"[FlyDiag] guard rejected SetPriority(..., {priority}) for {pawn.LabelShort}; writerDepth={FlyWriter.Depth}\n{new System.Diagnostics.StackTrace(2, false)}");
        }
    }
}
