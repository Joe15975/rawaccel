using System;
using System.Collections.Generic;
using grapher.Models.Calibration;
using Newtonsoft.Json.Linq;

public static class OptimizationFeedbackTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static List<TrialMetrics> Measurements(double initialError)
    {
        var result = new List<TrialMetrics>();
        for (int i = 0; i < 12; i++) result.Add(new TrialMetrics {
            ConditionId = i / 4 * 8, Radius = 10, Distance = 100, InitialError = initialError,
            TerminalError = 0, MovementMs = 500, InitialMovementMs = 250, CorrectionMs = 250,
            Corrections = 1, AcquisitionMs = 700
        });
        return result;
    }
    public static void Run()
    {
        var config = DriverConfig.GetDefault();
        config.profiles.Add(new Profile { name = "Untouched", outputDPI = 1200 });
        string baseline = config.ToJSON(), reason;
        var over = Measurements(20);
        var down = CalibrationOptimizer.CreateCandidates(baseline, 0, over, out reason);
        Check(down.Count == 2, "Corrected overshoot must offer bounded reductions even with acceleration Off: " + reason);
        foreach (var candidate in down)
        {
            var parsed = DriverConfig.Convert(candidate.SettingsJson).Item1;
            Check(parsed.profiles[0].outputDPI < config.profiles[0].outputDPI && parsed.profiles[0].argsX.mode == AccelMode.noaccel,
                "A return to the circle must not hide overshoot or add positive acceleration.");
            var before = JObject.Parse(baseline); var after = JObject.Parse(candidate.SettingsJson);
            ((JObject)before["profiles"][0]).Remove("Output DPI");
            ((JObject)after["profiles"][0]).Remove("Output DPI");
            Check(JToken.DeepEquals(before, after), "Base reduction must preserve every other setting.");
        }
        var up = CalibrationOptimizer.CreateCandidates(baseline, 0, Measurements(-20), out reason);
        Check(up.Count > 0 && DriverConfig.Convert(up[0].SettingsJson).Item1.profiles[0].outputDPI > config.profiles[0].outputDPI,
            "Corrected initial undershoot must offer an increase.");
        var refinement = CalibrationOptimizer.CreateRefinement(baseline, down[0].SettingsJson, 0, over, over, out reason);
        Check(refinement.Count > 0, "A measured sensitivity reduction must support bounded refinement: " + reason);
        Check(CalibrationOptimizer.DescribeMeasurements(over).Contains("12 past") &&
            CalibrationOptimizer.DescribeMeasurements(Measurements(-20)).Contains("12 short"),
            "Feedback must describe first approach even when the cursor finally ends inside the circle.");
        foreach (var metric in over) metric.InitialError = double.NaN;
        Check(CalibrationOptimizer.CreateCandidates(baseline, 0, over, out reason).Count == 0,
            "Unknown/invalid first errors must not generate personalized changes.");
    }
}
