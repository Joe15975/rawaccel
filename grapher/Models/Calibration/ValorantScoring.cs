using System;
using System.Collections.Generic;
using System.Linq;

namespace grapher.Models.Calibration
{
    public static class ValorantScoring
    {
        private static readonly string[] Categories = { "Micro", "Medium", "Wide" };
        private static double Median(IEnumerable<double> values)
        {
            var a = values.OrderBy(x => x).ToArray();
            return a.Length % 2 == 0 ? (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2 : a[a.Length / 2];
        }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        // Lower is better. A single additional miss outweighs every possible time gain.
        // The caller must supply the same target conditions to every candidate; four
        // (one or two per category) or six (two per category) trials are accepted.
        public static double RankScreen(IList<TrialMetrics> trials)
        {
            if (trials == null || trials.Count != 4 && trials.Count != 6 || trials.Any(x => x == null || x.Invalid ||
                !Categories.Contains(x.Category) || !Finite(x.Distance) || x.Distance < 0 ||
                !Finite(x.Radius) || x.Radius <= 0) ||
                Categories.Any(c => trials.Count(x => x.Category == c) == 0) ||
                trials.Select(x => x.ConditionId).Distinct().Count() != trials.Count)
                return double.PositiveInfinity;
            int failures = 0;
            double time = 0;
            foreach (var trial in trials)
            {
                if (trial.Failed || trial.TimedOut) { failures++; time += CalibrationDefaults.TimeoutMs; }
                else
                {
                    if (!Finite(trial.MovementMs) || trial.MovementMs < 0) return double.PositiveInfinity;
                    // A same-packet click has unresolved duration; do not reward it as instant.
                    time += trial.MovementMs == 0 ? CalibrationDefaults.TimeoutMs : Math.Min(trial.MovementMs, CalibrationDefaults.TimeoutMs);
                }
            }
            return failures * (6 * CalibrationDefaults.TimeoutMs + 1) + time;
        }
        public static CalibrationComparison Compare(IList<TrialMetrics> baseline, IList<TrialMetrics> candidate)
        {
            var result = new CalibrationComparison();
            if (baseline == null || candidate == null || baseline.Count != candidate.Count ||
                baseline.Any(x => x == null) || candidate.Any(x => x == null) ||
                baseline.Select(x => x.ConditionId).Distinct().Count() != baseline.Count ||
                candidate.Select(x => x.ConditionId).Distinct().Count() != candidate.Count)
            { result.Reason = "Matched trials are required."; return result; }
            var oldTimes = new List<double>(); var newTimes = new List<double>(); var pairedGains = new List<double>();
            int accuracyWins = 0, accuracyLosses = 0, timeWins = 0;
            bool enoughSpeedByCategory = true;
            foreach (string category in Categories)
            {
                var oldGroup = baseline.Where(x => x != null && x.Category == category).OrderBy(x => x.ConditionId).ToList();
                var newGroup = candidate.Where(x => x != null && x.Category == category).OrderBy(x => x.ConditionId).ToList();
                if (oldGroup.Count < 4 || oldGroup.Count != newGroup.Count ||
                    oldGroup.Select(x => x.ConditionId).Distinct().Count() != oldGroup.Count ||
                    newGroup.Select(x => x.ConditionId).Distinct().Count() != newGroup.Count)
                { result.Reason = "Not enough matched trials in each target category."; return result; }
                int oldMiss = 0, newMiss = 0, oldTimeout = 0, newTimeout = 0;
                var b = new List<double>(); var c = new List<double>();
                for (int i = 0; i < oldGroup.Count; i++)
                {
                    var x = oldGroup[i]; var y = newGroup[i];
                    if (x.Invalid || y.Invalid || x.ConditionId != y.ConditionId ||
                        !Finite(x.Distance) || !Finite(y.Distance) || x.Distance < 0 || y.Distance < 0 ||
                        !Finite(x.Radius) || !Finite(y.Radius) || x.Radius <= 0 || y.Radius <= 0 ||
                        Math.Abs(x.Distance - y.Distance) > 1e-8 || Math.Abs(x.Radius - y.Radius) > 1e-8)
                    { result.Reason = "A matched trial is missing or invalid."; return result; }
                    bool xf = x.Failed || x.TimedOut, yf = y.Failed || y.TimedOut;
                    if (xf) oldMiss++; if (yf) newMiss++;
                    if (x.TimedOut) oldTimeout++; if (y.TimedOut) newTimeout++;
                    if (xf && !yf) accuracyWins++; if (!xf && yf) accuracyLosses++;
                    result.Pairs++;
                    if (!xf && !yf)
                    {
                        if (double.IsNaN(x.MovementMs) || double.IsInfinity(x.MovementMs) || x.MovementMs < 0 ||
                            double.IsNaN(y.MovementMs) || double.IsInfinity(y.MovementMs) || y.MovementMs < 0)
                        { result.Reason = "A successful trial has no valid movement-to-shot time."; return result; }
                        // Movement and the click can arrive in one raw packet. The hit is valid
                        // but that packet cannot resolve the time between the two events.
                        if (x.MovementMs == 0 || y.MovementMs == 0) continue;
                        b.Add(x.MovementMs); c.Add(y.MovementMs); pairedGains.Add((x.MovementMs - y.MovementMs) / x.MovementMs);
                        if (y.MovementMs <= x.MovementMs * .95) timeWins++;
                    }
                }
                result.BaselineFailures += oldMiss; result.CandidateFailures += newMiss;
                if (newMiss > oldMiss || newTimeout > oldTimeout)
                { result.Reason = "First-shot accuracy or timeouts worsened in " + category + "."; return result; }
                if (b.Count < 2) enoughSpeedByCategory = false;
                if (b.Count >= 2 && Median(c) > Median(b) * 1.15)
                { result.Reason = "Movement-to-shot time worsened in " + category + "."; return result; }
                oldTimes.AddRange(b); newTimes.AddRange(c);
            }
            if (result.Pairs != baseline.Count)
            { result.Reason = "Unknown or missing target category."; return result; }
            result.Wins = accuracyWins;
            if (oldTimes.Count > 0)
            {
                result.BaselineMedianMs = Median(oldTimes); result.CandidateMedianMs = Median(newTimes);
                result.ImprovementFraction = Median(pairedGains);
            }
            // Require multiple discordant pairs to avoid selecting a candidate on a single lucky shot.
            if (accuracyWins >= 2 && accuracyWins >= accuracyLosses + 2)
            { result.Improved = true; result.SelectionScore = 1 + (double)(accuracyWins - accuracyLosses) / result.Pairs; result.Reason = "First-shot accuracy improved without a category regression."; }
            else if (accuracyWins == accuracyLosses && oldTimes.Count >= 6 && enoughSpeedByCategory &&
                result.ImprovementFraction >= .05 && timeWins * 3 >= oldTimes.Count * 2)
            { result.Improved = true; result.SelectionScore = result.ImprovementFraction; result.Reason = "Matched first-shot accuracy was unchanged and movement-to-shot time improved."; }
            else result.Reason = "The difference is too small or inconsistent to select a candidate.";
            return result;
        }
    }
}





