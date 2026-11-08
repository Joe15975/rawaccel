using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace grapher.Models.Calibration
{
    public static class CalibrationAnalysis
    {
        private static double Ms(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }
        private static double Length(double x, double y) { return Math.Sqrt(x * x + y * y); }
        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }

        public static TrialMetrics Analyze(TargetTrial trial)
        {
            if (trial == null) throw new ArgumentNullException("trial");
            var m = new TrialMetrics { ConditionId = trial.ConditionId, Radius = trial.Radius, InvalidReason = trial.InvalidReason };
            double dx = trial.TargetX - trial.StartX, dy = trial.TargetY - trial.StartY;
            m.Distance = Length(dx, dy);
            if (!Finite(m.Distance) || !Finite(trial.Radius) || trial.Radius <= 0 || m.Distance <= 0 || trial.Cursor.Count == 0 || trial.InvalidReason != null)
            {
                m.Invalid = true; m.InvalidReason = trial.InvalidReason ?? "Invalid target or missing cursor samples."; return m;
            }
            double ux = dx / m.Distance, uy = dy / m.Distance, path = 0, previousAxial = 0, accumulated = 0;
            int direction = 0, confirmedDirection = 0, onset = -1, dwellStart = -1, firstEntry = -1;
            long slowStart = -1;
            m.MinimumError = double.PositiveInfinity;
            for (int i = 0; i < trial.Cursor.Count; i++)
            {
                CursorSample p = trial.Cursor[i];
                if (!Finite(p.X) || !Finite(p.Y) || p.Ticks < trial.PresentedTicks || (i > 0 && (p.Ticks <= trial.Cursor[i-1].Ticks || Ms(p.Ticks - trial.Cursor[i-1].Ticks) > CalibrationDefaults.MaximumCursorGapMs)))
                { m.Invalid = true; m.InvalidReason = "Invalid cursor samples or sampling gap."; return m; }
                double ex = p.X - trial.TargetX, ey = p.Y - trial.TargetY;
                double error = Length(ex, ey), axial = ex * ux + ey * uy;
                m.MinimumError = Math.Min(m.MinimumError, error);
                m.Overshoot = Math.Max(m.Overshoot, axial);
                m.CrossTrackError = Math.Max(m.CrossTrackError, Math.Abs(ex * -uy + ey * ux));
                if (error <= trial.Radius) { if (firstEntry < 0) firstEntry = i; if (dwellStart < 0) dwellStart = i; } else dwellStart = -1;
                if (onset < 0 && Length(p.X - trial.StartX, p.Y - trial.StartY) >= Math.Max(2, trial.Radius * 0.1)) onset = i;
                if (i > 0)
                {
                    var prev = trial.Cursor[i-1];
                    double segment = Length(p.X - prev.X, p.Y - prev.Y);
                    path += segment;
                    double speed = segment / Ms(p.Ticks - prev.Ticks);
                    // Cursor cadence limits precision: use at least 10 ms windows where possible.
                    int windowStart = i - 1;
                    while (windowStart > 0 && Ms(p.Ticks - trial.Cursor[windowStart].Ticks) < 10) windowStart--;
                    double windowMs = Ms(p.Ticks - trial.Cursor[windowStart].Ticks);
                    if (windowMs >= 10)
                    {
                        double windowPath = 0;
                        for (int j = windowStart + 1; j <= i; j++)
                            windowPath += Length(trial.Cursor[j].X - trial.Cursor[j-1].X, trial.Cursor[j].Y - trial.Cursor[j-1].Y);
                        m.PeakSpeed = Math.Max(m.PeakSpeed, windowPath / windowMs);
                    }
                    if (onset >= 0 && firstEntry < 0 && !m.Undershoot.HasValue)
                    {
                        if (speed < Math.Max(0.02, trial.Radius * 0.002))
                        {
                            if (slowStart < 0) slowStart = prev.Ticks;
                            if (Ms(p.Ticks - slowStart) >= 40) m.Undershoot = Math.Max(0, -axial);
                        }
                        else slowStart = -1;
                    }
                    double change = axial - previousAxial;
                    int sign = Math.Sign(change);
                    if (sign != 0 && sign != direction) { accumulated = 0; direction = sign; }
                    accumulated += Math.Abs(change);
                    if (direction != 0 && accumulated >= Math.Max(2, trial.Radius * 0.2))
                    {
                        if (confirmedDirection != 0 && direction != confirmedDirection) m.Corrections++;
                        confirmedDirection = direction;
                        accumulated = 0;
                    }
                }
                previousAxial = axial;
            }
            var last = trial.Cursor[trial.Cursor.Count - 1];
            m.TerminalError = Length(last.X - trial.TargetX, last.Y - trial.TargetY);
            m.Failed = !trial.Completed || dwellStart < 0 || Ms(last.Ticks - trial.Cursor[dwellStart].Ticks) < CalibrationDefaults.DwellMs;
            AnalyzeFirstMovement(trial, m, onset, dwellStart, ux, uy);
            if (onset >= 0)
            {
                m.ReactionMs = Ms(trial.Cursor[onset].Ticks - trial.PresentedTicks);
                if (firstEntry >= onset) m.TimeToHitMs = Ms(trial.Cursor[firstEntry].Ticks - trial.Cursor[onset].Ticks);
            }
            m.MovementMs = onset < 0 ? 0 : Ms(last.Ticks - trial.Cursor[onset].Ticks);
            m.AcquisitionMs = m.Failed ? trial.TimeoutMs : Ms(last.Ticks - trial.PresentedTicks);
            m.PathEfficiency = path > 0 ? Math.Min(1, m.Distance / path) : 0;
            double duration = Ms(last.Ticks - trial.PresentedTicks);
            m.MeanSpeed = duration > 0 ? path / duration : 0;
            double rawPath = 0;
            foreach (var sample in trial.Motion) rawPath += Length(sample.X, sample.Y);
            m.RawMeanSpeed = duration > 0 ? rawPath / duration : 0;
            if (!m.Failed) m.SettlingMs = Ms(trial.Cursor[dwellStart].Ticks - trial.PresentedTicks);
            if (firstEntry >= 0 && Ms(last.Ticks - trial.Cursor[firstEntry].Ticks) >= CalibrationDefaults.DwellMs)
            {
                int end = firstEntry;
                while (end + 1 < trial.Cursor.Count && Ms(trial.Cursor[end + 1].Ticks - trial.Cursor[firstEntry].Ticks) <= CalibrationDefaults.DwellMs) end++;
                int n = end - firstEntry + 1;
                if (n >= 2)
                {
                    m.StableWindowComplete = true;
                    double meanX = 0, meanY = 0;
                    for (int i = firstEntry; i <= end; i++) { meanX += trial.Cursor[i].X; meanY += trial.Cursor[i].Y; }
                    meanX /= n; meanY /= n;
                    double sum = 0;
                    for (int i = firstEntry; i <= end; i++) { double x = trial.Cursor[i].X - meanX, y = trial.Cursor[i].Y - meanY; sum += x*x + y*y; }
                    m.Jitter = Math.Sqrt(sum / n);
                }
            }
            return m;
        }

        private static void AnalyzeFirstMovement(TargetTrial trial, TrialMetrics m, int onset, int dwellStart, double ux, double uy)
        {
            if (onset < 0) return;
            double substantial = Math.Max(trial.Radius * 2, m.Distance * 0.2);
            double hysteresis = Math.Max(2, trial.Radius * 0.25);
            double peak = double.NegativeInfinity;
            int peakIndex = -1, endIndex = -1, slowIndex = -1;
            bool moved = false;
            for (int i = onset; i < trial.Cursor.Count; i++)
            {
                var p = trial.Cursor[i];
                double axial = (p.X - trial.TargetX) * ux + (p.Y - trial.TargetY) * uy;
                double cross = Math.Abs((p.X - trial.TargetX) * -uy + (p.Y - trial.TargetY) * ux);
                if (Length(p.X - trial.StartX, p.Y - trial.StartY) >= substantial) moved = true;
                if (cross <= trial.Radius && axial > Math.Sqrt(Math.Max(0, trial.Radius * trial.Radius - cross * cross)) + 1e-6)
                    m.PassedTarget = true;
                if (axial > peak) { peak = axial; peakIndex = i; }
                if (moved && peak >= 0 && peakIndex >= 0 && peak - axial >= hysteresis)
                {
                    var top = trial.Cursor[peakIndex];
                    double topCross = Math.Abs((top.X - trial.TargetX) * -uy + (top.Y - trial.TargetY) * ux);
                    if (topCross <= trial.Radius) { endIndex = peakIndex; break; }
                }
                if (i == 0 || !moved) continue;
                var prev = trial.Cursor[i - 1];
                double speed = Length(p.X - prev.X, p.Y - prev.Y) / Ms(p.Ticks - prev.Ticks);
                if (speed < Math.Max(0.02, trial.Radius * 0.002))
                {
                    if (slowIndex < 0) slowIndex = i - 1;
                    if (Ms(p.Ticks - trial.Cursor[slowIndex].Ticks) >= 40) { endIndex = slowIndex; break; }
                }
                else slowIndex = -1;
            }
            if (endIndex < 0) return;
            var end = trial.Cursor[endIndex];
            double ex = end.X - trial.TargetX, ey = end.Y - trial.TargetY;
            double along = ex * ux + ey * uy, lateral = Math.Abs(ex * -uy + ey * ux);
            m.FirstMovementEndIndex = endIndex;
            m.InitialEndX = end.X; m.InitialEndY = end.Y;
            m.InitialMovementMs = Ms(end.Ticks - trial.Cursor[onset].Ticks);
            if (lateral <= trial.Radius)
            {
                double edge = Math.Sqrt(Math.Max(0, trial.Radius * trial.Radius - lateral * lateral));
                m.InitialError = along > edge ? along - edge : along < -edge ? along + edge : 0;
            }

            // A resume after stopping short is a correction even if its direction did not reverse.
            double correctionThreshold = Math.Max(2, trial.Radius * 0.2);
            int corrections = 0, lastDirection = 0;
            double anchorX = end.X, anchorY = end.Y, axialAnchor = along;
            for (int i = endIndex + 1; i < trial.Cursor.Count; i++)
            {
                var p = trial.Cursor[i];
                double fromAnchor = Length(p.X - anchorX, p.Y - anchorY);
                double a = (p.X - trial.TargetX) * ux + (p.Y - trial.TargetY) * uy;
                if (corrections == 0 && fromAnchor >= correctionThreshold)
                {
                    corrections = 1; lastDirection = Math.Sign(a - axialAnchor);
                    anchorX = p.X; anchorY = p.Y; axialAnchor = a;
                }
                else if (corrections > 0 && Math.Abs(a - axialAnchor) >= correctionThreshold)
                {
                    int nextDirection = Math.Sign(a - axialAnchor);
                    if (lastDirection != 0 && nextDirection != lastDirection) corrections++;
                    lastDirection = nextDirection; anchorX = p.X; anchorY = p.Y; axialAnchor = a;
                }
            }
            m.Corrections = corrections;
            if (!m.Failed)
            {
                if (m.InitialError == 0 && corrections == 0) m.CorrectionMs = 0;
                else if (dwellStart >= endIndex) m.CorrectionMs = Ms(trial.Cursor[dwellStart].Ticks - end.Ticks);
            }
            else if (!trial.Completed && corrections > 0)
                m.CorrectionMs = Ms(trial.Cursor[trial.Cursor.Count - 1].Ticks - end.Ticks);
        }

        private static double Median(IEnumerable<double> values)
        {
            var a = values.OrderBy(x => x).ToArray();
            return a.Length % 2 == 0 ? (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2 : a[a.Length / 2];
        }
        private static void ConsiderBenefit(CalibrationComparison result, double fraction, string label)
        {
            if (fraction <= result.SelectionScore) return;
            result.SelectionScore = fraction;
            result.Benefit = fraction.ToString("0.#%") + " " + label;
        }
        public static CalibrationComparison Compare(IList<TrialMetrics> baseline, IList<TrialMetrics> candidate)
        {
            var result = new CalibrationComparison();
            if (baseline == null || candidate == null || baseline.Count != candidate.Count) { result.Reason = "Matched trials are required."; return result; }
            var b = new List<double>(); var c = new List<double>();
            var groups = new Dictionary<int, int[]>();
            var stability = new Dictionary<int, List<double>[]>();
            var groupTimes = new Dictionary<int, List<double>[]>();
            var groupOvershoot = new Dictionary<int, List<double>[]>();
            var groupCorrections = new Dictionary<int, List<double>[]>();
            var firstErrors = new Dictionary<int, List<double>[]>();
            var firstBaseline = new List<double>(); var firstCandidate = new List<double>();
            var correctionTimes = new Dictionary<int, List<double>[]>();
            var correctionBaseline = new List<double>(); var correctionCandidate = new List<double>();
            var correctionCountsBaseline = new List<double>(); var correctionCountsCandidate = new List<double>();
            int firstWins = 0, correctionTimeWins = 0;
            for (int i = 0; i < baseline.Count; i++)
            {
                var x = baseline[i]; var y = candidate[i];
                if (x == null || y == null || x.Invalid || y.Invalid || x.ConditionId != y.ConditionId) continue;
                if (!Finite(x.AcquisitionMs) || !Finite(y.AcquisitionMs) || x.AcquisitionMs <= 0 || y.AcquisitionMs <= 0 ||
                    x.Distance <= 0 || y.Distance <= 0 || x.Radius <= 0 || y.Radius <= 0 ||
                    Math.Abs(x.Distance - y.Distance) > Math.Max(x.Distance, y.Distance) * 0.15 || Math.Abs(x.Radius-y.Radius) > 0.01) continue;
                b.Add(x.AcquisitionMs); c.Add(y.AcquisitionMs);
                if (y.AcquisitionMs < x.AcquisitionMs) result.Wins++;
                if (x.Failed) result.BaselineFailures++;
                if (y.Failed) result.CandidateFailures++;
                if (!groups.ContainsKey(x.ConditionId)) groups[x.ConditionId] = new int[2];
                if (x.Failed) groups[x.ConditionId][0]++;
                if (y.Failed) groups[x.ConditionId][1]++;
                int group = x.ConditionId / 8;
                if (!groupTimes.ContainsKey(group))
                {
                    groupTimes[group] = new[] { new List<double>(), new List<double>() };
                    groupOvershoot[group] = new[] { new List<double>(), new List<double>() };
                    groupCorrections[group] = new[] { new List<double>(), new List<double>() };
                }
                groupTimes[group][0].Add(x.AcquisitionMs); groupTimes[group][1].Add(y.AcquisitionMs);
                groupOvershoot[group][0].Add(x.Overshoot / x.Radius); groupOvershoot[group][1].Add(y.Overshoot / y.Radius);
                groupCorrections[group][0].Add(x.Corrections); groupCorrections[group][1].Add(y.Corrections);
                correctionCountsBaseline.Add(x.Corrections); correctionCountsCandidate.Add(y.Corrections);
                if (!x.Failed && !y.Failed && x.CorrectionMs.HasValue && y.CorrectionMs.HasValue && Finite(x.CorrectionMs.Value) &&
                    Finite(y.CorrectionMs.Value) && x.CorrectionMs.Value >= 0 && y.CorrectionMs.Value >= 0)
                {
                    if (!correctionTimes.ContainsKey(group)) correctionTimes[group] = new[] { new List<double>(), new List<double>() };
                    correctionTimes[group][0].Add(x.CorrectionMs.Value); correctionTimes[group][1].Add(y.CorrectionMs.Value);
                    correctionBaseline.Add(x.CorrectionMs.Value); correctionCandidate.Add(y.CorrectionMs.Value);
                    if (x.CorrectionMs.Value - y.CorrectionMs.Value >= 20 &&
                        y.CorrectionMs.Value <= x.CorrectionMs.Value * 0.85) correctionTimeWins++;
                }
                if (x.InitialError.HasValue && y.InitialError.HasValue && Finite(x.InitialError.Value) && Finite(y.InitialError.Value))
                {
                    if (!firstErrors.ContainsKey(group)) firstErrors[group] = new[] { new List<double>(), new List<double>() };
                    double oldError = Math.Abs(x.InitialError.Value) / x.Radius, newError = Math.Abs(y.InitialError.Value) / y.Radius;
                    firstErrors[group][0].Add(oldError); firstErrors[group][1].Add(newError);
                    firstBaseline.Add(oldError); firstCandidate.Add(newError);
                    if (oldError - newError >= 0.1 || (y.Corrections < x.Corrections && newError <= oldError + 0.1)) firstWins++;
                }
                if (x.Jitter.HasValue && y.Jitter.HasValue && Finite(x.Jitter.Value) && Finite(y.Jitter.Value) && x.Jitter >= 0 && y.Jitter >= 0)
                {
                    if (!stability.ContainsKey(group)) stability[group] = new[] { new List<double>(), new List<double>() };
                    stability[group][0].Add(x.Jitter.Value / x.Radius);
                    stability[group][1].Add(y.Jitter.Value / y.Radius);
                }
            }
            result.Pairs = b.Count;
            if (b.Count < CalibrationDefaults.MinimumComparisonPairs) { result.Reason = "Not enough valid matched trials."; return result; }
            // A fast result in one distance must not mask missing or invalid trials at other distances.
            for (int group = 0; group < 3; group++)
                if (!groupTimes.ContainsKey(group) || groupTimes[group][0].Count < 4)
                { result.Reason = "Not enough valid matched trials in each distance group."; return result; }
            result.BaselineMedianMs = Median(b); result.CandidateMedianMs = Median(c);
            result.ImprovementFraction = (result.BaselineMedianMs - result.CandidateMedianMs) / result.BaselineMedianMs;
            bool noFailureRegression = groups.Values.All(g => g[1] <= g[0]);
            string stabilityIssue = null;
            string[] distanceNames = { "Short", "Medium", "Long" };
            for (int group = 0; group < distanceNames.Length; group++)
            {
                if (!stability.ContainsKey(group))
                {
                    stabilityIssue = distanceNames[group] + " targets had no matched jitter readings.";
                    break;
                }
                double original = Median(stability[group][0]);
                double adjusted = Median(stability[group][1]);
                double limit = original * 1.1 + 0.01;
                if (!(adjusted <= limit))
                {
                    stabilityIssue = string.Format("{0} targets: median jitter rose from {1:0.#}% to {2:0.#}% of target radius (limit {3:0.#}%, {4} pairs).",
                        distanceNames[group], original * 100, adjusted * 100, limit * 100, stability[group][0].Count);
                    break;
                }
            }
            bool noAccuracyRegression = groupTimes.Keys.All(k =>
                Median(groupOvershoot[k][1]) <= Median(groupOvershoot[k][0]) + 0.1 &&
                Median(groupCorrections[k][1]) <= Median(groupCorrections[k][0]));
            bool normalTime = groupTimes.Keys.All(k => Median(groupTimes[k][1]) <= Median(groupTimes[k][0]) * 1.05);
            bool boundedTime = groupTimes.Keys.All(k => Median(groupTimes[k][1]) <= Median(groupTimes[k][0]) * 1.15);
            bool faster = normalTime && result.ImprovementFraction >= CalibrationDefaults.MinimumImprovement && result.Wins * 3 >= b.Count * 2;
            bool enoughFirst = firstBaseline.Count >= CalibrationDefaults.MinimumComparisonPairs &&
                Enumerable.Range(0, 3).All(k => firstErrors.ContainsKey(k) && firstErrors[k][0].Count >= 4);
            if (enoughFirst && Median(firstBaseline) > 0)
                result.FirstMovementImprovementFraction = (Median(firstBaseline) - Median(firstCandidate)) / Median(firstBaseline);
            bool enoughCorrection = correctionBaseline.Count >= CalibrationDefaults.MinimumComparisonPairs &&
                Enumerable.Range(0, 3).All(k => correctionTimes.ContainsKey(k) && correctionTimes[k][0].Count >= 4);
            if (enoughCorrection && Median(correctionBaseline) > 0)
                result.CorrectionImprovementFraction = (Median(correctionBaseline) - Median(correctionCandidate)) / Median(correctionBaseline);
            bool noFirstRegression = enoughFirst && groupTimes.Keys.All(k => firstErrors.ContainsKey(k) &&
                Median(firstErrors[k][1]) <= Median(firstErrors[k][0]) + 0.1);
            bool betterFirst = enoughFirst && firstWins * 3 >= firstBaseline.Count * 2 &&
                (Median(firstBaseline) - Median(firstCandidate) >= 0.1 ||
                 groupTimes.Keys.Any(k => Median(groupCorrections[k][0]) - Median(groupCorrections[k][1]) >= 1));
            bool betterCorrectionTime = enoughCorrection && enoughFirst && noFirstRegression &&
                correctionTimeWins * 3 >= correctionBaseline.Count * 2 &&
                Median(correctionBaseline) - Median(correctionCandidate) >= 20 &&
                Median(correctionCandidate) <= Median(correctionBaseline) * 0.85 &&
                correctionTimes.Keys.All(k => Median(correctionTimes[k][1]) <= Median(correctionTimes[k][0]) * 1.15);
            bool noCorrectionTimeRegression = !enoughCorrection ||
                correctionTimes.Keys.All(k => Median(correctionTimes[k][1]) <= Median(correctionTimes[k][0]) * 1.15);
            if (!noFailureRegression) result.Reason = "Candidate had more failures in at least one target condition.";
            else if (stabilityIssue != null) result.Reason = stabilityIssue;
            else if (!noAccuracyRegression) result.Reason = "Candidate increased overshoot or corrections in at least one distance group.";
            else if (!noCorrectionTimeRegression) result.Reason = "Candidate correction time increased by more than 15% in a distance group.";
            else if (faster && (!enoughFirst || noFirstRegression)) { result.Improved = true; result.Reason = "Candidate reduced acquisition time without a measured accuracy or stability regression."; }
            else if (boundedTime && noFirstRegression && betterFirst)
            { result.Improved = true; result.Reason = "Candidate improved first-approach accuracy or reduced corrections without a large time regression."; }
            else if (boundedTime && betterCorrectionTime)
            { result.Improved = true; result.Reason = "Candidate reduced correction time without a measured first-approach, failure or stability regression."; }
            else if (!boundedTime) result.Reason = "Candidate acquisition time increased by more than 15% in a distance group.";
            else result.Reason = "The measured time, first-approach accuracy and corrections did not show a consistent improvement.";
            if (result.Improved)
            {
                // Rank only benefits which independently qualified in this comparison.
                if (faster && (!enoughFirst || noFirstRegression))
                    ConsiderBenefit(result, result.ImprovementFraction, "faster target acquisition");
                if (boundedTime && noFirstRegression && betterFirst)
                {
                    if (Median(firstBaseline) - Median(firstCandidate) >= 0.1 && result.FirstMovementImprovementFraction.HasValue)
                        ConsiderBenefit(result, result.FirstMovementImprovementFraction.Value, "less first-movement error");
                    double baselineCount = Median(correctionCountsBaseline);
                    if (baselineCount > 0)
                        ConsiderBenefit(result, (baselineCount - Median(correctionCountsCandidate)) / baselineCount, "fewer corrections");
                    if (result.Benefit == null) result.Benefit = "fewer corrections in a tested distance group";
                }
                if (boundedTime && betterCorrectionTime)
                    ConsiderBenefit(result, result.CorrectionImprovementFraction.Value, "less correction time");
            }
            return result;
        }
    }
}
