// Offline check of the Fly brain's sleep/recreation schedule. Uses the real Source/Brain code;
// the RimWorld side (needs, DecideMode gates) is a simplified copy of Source/FlySim.cs.
// Usage: dotnet run -- [circuit|legacy] [days] [seed]
using System;
using System.Text;
using FlyXenotype.Brain;

static class Program
{
    const int Sensors = 15, Works = 20, Rest = 0, Joy = 2, Focus = 3, Crisis = 6;
    static bool Free = Environment.GetEnvironmentVariable("FREE") == "1";
    static int Q(double x) => (int)(Math.Clamp(x, -4, 4) * 65536);

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "probe") { Probe.Run(); return 0; }
        Probe.ApplyEnv();
        bool circuit = args.Length == 0 || args[0] != "legacy";
        int days = args.Length > 1 ? int.Parse(args[1]) : 4;
        int seed = args.Length > 2 ? int.Parse(args[2]) : 1234;
        var t = FlyBrainTopology.BuildDefault(Sensors, Works, Rest, Joy, Focus, Crisis, circuit);
        var s = new FlyBrainState(); s.Reset(t); s.BuildJitter(t, seed);
        var input = new int[t.NeuronCount];
        Console.WriteLine($"{(circuit ? "FlyWire circuit" : "legacy wiring")}: {t.NeuronCount} neurons, {t.Weight.Length} edges, hash {t.Hash:X8}");

        double rest = 0.8, joy = 0.6; bool asleep = false;
        int mode = 1, since = 0, joyCool = 0; // 1 Work 2 Sleep 3 Joy
        var sched = new char[days * 24];
        int[] modeHours = new int[4]; int sleepBlocks = 0; int prevMode = 1;
        var dfbByHour = new double[24]; var dfbN = new int[24];
        for (int tick = 0; tick < days * 60000; tick += 250)
        {
            int minute = (tick % 60000) * 1440 / 60000, hour = minute / 60;
            bool night = hour >= 22 || hour < 6;
            Array.Clear(input, 0, input.Length);
            input[Rest] = Q(1.6 * (1 - rest));
            input[Joy] = Q(2.2 * Math.Max(0, 0.6 - joy));
            input[10] = Q(0.3); input[11] = Q(0.3); input[14] = Q(0.5); // blueprints, haulables, research
            input[t.ModeStart] = Q(0.55);
            if (circuit) FlyCircadian.Fill(t, input, minute);
            else input[t.ModeStart + 1] = Q(night ? 0.45 : -0.35);
            FlyBrain.Evaluate(t, s, input, seed, tick);

            // --- DecideMode (copy of FlySim gates) ---
            int m = mode;
            if (rest < 0.10) m = 2;
            else if (!(mode == 2 && rest < 0.99 && tick - since < 30000))
            {
                bool sleepAllowed = rest < (circuit || night || Free ? 0.70 : 0.30); // legacy: old day/night gate
                bool cooling = tick < joyCool; bool latched = false;
                if (mode == 3)
                {
                    bool capped = tick - since >= 5000;
                    if (capped) joyCool = tick + 5000;
                    else if (joy < 0.80 && !(sleepAllowed && night)) latched = true;
                    cooling |= capped;
                }
                if (!latched)
                {
                    if (joy < 0.15 && !cooling) m = 3;
                    else
                    {
                        bool joyAllowed = joy < 0.40 && !cooling;
                        int best = -1, bc = -1;
                        for (int k = 0; k < 4; k++)
                        {
                            if (k == 1 && !sleepAllowed) continue; if (k == 2 && !joyAllowed) continue;
                            int c = s.Counts[t.ModeStart + k]; if (c > bc) { bc = c; best = k; }
                        }
                        int ch = bc <= 0 ? 0 : best + 1; if (ch == 4) ch = 3;
                        if (ch != 0 && ch != mode)
                        {
                            bool gated = (mode == 2 && !sleepAllowed) || (mode == 3 && !joyAllowed);
                            int cur = s.Counts[t.ModeStart + mode - 1];
                            if (gated || (tick - since >= 1250 && bc >= cur + 3)) m = ch;
                        }
                    }
                }
            }
            if (m != mode) { mode = m; since = tick; }

            // --- needs (per 250 ticks, approximating vanilla rates) ---
            if (mode == 2 && (asleep || rest < 0.75)) asleep = true; else asleep = false;
            if (asleep) { rest += 0.0095; if (rest >= 1) { rest = 1; asleep = false; } }
            else rest -= 0.00396;
            if (mode == 3) joy += 0.03; else joy -= 0.0025;
            rest = Math.Clamp(rest, 0, 1); joy = Math.Clamp(joy, 0, 1);

            if (circuit) { double d = 0; foreach (int x in FlyConnectomeSleep.DfbSleep) d += s.Counts[t.ConnStart + x]; dfbByHour[hour] += d; dfbN[hour]++; }
            if ((tick % 2500) == 0) { sched[tick / 2500] = mode == 1 ? 'W' : mode == 2 ? (asleep ? 'S' : 's') : 'J'; modeHours[mode]++; }
            if (mode == 2 && prevMode != 2) sleepBlocks++;
            prevMode = mode;
        }
        for (int d = 0; d < days; d++) Console.WriteLine($"day {d + 1}: {new string(sched, d * 24, 24)}");
        Console.WriteLine($"hours: work {modeHours[1]}, sleep {modeHours[2]}, joy {modeHours[3]}; sleep blocks {sleepBlocks} over {days} days");
        if (circuit)
        {
            var sb = new StringBuilder("dFB spikes/eval by hour: ");
            for (int h = 0; h < 24; h++) sb.Append($"{dfbByHour[h] / Math.Max(1, dfbN[h]):0} ");
            Console.WriteLine(sb);
        }
        return 0;
    }
}
