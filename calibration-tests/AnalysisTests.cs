using System;
using System.Collections.Generic;
using System.Diagnostics;
using grapher.Models.Calibration;
using Newtonsoft.Json.Linq;

public static class AnalysisTests
{
    private static long Tick(double ms) { return (long)(ms * Stopwatch.Frequency / 1000); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static TargetTrial Trial(bool completed = true)
    {
        var t = new TargetTrial { ConditionId = 1, StartX = 0, StartY = 0, TargetX = 100, TargetY = 0, Radius = 10, PresentedTicks = Tick(100), Completed = completed };
        t.Cursor.Add(new CursorSample { Ticks = Tick(100), X = 0, Y = 0 });
        t.Cursor.Add(new CursorSample { Ticks = Tick(150), X = 50, Y = 0 });
        t.Cursor.Add(new CursorSample { Ticks = Tick(200), X = 100, Y = 0 });
        t.Cursor.Add(new CursorSample { Ticks = Tick(250), X = 101, Y = 0 });
        t.Cursor.Add(new CursorSample { Ticks = Tick(300), X = 100, Y = 0 });
        t.Cursor.Add(new CursorSample { Ticks = Tick(350), X = 100, Y = 0 });
        return t;
    }
    public static void Run()
    {
        TestTiming();
        var good = CalibrationAnalysis.Analyze(Trial());
        Check(!good.Invalid && !good.Failed && good.Jitter.HasValue && good.SettlingMs.HasValue, "Successful dwell was not recognized.");
        Check(good.Overshoot >= 1 && good.TerminalError == 0, "Cursor geometry incorrect.");
        var failed = CalibrationAnalysis.Analyze(Trial(false));
        Check(failed.Failed && failed.Jitter.HasValue && failed.AcquisitionMs == 5000, "Failure should be censored and penalized.");
        var shortWindow = Trial(false); shortWindow.Cursor.RemoveRange(3, 3);
        Check(!CalibrationAnalysis.Analyze(shortWindow).Jitter.HasValue, "Incomplete stability window must be censored.");
        var undershoot = Trial();
        undershoot.Cursor[1] = new CursorSample { Ticks = Tick(150), X = 50 };
        undershoot.Cursor[2] = new CursorSample { Ticks = Tick(200), X = 50 };
        undershoot.Cursor[3] = new CursorSample { Ticks = Tick(250), X = 100 };
        undershoot.Cursor[4] = new CursorSample { Ticks = Tick(300), X = 100 };
        undershoot.Cursor[5] = new CursorSample { Ticks = Tick(350), X = 100 };
        Check(CalibrationAnalysis.Analyze(undershoot).Undershoot == 50, "Sustained stop must measure undershoot.");
        var broken = Trial(); broken.Cursor[2] = new CursorSample { Ticks = Tick(400), X = 100 };
        Check(CalibrationAnalysis.Analyze(broken).Invalid, "Capture gap must invalidate trial.");
        var capture = new CalibrationCapture(new IntPtr(1));
        capture.Begin(new TargetTrial());
        capture.Observe(new MotionSample { Device = new IntPtr(1), Relative = true, Ticks = Tick(1) });
        capture.Observe(new MotionSample { Device = new IntPtr(2), Relative = true, X = 1, Ticks = Tick(2) });
        Check(capture.Finish().InvalidReason != null, "Other mouse motion must invalidate trial.");
        var b = new List<TrialMetrics>(); var c = new List<TrialMetrics>();
        for (int i = 0; i < 12; i++)
        {
            int condition = (i / 4) * 8;
            b.Add(new TrialMetrics { ConditionId = condition, Radius = 10, Distance = 100, AcquisitionMs = 1000, Jitter = 1 });
            c.Add(new TrialMetrics { ConditionId = condition, Radius = 10, Distance = 100, AcquisitionMs = 900, Jitter = 1 });
        }
        Check(CalibrationAnalysis.Compare(b, c).Improved, "Consistent improvement should pass.");
        // Overall mean jitter falls, but the long-distance median crosses its guardrail.
        for (int i = 0; i < 12; i++) c[i].Jitter = i < 8 ? 0 : 2;
        var unstable = CalibrationAnalysis.Compare(b, c);
        Check(!unstable.Improved && unstable.Reason.Contains("Long targets") && unstable.Reason.Contains("4 pairs"),
            "A per-distance stability rejection must identify the group and its matched data.");
        for (int i = 8; i < 12; i++) c[i].Jitter = null;
        Check(CalibrationAnalysis.Compare(b, c).Reason.Contains("no matched jitter readings"),
            "Missing jitter evidence must be distinguished from measured worsening.");
        foreach (var m in c) m.Jitter = 1;
        c[0].Distance = 150;
        Check(!CalibrationAnalysis.Compare(b, c).Improved, "Unmatched geometry must not create a valid pair.");
        c[0].Distance = 100;
        var singleGroupBaseline = new List<TrialMetrics>(); var singleGroupCandidate = new List<TrialMetrics>();
        for (int i = 0; i < 12; i++)
        {
            singleGroupBaseline.Add(new TrialMetrics { ConditionId = 0, Radius = 10, Distance = 100, AcquisitionMs = 1000, Jitter = 1 });
            singleGroupCandidate.Add(new TrialMetrics { ConditionId = 0, Radius = 10, Distance = 100, AcquisitionMs = 900, Jitter = 1 });
        }
        Check(!CalibrationAnalysis.Compare(singleGroupBaseline, singleGroupCandidate).Improved, "Missing distance groups cannot support improvement.");
        c[0].Failed = true;
        Check(!CalibrationAnalysis.Compare(b, c).Improved, "Failure regression should reject candidate.");
        string reason;
        var proposals = CalibrationOptimizer.CreateCandidates(DriverConfig.GetDefault().ToJSON(), 0, out reason);
        Check(proposals.Count == 2, "Expected conservative default candidates: " + reason);
        foreach (var proposal in proposals)
        {
            var parsed = DriverConfig.Convert(proposal.SettingsJson).Item1;
            Check(parsed != null, "Candidate must round trip.");
            Check(parsed.profiles[0].outputDPI == DriverConfig.GetDefault().profiles[0].outputDPI &&
                  parsed.profiles[0].argsX.mode == AccelMode.classic && !parsed.profiles[0].argsX.gain &&
                  parsed.profiles[0].argsX.capMode == CapMode.output, "Candidate must retain sensitivity and use the bounded legacy curve.");
        }
        Check(CalibrationOptimizer.CreateCandidates("{}", 0, out reason).Count == 0, "Invalid configuration must be rejected.");
        var over = new List<TrialMetrics>(); var under = new List<TrialMetrics>();
        for (int i = 0; i < 8; i++)
        {
            over.Add(new TrialMetrics { Radius = 10, Distance = 100, Overshoot = 20, Undershoot = 0 });
            under.Add(new TrialMetrics { Radius = 10, Distance = 100, Overshoot = 0, Undershoot = 20 });
        }
        string defaultJson = DriverConfig.GetDefault().ToJSON();
        var reduced = CalibrationOptimizer.CreateCandidates(defaultJson, 0, over, out reason);
        Check(reduced.Count > 0 && DriverConfig.Convert(reduced[0].SettingsJson).Item1.profiles[0].outputDPI <
            DriverConfig.GetDefault().profiles[0].outputDPI, "Overshooting with acceleration Off should try lower base sensitivity.");
        Check(CalibrationOptimizer.CreateCandidates(defaultJson, 0, new List<TrialMetrics>(), out reason).Count == 0, "Sparse measurements must not suggest a curve.");
        var invalidMetrics = new List<TrialMetrics>();
        for (int i = 0; i < 4; i++) invalidMetrics.Add(new TrialMetrics { Radius = 10, Overshoot = double.NaN });
        Check(CalibrationOptimizer.CreateCandidates(defaultJson, 0, invalidMetrics, out reason).Count == 0, "Non-finite overshoot cannot supply evidence.");
        foreach (var metric in invalidMetrics) { metric.Overshoot = 0; metric.Undershoot = double.PositiveInfinity; }
        Check(CalibrationOptimizer.CreateCandidates(defaultJson, 0, invalidMetrics, out reason).Count == 0, "Non-finite undershoot cannot supply evidence.");
        var added = CalibrationOptimizer.CreateCandidates(defaultJson, 0, under, out reason);
        Check(added.Count > 0, "Undershoot should suggest acceleration: " + reason);
        var refine = CalibrationOptimizer.CreateRefinement(defaultJson, added[0].SettingsJson, 0, under, under, out reason);
        Check(refine.Count > 0, "Measured refinement should offer a bounded local change: " + reason);
        var altered = DriverConfig.Convert(added[0].SettingsJson).Item1;
        altered.profiles[0].rotation = 22; altered.SetProfileAt(0, altered.profiles[0]);
        Check(CalibrationOptimizer.CreateRefinement(defaultJson, altered.ToJSON(), 0, under, under, out reason).Count == 0,
            "Refinement must reject changes to unrelated settings.");
        altered = DriverConfig.Convert(added[0].SettingsJson).Item1;
        var hiddenArgs = altered.profiles[0].argsY; hiddenArgs.decayRate += 0.1;
        altered.profiles[0].argsY = hiddenArgs; altered.SetProfileAt(0, altered.profiles[0]);
        Check(CalibrationOptimizer.CreateRefinement(defaultJson, altered.ToJSON(), 0, under, under, out reason).Count == 0,
            "Refinement must reject unrelated inactive-axis edits.");
        altered = DriverConfig.Convert(added[0].SettingsJson).Item1;
        var changedArgs = altered.profiles[0].argsX; changedArgs.inputOffset += 1;
        altered.profiles[0].argsX = changedArgs; altered.SetProfileAt(0, altered.profiles[0]);
        Check(CalibrationOptimizer.CreateRefinement(defaultJson, altered.ToJSON(), 0, under, under, out reason).Count == 0,
            "Refinement must reject unissued classic parameters.");
        var separate = DriverConfig.GetDefault();
        var separateProfile = separate.profiles[0]; var separateSpeed = separateProfile.inputSpeedArgs;
        separateSpeed.combineMagnitudes = false; separateProfile.inputSpeedArgs = separateSpeed;
        separateProfile.maximumSpeed = 100; separate.SetProfileAt(0, separateProfile);
        var separateCandidates = CalibrationOptimizer.CreateCandidates(separate.ToJSON(), 0, under, out reason);
        Check(separateCandidates.Count > 0, "Separate-axis no-acceleration profile should generate candidates: " + reason);
        var separateResult = DriverConfig.Convert(separateCandidates[0].SettingsJson).Item1.profiles[0];
        Check(separateResult.argsX.mode == AccelMode.classic && separateResult.argsY.mode == AccelMode.classic &&
              separateResult.maximumSpeed == 100, "Separate-axis candidate did not preserve clamp or modify both axes.");
        TestExisting(AccelMode.classic, false, true, under, over);
        TestExisting(AccelMode.natural, true, true, under, over);
        TestExisting(AccelMode.jump, true, false, under, over);
        TestExisting(AccelMode.synchronous, true, false, under, over);
        TestExisting(AccelMode.power, true, false, under, over);
        TestExisting(AccelMode.lut, true, false, under, over);
        var notched = DriverConfig.GetDefault();
        var notchProfile = notched.profiles[0]; var notch = notchProfile.argsX;
        notch.mode = AccelMode.lut; notch.gain = false;
        float[] points = { 0, 1, 6.9f, 1, 7, 0, 7.1f, 1, 15, 1.3f };
        Array.Copy(points, notch.data, points.Length); notch.length = points.Length;
        notchProfile.argsX = notch; notched.SetProfileAt(0, notchProfile);
        using (var model = new ManagedAccel(notchProfile))
        {
            var zero = model.Accelerate(1, 0, 1, 1.0 / 7);
            Check(Math.Abs(zero.Item1) < 1e-8, "LUT notch fixture must produce zero native output.");
        }
        var notchCandidates = CalibrationOptimizer.CreateCandidates(notched.ToJSON(), 0, over, out reason);
        foreach (var notchCandidate in notchCandidates)
        using (var model = new ManagedAccel(DriverConfig.Convert(notchCandidate.SettingsJson).Item1.profiles[0]))
            Check(Math.Abs(model.Accelerate(1, 0, 1, 1.0 / 7).Item1) < 1e-8,
                "Candidate must preserve a narrow zero-output LUT notch.");
    }
    private static void TestTiming()
    {
        var trial = new TargetTrial { StartX = 0, StartY = 0, TargetX = 100, TargetY = 0, Radius = 10, PresentedTicks = Tick(100), Completed = true };
        double[] positions = { 0, 0, 0, 20, 60, 100, 100, 100, 100 };
        for (int i = 0; i < positions.Length; i++)
            trial.Cursor.Add(new CursorSample { Ticks = Tick(100 + i * 50), X = positions[i] });
        var metrics = CalibrationAnalysis.Analyze(trial);
        Check(metrics.ReactionMs == 150 && metrics.TimeToHitMs == 100,
            "Timing must separate appearance-to-movement from movement-to-first-hit and exclude dwell.");
        trial.Cursor[6] = new CursorSample { Ticks = Tick(400), X = 120 };
        trial.Completed = false;
        metrics = CalibrationAnalysis.Analyze(trial);
        Check(metrics.ReactionMs == 150 && metrics.TimeToHitMs == 100 && metrics.Failed,
            "Later overshoot/correction or an incomplete hold must not change the recorded first-hit time.");
        for (int i = 5; i < positions.Length; i++)
            trial.Cursor[i] = new CursorSample { Ticks = Tick(100 + i * 50), X = 60 };
        metrics = CalibrationAnalysis.Analyze(trial);
        Check(metrics.ReactionMs == 150 && !metrics.TimeToHitMs.HasValue,
            "Movement without a hit must not invent a time to hit.");
        for (int i = 0; i < positions.Length; i++)
            trial.Cursor[i] = new CursorSample { Ticks = Tick(100 + i * 50), X = i % 2 };
        metrics = CalibrationAnalysis.Analyze(trial);
        Check(!metrics.ReactionMs.HasValue && !metrics.TimeToHitMs.HasValue,
            "A stationary cursor or sub-threshold jitter must show unavailable timing, not zero or a timeout reaction.");
    }
    private static void TestExisting(AccelMode mode, bool combined, bool smoothing, IList<TrialMetrics> under, IList<TrialMetrics> over)
    {
        var cfg = DriverConfig.GetDefault();
        var profile = cfg.profiles[0];
        var args = profile.argsX;
        args.mode = mode;
        if (mode == AccelMode.lut)
        {
            args.gain = false;
            args.data[0] = 0; args.data[1] = 1; args.data[2] = 15; args.data[3] = 1.3f;
            args.length = 4;
        }
        profile.argsX = args;
        profile.argsY = args;
        var speed = profile.inputSpeedArgs; speed.combineMagnitudes = combined;
        if (smoothing) { speed.inputSmoothHalflife = 5; speed.scaleSmoothHalflife = 5; }
        profile.inputSpeedArgs = speed;
        var range = profile.rangeXY; range.x = 1; range.y = 0.8; profile.rangeXY = range;
        profile.rotation = 10; profile.yxOutputDPIRatio = 1.2; profile.lrOutputDPIRatio = 1.1;
        cfg.SetProfileAt(0, profile);
        var untouched = new Profile(); untouched.name = "other";
        cfg.profiles.Add(untouched); cfg.accels.Add(new ManagedAccel(untouched));
        var device = new DeviceSettings(); device.id = "device"; device.profile = "other"; cfg.devices.Add(device);
        string baseline = cfg.ToJSON(), reason;
        var up = CalibrationOptimizer.CreateCandidates(baseline, 0, under, out reason);
        Check(up.Count > 0, mode + " increased proposal rejected: " + reason);
        var down = CalibrationOptimizer.CreateCandidates(baseline, 0, over, out reason);
        Check(down.Count > 0, mode + " decreased proposal rejected: " + reason);
        var upProfile = DriverConfig.Convert(up[0].SettingsJson).Item1.profiles[0];
        var downProfile = DriverConfig.Convert(down[0].SettingsJson).Item1.profiles[0];
        Check(upProfile.rangeXY.x > profile.rangeXY.x && downProfile.rangeXY.x < profile.rangeXY.x, mode + " ignored outcome direction.");
        var before = JObject.Parse(baseline); var after = JObject.Parse(up[0].SettingsJson);
        var beforeProfiles = (JArray)before["profiles"]; var afterProfiles = (JArray)after["profiles"];
        Check(JToken.DeepEquals(beforeProfiles[1], afterProfiles[1]) && JToken.DeepEquals(before["devices"], after["devices"]) &&
              JToken.DeepEquals(before["defaultDeviceConfig"], after["defaultDeviceConfig"]), mode + " changed unrelated config.");
        Check(upProfile.rotation == profile.rotation && upProfile.yxOutputDPIRatio == profile.yxOutputDPIRatio &&
              upProfile.inputSpeedArgs.inputSmoothHalflife == profile.inputSpeedArgs.inputSmoothHalflife && upProfile.argsX.mode == mode,
              mode + " changed profile settings outside acceleration range.");
    }

}
