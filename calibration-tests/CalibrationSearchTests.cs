using System;
using System.Linq;
using System.Collections.Generic;
using grapher.Models.Calibration;
using Newtonsoft.Json.Linq;

public static class CalibrationSearchTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var config = DriverConfig.GetDefault();
        config.profiles.Add(new Profile { name = "Untouched", outputDPI = 1200 });
        string json = config.ToJSON(), reason;
        var choices = CalibrationSearch.CreateCandidates(json, 0, null, out reason);
        Check(choices.Count <= 12 && choices.Count > 0, "Search must be finite and produce candidates: " + reason);
        Check(choices.Any(x => x.Description == "Natural"), "Bounded Natural candidate should be available for a default profile.");
        Check(choices.Any(x => x.Description == "Sensitivity down") && choices.Any(x => x.Description == "Sensitivity up"),
            "Search should include base sensitivity changes.");
        foreach (var choice in choices)
        {
            var parsed = DriverConfig.Convert(choice.SettingsJson);
            Check(parsed.Item1 != null && parsed.Item2 == null, "Candidate must parse.");
            using (var original = new ManagedAccel(config.profiles[0]))
            using (var proposed = new ManagedAccel(parsed.Item1.profiles[0]))
            {
                foreach (int speed in new[] { 1, 10, 30, 100 })
                {
                    var oldOutput = original.Accelerate(speed, 0, 1, 1);
                    var newOutput = proposed.Accelerate(speed, 0, 1, 1);
                    double ratio = Math.Abs(newOutput.Item1 / oldOutput.Item1);
                    Check(!double.IsNaN(ratio) && !double.IsInfinity(ratio) && ratio >= .8 - 1e-9 && ratio <= 1.2 + 1e-9,
                        "Native sample outside search safety envelope: " + choice.Description);
                }
            }
            var before = JObject.Parse(json); var after = JObject.Parse(choice.SettingsJson);
            var a = (JObject)before["profiles"][0]; var b = (JObject)after["profiles"][0];
            foreach (var key in new[] { "Whole or horizontal accel parameters", "Vertical accel parameters", "Output DPI" })
            { a.Remove(key); b.Remove(key); }
            Check(JToken.DeepEquals(before, after), "Candidate changed unrelated profile or device settings: " + choice.Description);
        }
        var evidence = Enumerable.Range(0, 4).Select(i => new TrialMetrics { Category = "Micro", Radius = 1, InitialError = .5 }).ToList();
        var lower = CalibrationSearch.CreateCandidates(json, 0, evidence, out reason);
        Check(lower.Where(x => x.Description != "Sensitivity down" && x.Description != "Sensitivity up").All(x =>
            Math.Abs(DriverConfig.Convert(x.SettingsJson).Item1.profiles[0].outputDPI / config.profiles[0].outputDPI - .9) < .0001),
            "Repeated near-target overshoots should lower admitted family base sensitivity.");
        foreach (var item in evidence) item.InitialError = -.5;
        var higher = CalibrationSearch.CreateCandidates(json, 0, evidence, out reason);
        Check(higher.Where(x => x.Description != "Sensitivity down" && x.Description != "Sensitivity up").All(x =>
            Math.Abs(DriverConfig.Convert(x.SettingsJson).Item1.profiles[0].outputDPI / config.profiles[0].outputDPI - 1.1) < .0001),
            "Repeated near-target undershoots should increase admitted family base sensitivity.");
        var invalid = CalibrationSearch.CreateCandidates(json, -1, null, out reason);
        Check(invalid.Count == 0 && reason != null, "Invalid profile must fail closed.");
        var linearConfig = DriverConfig.GetDefault();
        var linearProfile = linearConfig.profiles[0];
        var linearArgs = linearProfile.argsX;
        linearArgs.mode = AccelMode.classic; linearArgs.gain = false; linearArgs.exponentClassic = 2;
        linearArgs.acceleration = .05; linearArgs.inputOffset = 5; linearArgs.capMode = CapMode.output;
        var cap = linearArgs.cap; cap.y = 2; linearArgs.cap = cap; linearProfile.argsX = linearArgs;
        linearConfig.SetProfileAt(0, linearProfile);
        linearConfig.profiles.Add(new Profile { name = "Preserved", outputDPI = 1350 });
        string linearJson = linearConfig.ToJSON();
        var fitted = CalibrationSearch.CreateCandidates(linearJson, 0, null, out reason);
        var natural = fitted.FirstOrDefault(p => p.Description == "Natural");
        Check(natural != null, "A non-default Linear baseline needs a bounded Natural alternative: " + reason);
        var converted = DriverConfig.Convert(natural.SettingsJson).Item1;
        Check(converted.profiles[0].argsX.mode == AccelMode.natural, "The fitted candidate must use Natural acceleration.");
        Check(JToken.DeepEquals(JObject.Parse(linearJson)["profiles"][1], JObject.Parse(natural.SettingsJson)["profiles"][1]),
            "Fitting a curve must preserve other profiles.");
        foreach (int speed in new[] { 1, 5, 10, 20, 40, 80, 120, 240 })
        using (var oldModel = new ManagedAccel(linearProfile))
        using (var newModel = new ManagedAccel(converted.profiles[0]))
        {
            var oldOutput = oldModel.Accelerate(speed, 0, 1, 1);
            var newOutput = newModel.Accelerate(speed, 0, 1, 1);
            double ratio = Math.Abs(newOutput.Item1 / oldOutput.Item1);
            Check(ratio >= .8 - 1e-9 && ratio <= 1.2 + 1e-9, "Fitted Natural exceeded the response envelope.");
        }
    }
}

