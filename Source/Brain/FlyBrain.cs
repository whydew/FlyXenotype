// Fixed-point leaky integrate-and-fire micro-connectome.
// No Verse / Unity references: this file is unit-testable outside RimWorld.
// Every value that persists between evaluations is an integer, so the result is
// bit-identical on every MP client regardless of FPU rounding mode.
namespace FlyXenotype.Brain
{
    /// <summary>Stateless SplitMix64-style hash. Replaces Verse.Rand for all brain noise.</summary>
    public static class FlyNoise
    {
        public static uint Hash(int a, int b, int c, int d)
        {
            ulong x = Mix((ulong)(uint)a);
            x = Mix(x ^ ((ulong)(uint)b << 32));
            x = Mix(x ^ (uint)c);
            x = Mix(x ^ ((ulong)(uint)d << 17));
            return (uint)x;
        }

        private static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// Shared, read-only wiring (CSR by post-synaptic neuron). Built once at startup from defs
    /// or from a distilled connectome asset. Weights are Q8 (256 = 1.0) with Dale's-law sign baked in.
    /// </summary>
    public sealed class FlyBrainTopology
    {
        public const int CentralCount = 16;
        public const int ModeCount = 4; // Work, Sleep, Joy, Meditate (order = FlyMode - 1)

        public readonly int SensorCount, WorkCount, NeuronCount;
        public readonly int CentralStart, ModeStart, WorkStart;
        public readonly int[] RowStart;   // length NeuronCount + 1
        public readonly ushort[] Pre;     // pre-synaptic neuron per edge
        public readonly short[] Weight;   // Q8 signed weight per edge
        public readonly int Hash;         // logged at startup; must match across clients

        public static FlyBrainTopology Current;

        private FlyBrainTopology(int sensors, int works, int[] rowStart, ushort[] pre, short[] weight)
        {
            SensorCount = sensors;
            WorkCount = works;
            CentralStart = sensors;
            ModeStart = CentralStart + CentralCount;
            WorkStart = ModeStart + ModeCount;
            NeuronCount = WorkStart + works;
            RowStart = rowStart;
            Pre = pre;
            Weight = weight;
            uint h = 2166136261u;
            for (int i = 0; i < weight.Length; i++) { h = (h ^ (uint)(pre[i] << 16 | (ushort)weight[i])) * 16777619u; }
            Hash = (int)h;
        }

        /// <summary>
        /// Hand-authored default wiring using flybrain motifs:
        /// sparse sensory expansion into a central pool, lateral inhibition in the central pool,
        /// mutual inhibition between mode DNs, rest modes inhibiting all work DNs.
        /// interoSensors = indices of rest/joy/psyfocus sensors that drive Sleep/Joy/Meditate.
        /// </summary>
        public static FlyBrainTopology BuildDefault(int sensorCount, int workCount, int restSensor, int joySensor, int focusSensor, int crisisSensor)
        {
            int central = sensorCount, mode = central + CentralCount, work = mode + ModeCount;
            int n = work + workCount;
            var rows = new System.Collections.Generic.List<(ushort pre, short w)>[n];
            for (int i = 0; i < n; i++) rows[i] = new System.Collections.Generic.List<(ushort, short)>(8);

            // Sensors -> central: each central neuron samples 4 sensors (deterministic hash, no Rand).
            for (int c = 0; c < CentralCount; c++)
                for (int k = 0; k < 4; k++)
                    rows[central + c].Add(((ushort)(FlyNoise.Hash(c, k, 7, 11) % (uint)sensorCount), 160));
            // Central lateral inhibition (ring, GABA-like).
            for (int c = 0; c < CentralCount; c++)
            {
                rows[central + c].Add(((ushort)(central + (c + 1) % CentralCount), -96));
                rows[central + c].Add(((ushort)(central + (c + CentralCount - 1) % CentralCount), -96));
            }
            // Interoceptive sensors -> mode DNs.
            rows[mode + 1].Add(((ushort)restSensor, 384));   // Sleep
            rows[mode + 2].Add(((ushort)joySensor, 320));    // Joy
            rows[mode + 3].Add(((ushort)focusSensor, 320));  // Meditate
            rows[mode + 0].Add(((ushort)crisisSensor, 512)); // Work (crisis)
            for (int c = 0; c < CentralCount; c++) rows[mode + 0].Add(((ushort)(central + c), 24)); // colony salience -> Work
            // Mode DNs mutually inhibit.
            for (int a = 0; a < ModeCount; a++)
                for (int b = 0; b < ModeCount; b++)
                    if (a != b) rows[mode + a].Add(((ushort)(mode + b), -192));
            // Work DNs: gated by Work mode, suppressed by rest modes, weak central drive.
            for (int w = 0; w < workCount; w++)
            {
                rows[work + w].Add(((ushort)(mode + 0), 128));
                rows[work + w].Add(((ushort)(mode + 1), -256));
                rows[work + w].Add(((ushort)(mode + 2), -160));
                rows[work + w].Add(((ushort)(mode + 3), -160));
                rows[work + w].Add(((ushort)(central + w % CentralCount), 32));
            }

            var rowStart = new int[n + 1];
            int edges = 0;
            for (int i = 0; i < n; i++) { rowStart[i] = edges; edges += rows[i].Count; }
            rowStart[n] = edges;
            var pre = new ushort[edges];
            var wt = new short[edges];
            for (int i = 0, e = 0; i < n; i++)
                foreach (var (p, w) in rows[i]) { pre[e] = p; wt[e] = w; e++; }
            return new FlyBrainTopology(sensorCount, workCount, rowStart, pre, wt);
        }
    }

    /// <summary>Per-pawn mutable state. Owned and saved by Gene_FlyBrain.</summary>
    public sealed class FlyBrainState
    {
        public int[] V;          // membrane, Q16 (0 = rest, 65536 = threshold)
        public int[] ISyn;       // synaptic current, Q16
        public byte[] Refractory;
        public bool[] Spiked;    // spikes from the last substep
        public ushort[] Counts;  // spike counts in the last evaluation window (read by decoders and UI)
        public sbyte[] Jitter;   // per-edge personality, derived from the pawn id; recomputed on load, not saved

        public bool Matches(FlyBrainTopology t) => V != null && V.Length == t.NeuronCount;

        public void BuildJitter(FlyBrainTopology t, int personalitySeed)
        {
            Jitter = new sbyte[t.Weight.Length];
            for (int e = 0; e < Jitter.Length; e++)
                Jitter[e] = (sbyte)((int)(FlyNoise.Hash(personalitySeed, e, 0, 0) & 63) - 31); // -31..32 of 256 = +/-12%
        }

        public void Reset(FlyBrainTopology t)
        {
            V = new int[t.NeuronCount];
            ISyn = new int[t.NeuronCount];
            Refractory = new byte[t.NeuronCount];
            Spiked = new bool[t.NeuronCount];
            Counts = new ushort[t.NeuronCount];
        }

        public int StateHash()
        {
            uint h = 2166136261u;
            for (int i = 0; i < V.Length; i++) h = (h ^ (uint)V[i]) * 16777619u ^ (uint)ISyn[i] * 31u;
            return (int)h;
        }
    }

    public static class FlyBrain
    {
        public const int One = 65536;
        public const int Steps = 32;
        private const int DecaySyn = 53656;   // round(65536 * e^(-1/5)), tau_syn = 5 steps
        private const int KMem = 19661;       // 0.3: membrane integration per step
        private const int InputGainShift = 1; // external input x2: an input of 0.5 sits exactly at threshold
        private const int RefractorySteps = 2;
        private const int NoiseMask = 0x0FFF; // +/- ~3% of threshold

        /// <summary>
        /// Advance the network Steps substeps with constant external input (Q16 per neuron).
        /// s.Jitter must be built (BuildJitter); noiseSeed varies per evaluation (e.g. tick).
        /// Allocation-free. Pure function of (state, input, seeds).
        /// </summary>
        public static void Evaluate(FlyBrainTopology t, FlyBrainState s, int[] input, int personalitySeed, int noiseSeed)
        {
            int n = t.NeuronCount;
            System.Array.Clear(s.Counts, 0, n);
            bool[] spk = s.Spiked;
            for (int step = 0; step < Steps; step++)
            {
                // 1. Synaptic current: decay, then add weighted spikes from the previous substep.
                for (int post = 0; post < n; post++)
                {
                    long acc = 0;
                    for (int e = t.RowStart[post], end = t.RowStart[post + 1]; e < end; e++)
                    {
                        if (!spk[t.Pre[e]]) continue;
                        acc += (long)t.Weight[e] * (256 + s.Jitter[e]); // Q8 * Q8 = Q16
                    }
                    s.ISyn[post] = (int)(((long)s.ISyn[post] * DecaySyn) >> 16) + (int)acc;
                }
                // 2. Membrane update and spiking.
                for (int i = 0; i < n; i++)
                {
                    int noise = (int)(FlyNoise.Hash(noiseSeed, step, i, personalitySeed) & NoiseMask) - (NoiseMask >> 1);
                    int drive = (input[i] << InputGainShift) + s.ISyn[i] + noise;
                    s.V[i] += (int)(((long)(drive - s.V[i]) * KMem) >> 16);
                    if (s.V[i] < -One) s.V[i] = -One;

                    bool fire = false;
                    if (s.Refractory[i] > 0) s.Refractory[i]--;
                    else if (s.V[i] >= One)
                    {
                        fire = true;
                        s.V[i] = 0;
                        s.Refractory[i] = RefractorySteps;
                        if (s.Counts[i] < ushort.MaxValue) s.Counts[i]++;
                    }
                    // Safe to overwrite in place: step 1 already consumed every previous spike.
                    spk[i] = fire;
                }
            }
        }
    }
}
