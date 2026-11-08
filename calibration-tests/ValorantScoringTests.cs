using System;
using System.Collections.Generic;
using System.Linq;
using grapher.Models.Calibration;

public static class ValorantScoringTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static List<TrialMetrics> Shots()
    {
        var shots = new List<TrialMetrics>();
        foreach (string category in new[] { "Micro", "Medium", "Wide" })
            for (int i = 0; i < 4; i++) shots.Add(new TrialMetrics { Category = category, ConditionId = shots.Count, AcquisitionMs = 700, MovementMs = 500, Radius = 1, InitialError = 0 });
        return shots;
    }
    public static void Run()
    {
        var baseline = Shots(); var candidate = Shots();
        baseline[0].Failed = baseline[1].Failed = true;
        Check(ValorantScoring.Compare(baseline, candidate).Improved, "Two matched first-shot gains should win.");
        candidate[4].Failed = true;
        Check(!ValorantScoring.Compare(baseline, candidate).Improved, "A category regression must block selection.");
        candidate = Shots(); candidate[4].TimedOut = true;
        Check(!ValorantScoring.Compare(baseline, candidate).Improved, "Timeouts must block selection.");
        candidate = Shots(); candidate.Reverse();
        Check(ValorantScoring.Compare(baseline, candidate).Improved, "Shuffled matched conditions should compare correctly.");
        candidate = Shots(); candidate.RemoveAt(0);
        Check(!ValorantScoring.Compare(baseline, candidate).Improved, "Missing trials must block selection.");
        baseline = Shots(); candidate = Shots();
        foreach (var shot in candidate) shot.MovementMs = 450;
        Check(ValorantScoring.Compare(baseline, candidate).Improved, "Consistent faster matched hits should win.");
        candidate = Shots(); foreach (var shot in candidate) shot.AcquisitionMs = 550;
        Check(!ValorantScoring.Compare(baseline, candidate).Improved, "Reaction-only gains must not win.");
        candidate[0].Invalid = true;
        Check(!ValorantScoring.Compare(baseline, candidate).Improved, "Invalid trials must block selection.");
        candidate = Shots(); baseline[0].Failed = true;
        Check(!ValorantScoring.Compare(baseline, candidate).Improved, "A single gain is inconclusive.");
        baseline = Shots(); candidate = Shots();
        baseline[0].Failed = true; candidate[1].Failed = true;
        foreach (var shot in candidate) shot.MovementMs = 450;
        Check(ValorantScoring.Compare(baseline, candidate).Improved, "Speed may win with unchanged category hit counts and discordant targets.");
        candidate[2].Radius = 2;
        Check(!ValorantScoring.Compare(baseline, candidate).Improved, "Mismatched target geometry must reject a comparison.");
        candidate = Shots(); candidate[2].Distance = double.NaN;
        Check(!ValorantScoring.Compare(baseline, candidate).Improved, "Nonfinite geometry must reject a comparison.");
        var screen = new List<TrialMetrics>();
        for (int i = 0; i < 6; i++) screen.Add(new TrialMetrics { ConditionId = i, Category = new[] { "Micro", "Medium", "Wide" }[i % 3], Radius = 1, Distance = 10, MovementMs = 400 });
        double fast = ValorantScoring.RankScreen(screen);
        screen[0].MovementMs = 500;
        Check(ValorantScoring.RankScreen(screen) > fast, "Faster equal-accuracy screen should rank better.");
        screen[0].MovementMs = 1; screen[0].Failed = true;
        Check(ValorantScoring.RankScreen(screen) > fast, "Any miss must outweigh all speed gains.");
        screen[0].TimedOut = true;
        Check(ValorantScoring.RankScreen(screen) > fast, "Timeout must count as a failure.");
        screen[0].Invalid = true;
        Check(double.IsPositiveInfinity(ValorantScoring.RankScreen(screen)), "Invalid screening data must not rank.");
        screen[0].Invalid = false; screen[0].ConditionId = screen[1].ConditionId;
        Check(double.IsPositiveInfinity(ValorantScoring.RankScreen(screen)), "Duplicate screening conditions must not rank.");        string reason;
        string json = DriverConfig.GetDefault().ToJSON();
        var measurements = Shots();
        foreach (var shot in measurements.Where(x => x.Category == "Micro")) shot.InitialError = .5;
        var options = CalibrationOptimizer.CreateValorantCandidates(json, 0, measurements, out reason);
        Check(options.Count > 0 && options[0].Description.Contains("reduced"), "Micro overshoots must prioritize lower sensitivity: " + reason);
        foreach (var shot in measurements.Where(x => x.Category == "Micro")) shot.InitialError = -.5;
        options = CalibrationOptimizer.CreateValorantCandidates(json, 0, measurements, out reason);
        Check(options.Count > 0 && options[0].Description.Contains("increased"), "Micro undershoots must prioritize higher sensitivity.");
        measurements = Shots();
        foreach (var shot in measurements.Where(x => x.Category == "Wide")) { shot.InitialError = -.5; shot.Failed = true; }
        options = CalibrationOptimizer.CreateValorantCandidates(json, 0, measurements, out reason);
        var accel = options.FirstOrDefault(x => x.Description.Contains("acceleration"));
        Check(accel != null, "Repeated wide misses should allow bounded acceleration: " + reason);
        measurements.Add(new TrialMetrics { Category = "Micro", ConditionId = 100, Failed = true, TimedOut = true });
        measurements.Add(new TrialMetrics { Category = "Micro", ConditionId = 101, Failed = true, InitialError = null });
        options = CalibrationOptimizer.CreateValorantCandidates(json, 0, measurements, out reason);
        Check(!options.Any(x => x.Description.Contains("acceleration")), "Poor micro accuracy must prevent acceleration.");
        measurements = Shots();
        options = CalibrationOptimizer.CreateValorantCandidates(accel.SettingsJson, 0, measurements, out reason);
        Check(options.Any(x => x.Description.Contains("Flat")), "Mild acceleration must offer a flat challenger within native bounds.");
        var strong = DriverConfig.Convert(accel.SettingsJson).Item1;
        var profile = strong.profiles[0]; var args = profile.argsX; var cap = args.cap; cap.y = 2; args.cap = cap; args.acceleration *= 12.5; profile.argsX = args;
        strong.SetProfileAt(0, profile);
        options = CalibrationOptimizer.CreateValorantCandidates(strong.ToJSON(), 0, measurements, out reason);
        Check(!options.Any(x => x.Description.Contains("Flat")), "Flat challenger outside native bounds must be rejected.");
    }
}





