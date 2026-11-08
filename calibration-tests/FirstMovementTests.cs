using System;
using System.Collections.Generic;
using System.Diagnostics;
using grapher.Models.Calibration;

public static class FirstMovementTests
{
    private static long Tick(int ms) { return (long)(ms * (double)Stopwatch.Frequency / 1000); }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static TrialMetrics Path(bool completed, params double[] coordinates)
    {
        var t = new TargetTrial { StartX = 0, StartY = 0, TargetX = 100, TargetY = 0, Radius = 10, PresentedTicks = Tick(100), Completed = completed };
        for (int i = 0; i < coordinates.Length / 2; i++)
            t.Cursor.Add(new CursorSample { Ticks = Tick(100 + i * 50), X = coordinates[2 * i], Y = coordinates[2 * i + 1] });
        return CalibrationAnalysis.Analyze(t);
    }
    public static void Run()
    {
        var over = Path(true, 0,0, 40,0, 80,0, 120,0, 120,0, 100,0, 100,0, 100,0, 100,0);
        Check(over.InitialError.HasValue && Math.Abs(over.InitialError.Value - 10) < 1e-8 && over.TerminalError == 0 && over.PassedTarget,
            "Overshoot must survive a corrected final dwell.");
        Check(over.FirstMovementEndIndex == 3 && over.InitialEndX == 120 && over.Corrections >= 1 && over.CorrectionMs.HasValue,
            "The first stop and correction must be recorded separately.");
        var reverse = Path(true, 0,0, 40,0, 80,0, 120,0, 112,0, 100,0, 100,0, 100,0, 100,0);
        Check(reverse.InitialError == 10 && reverse.FirstMovementEndIndex == 3 && reverse.Corrections >= 1,
            "A significant reversal should identify the first endpoint without a pause.");
        var under = Path(true, 0,0, 40,0, 80,0, 80,0, 100,0, 100,0, 100,0, 100,0);
        Check(under.InitialError == -10 && under.Corrections == 1 && under.CorrectionMs.HasValue,
            "A forward correction after an undershoot must count.");
        var hit = Path(true, 0,0, 0,0, 3,0, 3,0, 40,0, 80,0, 100,0, 100,0, 100,0, 100,0);
        Check(hit.InitialError == 0 && hit.InitialEndX == 100 && hit.Corrections == 0 && hit.CorrectionMs == 0 && !hit.PassedTarget,
            "An onset hesitation must not split a clean approach.");
        var inProgress = Path(false, 0,0, 40,0, 80,0, 80,0, 90,0);
        Check(inProgress.InitialError == -10 && inProgress.Corrections == 1 && inProgress.CorrectionMs == 100,
            "An ongoing correction should show elapsed time since its first endpoint.");
        var jitter = Path(true, 0,0, 40,0, 80,0, 110,0, 109,0, 110,0, 110,0, 110,0, 110,0);
        Check(jitter.InitialError == 0 && jitter.Corrections == 0, "Small jitter must not count as a correction.");
        var lateral = Path(false, 0,0, 40,5, 80,10, 120,20, 120,20, 120,20);
        Check(!lateral.InitialError.HasValue && !lateral.CorrectionMs.HasValue && lateral.Failed,
            "A lateral miss must not be labeled overshoot or a completed correction.");
        var unfinished = Path(false, 0,0, 40,0, 60,0);
        Check(!unfinished.InitialError.HasValue && unfinished.FirstMovementEndIndex == -1 && unfinished.Failed,
            "A moving unfinished path has no defensible first endpoint.");
        var baseline = new List<TrialMetrics>(); var candidate = new List<TrialMetrics>();
        for (int i = 0; i < 12; i++)
        {
            int id = (i / 4) * 8;
            baseline.Add(new TrialMetrics { ConditionId = id, Distance = 100, Radius = 10, AcquisitionMs = 1000, Jitter = 1, InitialError = 10, Corrections = 1, CorrectionMs = 200 });
            candidate.Add(new TrialMetrics { ConditionId = id, Distance = 100, Radius = 10, AcquisitionMs = 1000, Jitter = 1, InitialError = 0, Corrections = 0, CorrectionMs = 100 });
        }
        var comparison = CalibrationAnalysis.Compare(baseline, candidate);
        Check(comparison.Improved && comparison.Reason.Contains("first-approach"), "First-approach improvement should not require faster completion.");
        Check(comparison.ImprovementFraction == 0 && comparison.SelectionScore > 0 &&
              comparison.FirstMovementImprovementFraction == 1 && comparison.CorrectionImprovementFraction == 0.5,
              "Selection must use measured first-approach/correction improvements with no time improvement.");
        candidate[0].Failed = true;
        Check(!CalibrationAnalysis.Compare(baseline, candidate).Improved, "First-approach gains must not conceal failure regression.");
        candidate[0].Failed = false;
        foreach (var m in candidate) { m.AcquisitionMs = 900; m.InitialError = 25; }
        Check(!CalibrationAnalysis.Compare(baseline, candidate).Improved, "Faster completion must not hide worse first-approach error.");
        foreach (var m in candidate) m.InitialError = 0;
        foreach (var m in candidate) m.AcquisitionMs = 1200;
        Check(!CalibrationAnalysis.Compare(baseline, candidate).Improved, "First-approach gains must not conceal large time regression.");
        foreach (var m in candidate) { m.AcquisitionMs = 1000; m.CorrectionMs = null; }
        Check(!CalibrationAnalysis.Compare(baseline, candidate).CorrectionImprovementFraction.HasValue,
            "Missing correction durations must not be reported as zero improvement.");
        foreach (var m in candidate) { m.InitialError = 10; m.Corrections = 1; m.CorrectionMs = 100; }
        var correctionOnly = CalibrationAnalysis.Compare(baseline, candidate);
        Check(correctionOnly.Improved && correctionOnly.SelectionScore == 0.5 && correctionOnly.Reason.Contains("correction time"),
            "Consistently faster corrections must qualify even when counts, first error and acquisition time do not change.");
        foreach (var m in candidate) m.CorrectionMs = 300;
        Check(!CalibrationAnalysis.Compare(baseline, candidate).Improved,
            "Slower corrections must not qualify as an improvement.");
        foreach (var m in candidate) m.AcquisitionMs = 900;
        Check(!CalibrationAnalysis.Compare(baseline, candidate).Improved,
            "Faster acquisition must not hide a large correction-time regression.");
        for (int i = 0; i < candidate.Count; i++)
        {
            candidate[i].CorrectionMs = baseline[i].CorrectionMs;
            candidate[i].InitialError = i < 7 ? 0 : baseline[i].InitialError;
        }
        var speedOnly = CalibrationAnalysis.Compare(baseline, candidate);
        Check(speedOnly.Improved && Math.Abs(speedOnly.SelectionScore - .1) < .0001 &&
            speedOnly.Benefit.Contains("faster target acquisition") && speedOnly.FirstMovementImprovementFraction == 1,
            "A large median accuracy gain on only seven pairs must not outrank the qualifying speed benefit.");
    }
}
