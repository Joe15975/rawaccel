using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace grapher.Models.Calibration
{
    public static class CalibrationOptimizer
    {
        private const double MaximumRelativeChange = 0.20;
        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }

        // Retained for existing callers; personalized sessions should supply measured trials.
        public static IList<CalibrationProposal> CreateCandidates(string baselineJson, int profileIndex, out string reason)
        { return CreateCandidates(baselineJson, profileIndex, null, out reason); }

        public static IList<CalibrationProposal> CreateCandidates(string baselineJson, int profileIndex, IList<TrialMetrics> measurements, out string reason)
        {
            return Create(baselineJson, baselineJson, profileIndex, measurements, false, out reason);
        }

        public static IList<CalibrationProposal> CreateRefinement(string baselineJson, string bestCandidateJson, int profileIndex,
            IList<TrialMetrics> baselineMetrics, IList<TrialMetrics> bestMetrics, out string reason)
        {
            if (!HasEvidence(baselineMetrics) || !HasEvidence(bestMetrics))
            { reason = "More valid trials are needed before refinement."; return new List<CalibrationProposal>(); }
            return Create(baselineJson, bestCandidateJson, profileIndex, bestMetrics, true, out reason);
        }

        // Valorant candidates are bounded against the original native model, including a flat challenger.
        public static IList<CalibrationProposal> CreateValorantCandidates(string baselineJson, int profileIndex,
            IList<TrialMetrics> measurements, out string reason)
        {
            var result = new List<CalibrationProposal>();
            reason = "At least four valid shots in each target category are needed.";
            string[] categories = { "Micro", "Medium", "Wide" };
            if (measurements == null || categories.Any(c => measurements.Count(m => m != null && !m.Invalid && m.Category == c) < 4)) return result;
            try
            {
                var original = DriverConfig.Convert(baselineJson);
                if (original.Item1 == null || original.Item2 != null || profileIndex < 0 || profileIndex >= original.Item1.profiles.Count)
                { reason = "Invalid baseline profile."; return result; }
                var before = original.Item1.profiles[profileIndex];
                bool flat = before.argsX.mode == AccelMode.noaccel &&
                    (before.inputSpeedArgs.combineMagnitudes || before.argsY.mode == AccelMode.noaccel);
                // Prioritize micro-shot bias for base sensitivity. Wide undershoots can justify
                // adding acceleration only when the micro shots do not call for a base change.
                var micro = measurements.Where(m => m != null && !m.Invalid && m.Category == "Micro").ToList();
                var directedMicro = micro.Where(m => m.InitialError.HasValue && Finite(m.InitialError.Value) &&
                    Finite(m.Radius) && m.Radius > 0).ToList();
                var wide = measurements.Where(m => m != null && !m.Invalid && m.Category == "Wide" &&
                    m.InitialError.HasValue && Finite(m.InitialError.Value) && Finite(m.Radius) && m.Radius > 0).ToList();
                int microOver = directedMicro.Count(m => m.InitialError.Value > .15 * m.Radius);
                int microUnder = directedMicro.Count(m => m.InitialError.Value < -.15 * m.Radius);
                int wideUnder = wide.Count(m => m.InitialError.Value < -.15 * m.Radius);
                bool reduce = microOver >= 2 && microOver > microUnder;
                bool increase = microUnder >= 2 && microUnder > microOver;
                var changes = new List<string>();
                if (reduce || increase) changes.Add(reduce ? "down" : "up");
                if (!flat) changes.Add("flat");
                else if (!reduce && !increase && micro.Count >= 4 && micro.Count(m => !m.Failed && !m.TimedOut) * 4 >= micro.Count * 3 && wideUnder >= 2) changes.Add("accel");
                if (changes.Count < 2) changes.Add(reduce ? "up" : "down");
                if (changes.Count < 2) changes.Add("up");
                foreach (string change in changes)
                {
                    var clone = DriverConfig.Convert(baselineJson);
                    var profile = clone.Item1.profiles[profileIndex];
                    if (change == "down") profile.outputDPI *= .96;
                    else if (change == "up") profile.outputDPI *= 1.04;
                    else if (change == "accel")
                    {
                        profile.argsX = Classic(profile.argsX, 1.08);
                        if (!profile.inputSpeedArgs.combineMagnitudes) profile.argsY = Classic(profile.argsY, 1.08);
                    }
                    else
                    {
                        var x = profile.argsX; x.mode = AccelMode.noaccel; profile.argsX = x;
                        if (!profile.inputSpeedArgs.combineMagnitudes) { var y = profile.argsY; y.mode = AccelMode.noaccel; profile.argsY = y; }
                    }
                    clone.Item1.SetProfileAt(profileIndex, profile);
                    string json = clone.Item1.ToJSON();
                    var validated = DriverConfig.Convert(json);
                    if (validated.Item1 == null || validated.Item2 != null || !ValidateModel(before, validated.Item1.profiles[profileIndex])) continue;
                    result.Add(new CalibrationProposal { SettingsJson = json,
                        Description = change == "down" ? "Base sensitivity reduced 4%" : change == "up" ? "Base sensitivity increased 4%" :
                            change == "flat" ? "Flat sensitivity challenger" : "Wide-target acceleration challenger",
                        Cap = change == "accel" ? 1.08 : profile.argsX.cap.y, Offset = profile.argsX.inputOffset });
                }
                reason = result.Count == 0 ? "No candidate passed native bounds for this profile." : null;
                return result;
            }
            catch (Exception e) { reason = "Could not validate candidates: " + e.Message; return new List<CalibrationProposal>(); }
        }
        private static bool HasEvidence(IList<TrialMetrics> metrics)
        {
            return metrics != null && metrics.Count(UsableMetric) >= 4;
        }

        private static bool UsableMetric(TrialMetrics m)
        {
            return m != null && !m.Invalid && Finite(m.Radius) && m.Radius > 0 && Finite(m.Overshoot) &&
                (!m.Undershoot.HasValue || Finite(m.Undershoot.Value)) && Finite(m.MovementMs) &&
                Finite(m.TerminalError) && Finite(m.Corrections) &&
                (!m.InitialError.HasValue || Finite(m.InitialError.Value));
        }

        // Direction is based on observed errors, never inferred from post-driver speed.
        private static int Direction(IList<TrialMetrics> metrics)
        {
            if (metrics == null) return 0;
            var valid = metrics.Where(UsableMetric).ToList();
            var initial = valid.Where(m => m.InitialError.HasValue).ToList();
            if (initial.Count >= 4)
            {
                double error = initial.Average(m => m.InitialError.Value / m.Radius);
                if (error > .15) return -1;
                if (error < -.15) return 1;
                return 0; // Corrections after the first approach must not override its measured direction.
            }
            double over = valid.Average(m => Math.Max(0, m.Overshoot) / m.Radius);
            var stops = valid.Where(m => m.Undershoot.HasValue && Finite(m.Undershoot.Value)).ToList();
            double under = stops.Count == 0 ? 0 : stops.Average(m => Math.Max(0, m.Undershoot.Value) / m.Radius);
            if (over > under + 0.15) return -1;
            if (under > over + 0.15) return 1;
            // A slow, accurate movement with little correction can justify trying more acceleration.
            int slow = valid.Count(m => !m.Failed && Finite(m.MovementMs) && m.MovementMs > 1500 &&
                m.TerminalError <= m.Radius && m.Corrections <= 1);
            if (over < 0.15 && slow * 2 > valid.Count) return 1;
            return 0;
        }

        private static IList<CalibrationProposal> Create(string baselineJson, string centerJson, int profileIndex,
            IList<TrialMetrics> metrics, bool refinement, out string reason)
        {
            var proposals = new List<CalibrationProposal>();
            reason = "No supported bounded candidate was found.";
            if (metrics != null && !HasEvidence(metrics)) { reason = "More valid trials are needed to suggest a change."; return proposals; }
            try
            {
                var original = DriverConfig.Convert(baselineJson);
                var center = DriverConfig.Convert(centerJson);
                if (original.Item1 == null || center.Item1 == null || original.Item2 != null || center.Item2 != null ||
                    profileIndex < 0 || profileIndex >= original.Item1.profiles.Count || profileIndex >= center.Item1.profiles.Count)
                { reason = "Invalid baseline or candidate profile."; return proposals; }
                if (!SameUnrelatedSettings(original.Item1.ToJSON(), center.Item1.ToJSON(), profileIndex))
                { reason = "The refinement changed settings outside the selected acceleration curve."; return proposals; }
                var originalProfile = original.Item1.profiles[profileIndex];
                var centerProfile = center.Item1.profiles[profileIndex];
                int direction = Direction(metrics);
                bool noAccel = centerProfile.argsX.mode == AccelMode.noaccel &&
                    (centerProfile.inputSpeedArgs.combineMagnitudes || centerProfile.argsY.mode == AccelMode.noaccel);
                bool firstMovementEvidence = metrics != null && metrics.Count(m => UsableMetric(m) && m.InitialError.HasValue) >= 4;
                // A small sensitivity change can correct an error even when acceleration is Off.
                var changes = new List<Tuple<double, bool>>(); // factor, change base sensitivity
                if (noAccel && direction < 0)
                {
                    changes.Add(Tuple.Create(refinement ? .98 : .96, true));
                    changes.Add(Tuple.Create(refinement ? .96 : .92, true));
                }
                else if (noAccel)
                {
                    if (direction > 0 && firstMovementEvidence) changes.Add(Tuple.Create(1.04, true));
                    changes.Add(Tuple.Create(1.08, false));
                    if (changes.Count < 2) changes.Add(Tuple.Create(1.12, false));
                }
                else
                {
                    if (direction <= 0) changes.Add(Tuple.Create(refinement ? .96 : .92, false));
                    if (direction >= 0) changes.Add(Tuple.Create(refinement ? 1.04 : 1.08, false));
                    if (direction != 0) changes.Add(Tuple.Create(direction < 0 ? (refinement ? .98 : .96) : (refinement ? 1.02 : 1.04), true));
                }
                foreach (var adjustment in changes)
                {
                    double change = adjustment.Item1;
                    bool sensitivity = adjustment.Item2;
                    var clone = DriverConfig.Convert(centerJson);
                    if (clone.Item1 == null || clone.Item2 != null) continue;
                    var profile = clone.Item1.profiles[profileIndex];
                    if (sensitivity) profile.outputDPI *= change;
                    else if (noAccel)
                    {
                        profile.argsX = Classic(profile.argsX, change);
                        if (!profile.inputSpeedArgs.combineMagnitudes) profile.argsY = Classic(profile.argsY, change);
                    }
                    else
                    {
                        var weights = profile.rangeXY;
                        weights.x = ChangedWeight(weights.x, change);
                        weights.y = ChangedWeight(weights.y, change);
                        profile.rangeXY = weights;
                    }
                    clone.Item1.SetProfileAt(profileIndex, profile);
                    string json = clone.Item1.ToJSON();
                    var validated = DriverConfig.Convert(json);
                    if (validated.Item1 == null || validated.Item2 != null ||
                        !ValidateModel(originalProfile, validated.Item1.profiles[profileIndex])) continue;
                    proposals.Add(new CalibrationProposal { SettingsJson = json,
                        Offset = noAccel ? CalibrationDefaults.CandidateOffset : profile.argsX.inputOffset,
                        Cap = noAccel ? change : profile.argsX.cap.y,
                        Description = sensitivity ? "Sensitivity " + (change < 1 ? "reduced" : "increased") + " " + (Math.Abs(change - 1) * 100).ToString("0") + "% to correct the first movement" :
                            noAccel ? "Classic acceleration, cap " + change.ToString("0.00") :
                            "Acceleration range " + (change < 1 ? "reduced" : "increased") + " " +
                            (Math.Abs(change - 1) * 100).ToString("0") + "%" });
                }
                reason = proposals.Count == 0 ? "No candidate passed native bounds for the measured profile." : null;
                return proposals;
            }
            catch (Exception e) { reason = "Could not validate candidates: " + e.Message; return new List<CalibrationProposal>(); }
        }

        private static double ChangedWeight(double weight, double factor)
        {
            if (!Finite(weight) || weight < 0) throw new ArgumentException("Invalid acceleration range.");
            return weight == 0 && factor > 1 ? 0.04 : weight * factor;
        }

        public static string DescribeMeasurements(IList<TrialMetrics> metrics)
        {
            if (metrics == null) return "No movements have been measured yet.";
            var valid = metrics.Where(UsableMetric).ToList();
            var first = valid.Where(m => m.InitialError.HasValue).ToList();
            if (first.Count == 0) return valid.Count + " movements recorded; first-movement error is not yet available.";
            int over = first.Count(m => m.InitialError.Value > 0), under = first.Count(m => m.InitialError.Value < 0);
            string trend = Direction(valid) < 0 ? "Try less sensitivity or acceleration." : Direction(valid) > 0 ?
                "Try more sensitivity or acceleration." : "First movements are balanced; compare small adjustments.";
            return "First movement: " + over + " past, " + under + " short, " + (first.Count - over - under) +
                " on target. Average error " + first.Average(m => Math.Abs(m.InitialError.Value)).ToString("0.0") +
                " px; corrections " + valid.Average(m => m.Corrections).ToString("0.0") + ". " + trend;
        }

        private static bool SameUnrelatedSettings(string baseline, string candidate, int index)
        {
            var original = DriverConfig.Convert(baseline);
            var proposed = DriverConfig.Convert(candidate);
            if (original.Item1 == null || proposed.Item1 == null || index >= original.Item1.profiles.Count ||
                index >= proposed.Item1.profiles.Count) return false;
            var before = original.Item1.profiles[index];
            var after = proposed.Item1.profiles[index];
            if (!Finite(before.outputDPI) || !Finite(after.outputDPI) || before.outputDPI == 0 ||
                after.outputDPI / before.outputDPI < .8 || after.outputDPI / before.outputDPI > 1.2) return false;
            bool originallyNoAccel = before.argsX.mode == AccelMode.noaccel &&
                (before.inputSpeedArgs.combineMagnitudes || before.argsY.mode == AccelMode.noaccel);
            if (originallyNoAccel && after.argsX.mode == AccelMode.classic)
            {
                double cap = after.argsX.cap.y;
                if (!CalibrationDefaults.CandidateCaps.Contains(cap)) return false;
                before.argsX = Classic(before.argsX, cap);
                if (!before.inputSpeedArgs.combineMagnitudes) before.argsY = Classic(before.argsY, cap);
                original.Item1.SetProfileAt(index, before);
            }
            var first = JObject.Parse(original.Item1.ToJSON()); var second = JObject.Parse(proposed.Item1.ToJSON());
            var a = first["profiles"] as JArray; var b = second["profiles"] as JArray;
            if (a == null || b == null || index >= a.Count || index >= b.Count) return false;
            var pa = a[index] as JObject; var pb = b[index] as JObject;
            if (pa == null || pb == null) return false;
            const string range = "Stretches accel range for horizontal vs vertical inputs";
            pa.Remove(range); pb.Remove(range);
            pa.Remove("Output DPI"); pb.Remove("Output DPI");
            return JToken.DeepEquals(first, second);
        }

        private static AccelArgs Classic(AccelArgs args, double cap)
        {
            args.mode = AccelMode.classic; args.gain = false; args.exponentClassic = 2;
            args.inputOffset = CalibrationDefaults.CandidateOffset;
            args.capMode = CapMode.output;
            var caps = args.cap; caps.y = cap; args.cap = caps;
            double span = CalibrationDefaults.CandidateReferenceSpeed - args.inputOffset;
            args.acceleration = (cap - 1) * CalibrationDefaults.CandidateReferenceSpeed / (span * span);
            return args;
        }

        // Each model is kept alive through a sequence so smoothing and speed clamps are exercised.
        private static bool ValidateModel(Profile baseline, Profile candidate)
        {
            return ValidateModel(baseline, candidate, 1 - MaximumRelativeChange, 1 + MaximumRelativeChange);
        }

        internal static bool ValidateSearchModel(Profile baseline, Profile candidate)
        {
            // Screening must obey the same response limit as local refinement: a
            // user experiences each candidate before there is evidence it helps.
            return ValidateModel(baseline, candidate);
        }

        private static bool ValidateModel(Profile baseline, Profile candidate, double minimumRatio, double maximumRatio)
        {
            bool changed = false;
            int[][] vectors = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 },
                new[] { 1, 1 }, new[] { -1, 1 }, new[] { 1, -1 }, new[] { -1, -1 } };
            // The driver calls modify only for nonzero motion packets.
            int[] sequence = { 1, 5, 15, 40, 120, 40, 15, 5, 1, 20, 60, 5 };
            foreach (double dpi in new[] { 0.5, 1.0, 2.0 })
            foreach (double time in new[] { 0.5, 1.0, 2.0 })
            foreach (int[] v in vectors)
            using (var oldModel = new ManagedAccel(baseline))
            using (var newModel = new ManagedAccel(candidate))
            {
                foreach (int speed in sequence)
                {
                    var a = oldModel.Accelerate(v[0] * speed, v[1] * speed, dpi, time);
                    var b = newModel.Accelerate(v[0] * speed, v[1] * speed, dpi, time);
                    if (!Finite(a.Item1) || !Finite(a.Item2) || !Finite(b.Item1) || !Finite(b.Item2)) return false;
                    double oldLength = Math.Sqrt(a.Item1 * a.Item1 + a.Item2 * a.Item2);
                    double newLength = Math.Sqrt(b.Item1 * b.Item1 + b.Item2 * b.Item2);
                    if (oldLength < 1e-9) { if (newLength > 1e-8) return false; continue; }
                    double ratio = newLength / oldLength;
                    if (!Finite(ratio) || ratio < minimumRatio || ratio > maximumRatio ||
                        a.Item1 * b.Item1 + a.Item2 * b.Item2 < -1e-9) return false;
                    if (Math.Abs(ratio - 1) > 1e-5) changed = true;
                }
            }
            // Fresh-state probes cover narrow features between the stateful sequence's speeds.
            for (int halfSpeed = 1; halfSpeed <= 240; halfSpeed++)
                if (!ProbeSpeed(baseline, candidate, halfSpeed / 2.0, ref changed, minimumRatio, maximumRatio)) return false;
            foreach (var args in new[] { baseline.argsX, baseline.argsY, candidate.argsX, candidate.argsY })
            {
                if (args.mode != AccelMode.lut || args.data == null) continue;
                for (int i = 0; i + 1 < args.length && i + 1 < args.data.Length; i += 2)
                {
                    double knot = args.data[i];
                    if (!Finite(knot) || knot <= 0) continue;
                    foreach (double multiplier in new[] { 0.9999, 1.0, 1.0001 })
                        if (!ProbeSpeed(baseline, candidate, knot * multiplier, ref changed, minimumRatio, maximumRatio)) return false;
                }
            }
            return changed;
        }

        private static bool ProbeSpeed(Profile baseline, Profile candidate, double speed, ref bool changed, double minimumRatio, double maximumRatio)
        {
            if (!Finite(speed) || speed <= 0) return false;
            double angle = baseline.rotation * Math.PI / 180;
            foreach (int axis in new[] { 0, 1 })
            {
                double x = axis == 0 ? Math.Cos(angle) : -Math.Sin(angle);
                double y = axis == 0 ? Math.Sin(angle) : Math.Cos(angle);
                double wx = Math.Abs(x * baseline.domainXY.x), wy = Math.Abs(y * baseline.domainXY.y);
                var weights = new List<double>();
                if (baseline.inputSpeedArgs.combineMagnitudes)
                {
                    double norm = baseline.inputSpeedArgs.lpNorm;
                    weights.Add(norm >= 16 || norm <= 0 ? Math.Max(wx, wy) :
                        Math.Pow(Math.Pow(wx, norm) + Math.Pow(wy, norm), 1 / norm));
                }
                else { weights.Add(wx); weights.Add(wy); }
                foreach (double weight in weights)
                {
                    if (!Finite(weight) || weight < 1e-9) continue;
                    double time = weight / speed;
                    if (!Finite(time) || time <= 0) continue;
                    using (var oldModel = new ManagedAccel(baseline))
                    using (var newModel = new ManagedAccel(candidate))
                    {
                        var a = oldModel.Accelerate(axis == 0 ? 1 : 0, axis == 1 ? 1 : 0, 1, time);
                        var b = newModel.Accelerate(axis == 0 ? 1 : 0, axis == 1 ? 1 : 0, 1, time);
                        if (!Finite(a.Item1) || !Finite(a.Item2) || !Finite(b.Item1) || !Finite(b.Item2)) return false;
                        double oldLength = Math.Sqrt(a.Item1 * a.Item1 + a.Item2 * a.Item2);
                        double newLength = Math.Sqrt(b.Item1 * b.Item1 + b.Item2 * b.Item2);
                        if (oldLength < 1e-9) { if (newLength > 1e-8) return false; continue; }
                        double ratio = newLength / oldLength;
                        if (!Finite(ratio) || ratio < minimumRatio || ratio > maximumRatio ||
                            a.Item1 * b.Item1 + a.Item2 * b.Item2 < -1e-9) return false;
                        if (Math.Abs(ratio - 1) > 1e-5) changed = true;
                    }
                }
            }
            return true;
        }
    }
}




