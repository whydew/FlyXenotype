using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FlyXenotype.Brain;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FlyXenotype
{
    /// <summary>Local, display-only preferences. The simulation never reads these.</summary>
    public class FlySettings : ModSettings
    {
        public bool hideLockedCells;
        public bool logHashes;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref hideLockedCells, "hideLockedCells");
            Scribe_Values.Look(ref logHashes, "logHashes");
        }
    }

    public class FlyMod : Mod
    {
        public static FlySettings Settings;

        public FlyMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<FlySettings>();
            new Harmony("matt.flyxenotype").PatchAll();
        }
    }

    /// <summary>Builds the shared topology once defs are loaded. Identical on every client (same defs, same order).</summary>
    [StaticConstructorOnStartup]
    public static class FlyStartup
    {
        static FlyStartup()
        {
            int sensors = System.Enum.GetValues(typeof(FlySensor)).Length;
            int works = DefDatabase<WorkTypeDef>.DefCount;
            FlyBrainTopology.Current = FlyBrainTopology.BuildDefault(sensors, works,
                (int)FlySensor.RestDeficit, (int)FlySensor.JoyDeficit, (int)FlySensor.PsyfocusDeficit, (int)FlySensor.Crisis);
            Log.Message($"[Fly] topology {FlyBrainTopology.Current.NeuronCount} neurons, {FlyBrainTopology.Current.Weight.Length} edges, hash {FlyBrainTopology.Current.Hash:X8}");
            FlyDiag.LogPatches(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority));
            FlyDiag.LogPatches(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.SetAssignment));
        }
    }
}

namespace FlyXenotype.Compat
{
    /// <summary>
    /// Same pattern as WorkPriorityPresets: detect MP by package id, confine every
    /// Multiplayer.API reference to NoInlining methods so the mod loads without MP.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MultiplayerCompat
    {
        private static bool active;
        public static bool Active => active;
        public static bool InMultiplayer => active && InMultiplayerInternal();
        public static bool IsExecutingSyncCommand => active && IsExecutingInternal();

        static MultiplayerCompat()
        {
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                string id = mod.PackageId?.ToLowerInvariant() ?? string.Empty;
                if (id.StartsWith("rwmt.multiplayer")) { active = true; break; }
            }
            if (!active) return;
            try { WireUp(); Log.Message("[Fly] Multiplayer detected; sync methods registered."); }
            catch (System.Exception e) { active = false; Log.Warning("[Fly] MP wire-up failed, disabling: " + e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WireUp()
        {
            Multiplayer.API.MP.RegisterSyncMethod(typeof(FlySync), nameof(FlySync.SetTemperament));
            Multiplayer.API.MP.RegisterSyncMethod(typeof(FlySync), nameof(FlySync.DebugResetBrain)).SetDebugOnly();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool InMultiplayerInternal() => Multiplayer.API.MP.enabled && Multiplayer.API.MP.IsInMultiplayer;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsExecutingInternal() => Multiplayer.API.MP.enabled && Multiplayer.API.MP.IsExecutingSyncCommand;
    }

    /// <summary>
    /// Player-intent actions. Call these normally from gizmos; with MP they become one
    /// command replayed on all clients, in singleplayer they run inline.
    /// Brain decisions are NOT here: they are recomputed on every client.
    /// </summary>
    public static class FlySync
    {
        public static void SetTemperament(Pawn pawn, int value)
        {
            if (FlyRegistry.TryGet(pawn, out Gene_FlyBrain g)) g.temperament = value < -1 ? -1 : value > 1 ? 1 : value;
        }

        public static void DebugResetBrain(Pawn pawn)
        {
            if (FlyRegistry.TryGet(pawn, out Gene_FlyBrain g) && FlyBrainTopology.Current != null)
                g.brain.Reset(FlyBrainTopology.Current);
        }
    }
}
