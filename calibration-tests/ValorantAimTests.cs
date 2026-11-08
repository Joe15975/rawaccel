using System;
using System.Diagnostics;
using grapher.Models.Calibration;

public static class ValorantAimTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static long Tick(int ms) { return (long)(ms * (double)Stopwatch.Frequency / 1000); }
    public static void Run()
    {
        var a = ValorantAim.CreateConditions(30, 42);
        var b = ValorantAim.CreateConditions(30, 42);
        int micro = 0, medium = 0, wide = 0;
        for (int i = 0; i < a.Count; i++)
        {
            Check(a[i].Id == b[i].Id && a[i].Yaw == b[i].Yaw && a[i].Pitch == b[i].Pitch, "Target sequence must be reproducible.");
            if (a[i].Category == "Micro") micro++; else if (a[i].Category == "Medium") medium++; else if (a[i].Category == "Wide") wide++;
        }
        Check(micro == 10 && medium == 10 && wide == 10, "Target categories must be balanced.");
        var caps = new[] { 3.6, 12.0, 18.0 };
        foreach (var c in a)
        {
            double cap = c.Category == "Micro" ? caps[0] : c.Category == "Medium" ? caps[1] : caps[2];
            Check(Math.Abs(c.Yaw) <= cap + 1e-9, "Targets must not spawn far out on the horizontal axis.");
        }
        bool wideSmall = false, wideLarge = false;
        double firstWideDistance = -1;
        bool variedWideDistance = false;
        foreach (var c in a)
        {
            if (c.Category != "Wide") continue;
            wideSmall |= c.Radius == 0.6; wideLarge |= c.Radius == 1.5;
            double d = Math.Sqrt(c.Yaw * c.Yaw + c.Pitch * c.Pitch);
            if (firstWideDistance < 0) firstWideDistance = d;
            else variedWideDistance |= Math.Abs(d - firstWideDistance) > 1e-6;
        }
        Check(wideSmall && wideLarge && variedWideDistance, "Wide turns must vary distance and include small targets.");
        foreach (double distance in new[] { 3.0, 25.0 })
        {
            Check(ValorantAim.ShotBias(distance, 0, distance, 0, 1) == 0, "Centered shots have no bias.");
            Check(ValorantAim.ShotBias(distance - 2, 0, distance, 0, 1).Value < -0.99, "Short misses must report undershoot.");
            Check(ValorantAim.ShotBias(distance + 2, 0, distance, 0, 1).Value > 0.99, "Long misses must report overshoot.");
            Check(!ValorantAim.ShotBias(distance, 3, distance, 0, 1).HasValue, "Lateral misses must remain unclassified.");
            Check(ValorantAim.ShotBias(distance + 1, 0, distance, 0, 1) == 0, "Target boundary counts as a hit.");
        }        var edge = ValorantAim.Project(51.5, 0, 1600, 900);
        Check(Math.Abs(edge.X - 1600) < 0.01 && edge.Y == 450, "Projection must use 103-degree horizontal field of view.");
        var center = ValorantAim.ProjectTarget(20, 10, 20, 10, 1600, 900);
        Check(Math.Abs(center.X - 800) < 0.01 && Math.Abs(center.Y - 450) < 0.01, "Aimed target must project to center at simultaneous yaw and pitch.");
        var condition = new ValorantAimCondition { Id = 1, Category = "Micro", Yaw = 7, Pitch = 0, Radius = 1 };
        var trial = new ValorantAimTrial(condition, 1, Tick(100), 1000);
        trial.Observe(new MotionSample { Ticks = Tick(200), X = 100, Relative = true });
        trial.Observe(new MotionSample { Ticks = Tick(250), Buttons = 1, Relative = true });
        Check(trial.Finished && !trial.Metrics.Failed && Math.Abs(trial.Yaw - 7) < 1e-8, "First shot should hit using Valorant yaw scaling.");
        Check(Math.Abs(trial.Metrics.ReactionMs.Value - 100) < 1 && Math.Abs(trial.Metrics.MovementMs - 50) < 1, "Record movement onset and first shot time.");
        trial.Observe(new MotionSample { Ticks = Tick(300), X = 100, Buttons = 1, Relative = true });
        Check(Math.Abs(trial.Yaw - 7) < 1e-8 && !trial.Metrics.Failed, "Later motion and clicks cannot change the first shot.");
        var miss = new ValorantAimTrial(condition, 1, Tick(100), 1000);
        miss.Shoot(Tick(150));
        Check(miss.Metrics.Failed && !miss.Metrics.TimedOut && miss.Metrics.InitialError < 0 && Math.Abs(miss.Metrics.TerminalError - 7) < 1e-6, "A first click away from the target must miss.");
        var timeout = new ValorantAimTrial(condition, 1, Tick(100), 1000);
        timeout.Timeout(Tick(1100));
        Check(timeout.Finished && timeout.Metrics.Failed && timeout.Metrics.TimedOut, "Timeout must be a failed trial.");
    }
}


