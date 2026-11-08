using System;
using System.Collections.Generic;
using System.Linq;

namespace grapher.Models.Calibration
{
    // A finite screening set. Every proposal is parsed and probed by the native model.
    public static class CalibrationSearch
    {
        public static IList<CalibrationProposal> CreateCandidates(string json, int profileIndex,
            IList<TrialMetrics> measurements, out string reason)
        {
            var results = new List<CalibrationProposal>();
            reason = "Invalid baseline profile.";
            try
            {
                var baseline = DriverConfig.Convert(json);
                if (baseline.Item1 == null || baseline.Item2 != null || profileIndex < 0 || profileIndex >= baseline.Item1.profiles.Count)
                    return results;
                var original = baseline.Item1.profiles[profileIndex];
                double baseFactor = 1;
                var near = measurements == null ? new List<TrialMetrics>() : measurements.Where(m => m != null && !m.Invalid &&
                    m.Category == "Micro" && m.InitialError.HasValue && !double.IsNaN(m.InitialError.Value) &&
                    !double.IsInfinity(m.InitialError.Value) && m.Radius > 0 && !double.IsInfinity(m.Radius)).ToList();
                int over = near.Count(m => m.InitialError.Value > .15 * m.Radius);
                int under = near.Count(m => m.InitialError.Value < -.15 * m.Radius);
                if (over >= 2 && over > under) baseFactor = .9;
                else if (under >= 2 && under > over) baseFactor = 1.1;
                // Keep the family screen first so a fixed session budget cannot hide whole families.
                string[] variants = { "Off", "Linear", "Classic", "Natural", "Jump", "Synchronous", "Power", "LUT",
                    "Sensitivity down", "Sensitivity up", "Classic gain", "Natural gain" };
                foreach (string variant in variants)
                {
                    var copy = DriverConfig.Convert(json);
                    if (copy.Item1 == null || copy.Item2 != null) continue;
                    var profile = copy.Item1.profiles[profileIndex];
                    if (variant == "Sensitivity down") profile.outputDPI *= .90;
                    else if (variant == "Sensitivity up") profile.outputDPI *= 1.10;
                    else
                    {
                        profile.outputDPI *= baseFactor;
                        profile.argsX = Preset(profile.argsX, variant);
                        if (!profile.inputSpeedArgs.combineMagnitudes) profile.argsY = Preset(profile.argsY, variant);
                    }
                    copy.Item1.SetProfileAt(profileIndex, profile);
                    string candidate = copy.Item1.ToJSON();
                    var parsed = DriverConfig.Convert(candidate);
                    if (parsed.Item1 == null || parsed.Item2 != null ||
                        !CalibrationOptimizer.ValidateSearchModel(original, parsed.Item1.profiles[profileIndex]))
                    {
                        // Fit Natural to the actual baseline rather than discarding the family
                        // just because a fixed curve is too far from an existing configuration.
                        if (variant != "Natural") continue;
                        candidate = FitNatural(json, profileIndex, original, baseFactor);
                        if (candidate == null) continue;
                        parsed = DriverConfig.Convert(candidate);
                        profile = parsed.Item1.profiles[profileIndex];
                    }
                    if (results.Any(p => p.SettingsJson == candidate)) continue;
                    results.Add(new CalibrationProposal { SettingsJson = candidate, Description = variant,
                        Offset = profile.argsX.inputOffset, Cap = profile.argsX.cap.y });
                }
                reason = results.Count == 0 ? "No candidate passed native model safety checks." : null;
                return results;
            }
            catch (Exception e) { reason = "Could not construct candidates: " + e.Message; return new List<CalibrationProposal>(); }
        }

        private static string FitNatural(string json, int index, Profile original, double baseFactor)
        {
            var copy = DriverConfig.Convert(json).Item1;
            var profile = copy.profiles[index];
            // Compare with the same profile with acceleration disabled, so rotation,
            // sensitivity, device normalization and directional scaling cancel out.
            var flat = DriverConfig.Convert(json).Item1.profiles[index];
            var flatX = flat.argsX; flatX.mode = AccelMode.noaccel; flat.argsX = flatX;
            var flatY = flat.argsY; flatY.mode = AccelMode.noaccel; flat.argsY = flatY;
            int[] speeds = { 1, 5, 10, 20, 40, 80, 120, 240 };
            var desired = new double[speeds.Length];
            var flatOutput = new double[speeds.Length];
            for (int i = 0; i < speeds.Length; i++)
            {
                flatOutput[i] = Response(flat, speeds[i]);
                double current = Response(original, speeds[i]);
                if (flatOutput[i] <= 1e-9 || current <= 1e-9 || double.IsNaN(current) || double.IsInfinity(current)) return null;
                desired[i] = current / flatOutput[i];
            }
            double peak = desired.Max();
            // Flat and unusually large responses need no speculative extrapolation.
            if (peak > 8 || peak < 0.1) return null;
            var limits = new[] { 1.1, peak, peak * 1.1, peak * 1.25, peak * 1.5, 2.0, 3.0, 4.0 }
                .Where(x => x > 1.001 && x <= 8).Distinct().ToArray();
            var offsets = new[] { 0.0, 5.0, 15.0, original.argsX.inputOffset }.Where(x => x >= 0 && x <= 120).Distinct();
            var fits = new List<Tuple<double, double, double, double>>();
            foreach (double limit in limits)
            foreach (double offset in offsets)
            foreach (double decay in new[] { .002, .005, .01, .02, .04, .08, .16, .32, .64 })
            {
                profile.outputDPI = original.outputDPI * baseFactor;
                var x = profile.argsX; x.mode = AccelMode.natural; x.gain = false;
                x.limit = limit; x.inputOffset = offset; x.decayRate = decay; profile.argsX = x;
                if (!profile.inputSpeedArgs.combineMagnitudes)
                {
                    var y = profile.argsY; y.mode = AccelMode.natural; y.gain = false;
                    y.limit = limit; y.inputOffset = offset; y.decayRate = decay; profile.argsY = y;
                }
                double error = 0;
                for (int i = 0; i < speeds.Length; i++)
                {
                    double ratio = Response(profile, speeds[i]) / (flatOutput[i] * desired[i]);
                    if (double.IsNaN(ratio) || double.IsInfinity(ratio)) { error = double.PositiveInfinity; break; }
                    error = Math.Max(error, Math.Abs(ratio - 1));
                }
                if (error <= .2) fits.Add(Tuple.Create(error, limit, offset, decay));
            }
            foreach (var fit in fits.OrderBy(x => x.Item1).Take(20))
            {
                var x = profile.argsX; x.limit = fit.Item2; x.inputOffset = fit.Item3; x.decayRate = fit.Item4; profile.argsX = x;
                if (!profile.inputSpeedArgs.combineMagnitudes)
                {
                    var y = profile.argsY; y.limit = fit.Item2; y.inputOffset = fit.Item3; y.decayRate = fit.Item4; profile.argsY = y;
                }
                copy.SetProfileAt(index, profile);
                string candidate = copy.ToJSON();
                var parsed = DriverConfig.Convert(candidate);
                if (parsed.Item1 != null && parsed.Item2 == null && CalibrationOptimizer.ValidateSearchModel(original, parsed.Item1.profiles[index]))
                    return candidate;
            }
            return null;
        }

        private static double Response(Profile profile, int speed)
        {
            using (var model = new ManagedAccel(profile))
            {
                var result = model.Accelerate(speed, 0, 1, 1);
                return Math.Sqrt(result.Item1 * result.Item1 + result.Item2 * result.Item2);
            }
        }

        private static AccelArgs Preset(AccelArgs args, string variant)
        {
            args.gain = variant.EndsWith(" gain", StringComparison.Ordinal);
            var cap = args.cap;
            switch (variant)
            {
                case "Off": args.mode = AccelMode.noaccel; break;
                case "Linear":
                case "Classic":
                case "Classic gain":
                    args.mode = AccelMode.classic; args.exponentClassic = variant == "Linear" ? 2 : 2.4;
                    args.inputOffset = variant == "Classic gain" ? 10 : 5;
                    args.capMode = CapMode.output; cap.y = variant == "Classic gain" ? 1.5 : 1.3;
                    args.acceleration = variant == "Linear" ? .01 : .03; args.cap = cap; break;
                case "Natural":
                case "Natural gain":
                    args.mode = AccelMode.natural; args.inputOffset = variant == "Natural gain" ? 10 : 5;
                    args.limit = 1.1; args.decayRate = .05; break;
                case "Jump":
                    args.mode = AccelMode.jump; cap.x = 15; cap.y = 1.5; args.cap = cap; args.smooth = .5; break;
                case "Synchronous":
                    args.mode = AccelMode.synchronous; args.motivity = 1.3; args.gamma = 1; args.syncSpeed = 15; args.smooth = .5; break;
                case "Power":
                    args.mode = AccelMode.power; args.exponentPower = .08; args.scale = 1; args.outputOffset = 1;
                    args.capMode = CapMode.output; cap.y = 1.5; args.cap = cap; break;
                case "LUT":
                    args.mode = AccelMode.lut; args.data = new float[514]; args.length = 8;
                    args.data[0] = 1; args.data[1] = 1; args.data[2] = 10; args.data[3] = 1;
                    args.data[4] = 30; args.data[5] = 1.3f; args.data[6] = 120; args.data[7] = 1.5f; break;
            }
            return args;
        }
    }
}

