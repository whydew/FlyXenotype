using System;
using System.Linq;
using FlyXenotype.Brain;
static class Probe
{
    static void Env(string n, ref short v) { var e = Environment.GetEnvironmentVariable(n); if (e != null) v = short.Parse(e); }
    static void EnvI(string n, ref int v) { var e = Environment.GetEnvironmentVariable(n); if (e != null) v = int.Parse(e); }
    public static void ApplyEnv()
    {
        Env("R5", ref FlyBrainTopology.RestToR5); Env("DFB", ref FlyBrainTopology.RestToDfb);
        Env("OUT", ref FlyBrainTopology.DfbToSleep); Env("DIRECT", ref FlyBrainTopology.DirectRestToSleep);
        Env("HEL", ref FlyBrainTopology.HeliconToWork);
        EnvI("CLOCK", ref FlyCircadian.ClockGainPct); EnvI("LIGHT", ref FlyCircadian.LightGainPct);
    }
    public static void Run()
    {
        ApplyEnv();
        var t = FlyBrainTopology.BuildDefault(15, 20, 0, 2, 3, 6, true);
        Console.WriteLine("rest  | hour: dFB/SleepDN/WorkDN ...");
        foreach (double restLevel in (Environment.GetEnvironmentVariable("RESTS") ?? "0.9,0.75,0.6,0.45,0.3").Split(',').Select(double.Parse))
        {
            var line = $"{restLevel:0.00} |";
            foreach (int h in (Environment.GetEnvironmentVariable("HOURS") ?? "2,6,10,14,18,22").Split(',').Select(int.Parse))
            {
                var s = new FlyBrainState(); s.Reset(t); s.BuildJitter(t, 1);
                var inp = new int[t.NeuronCount]; long[] acc = new long[t.NeuronCount];
                for (int r = 0; r < 8; r++)
                {
                    Array.Clear(inp, 0, inp.Length);
                    inp[0] = (int)(1.6 * (1 - restLevel) * 65536); inp[t.ModeStart] = (int)(0.55 * 65536);
                    inp[10] = inp[11] = (int)(0.3 * 65536); inp[14] = 32768;
                    FlyCircadian.Fill(t, inp, h * 60);
                    FlyBrain.Evaluate(t, s, inp, 1, r * 250);
                    if (r >= 4) for (int i = 0; i < t.NeuronCount; i++) acc[i] += s.Counts[i];
                }
                long dfb = 0; foreach (int d in FlyConnectomeSleep.DfbSleep) dfb += acc[t.ConnStart + d];
                line += $" {h:00}:{dfb / 4,3}/{acc[t.ModeStart + 1] / 4,2}/{acc[t.ModeStart] / 4,2}";
            }
            Console.WriteLine(line);
        }
    }
}
