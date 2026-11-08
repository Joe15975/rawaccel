using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using grapher.Models.Calibration;
using RawAccel.ServiceSupport;

namespace grapher
{
    public sealed partial class CalibrationForm
    {
        private bool valorantViewport;
        private bool valorantWindowTransition, valorantCursorHidden;
        private Rectangle valorantPreviousBounds;
        private FormBorderStyle valorantPreviousBorder;
        private FormWindowState valorantPreviousState;
        private int valorantSeed, valorantShotsCompleted, valorantShotsTotal;
        private string valorantProgress = "Practice", valorantFeedback = "";
        private ValorantAimTrial aimTrial;
        private ValorantAimCondition aimCondition;
        private TaskCompletionSource<TrialMetrics> aimCompletion;
        private Rectangle oldClip;
        private Control[] hiddenControls;
        private bool[] previousVisibility;

        private void BeginValorantViewport()
        {
            hiddenControls = Controls.Cast<Control>().Where(c => c != target).ToArray();
            previousVisibility = hiddenControls.Select(c => c.Visible).ToArray();
            if (!syntheticCursor)
            {
                oldClip = Cursor.Clip;
                valorantPreviousState = WindowState;
                valorantPreviousBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                valorantPreviousBorder = FormBorderStyle;
                var monitorBounds = Screen.FromControl(this).Bounds;
                valorantViewport = true;
                valorantWindowTransition = true;
                try
                {
                    WindowState = FormWindowState.Normal;
                    FormBorderStyle = FormBorderStyle.None;
                    Bounds = monitorBounds;
                    initialBounds = Bounds;
                }
                finally { valorantWindowTransition = false; }
            }
            valorantViewport = true;
            foreach (var control in hiddenControls) control.Visible = false;
            target.Dock = DockStyle.Fill; target.BringToFront();
            if (!syntheticCursor)
            {
                Cursor.Clip = target.RectangleToScreen(target.ClientRectangle);
                Cursor.Position = target.PointToScreen(new Point(target.Width / 2, target.Height / 2));
                Cursor.Hide();
                valorantCursorHidden = true;
                target.Capture = true;
            }
            target.Invalidate();
        }
        private void EndValorantViewport()
        {
            if (!valorantViewport) return;
            cursorTimer.Stop();
            if (!syntheticCursor)
            {
                target.Capture = false;
                Cursor.Clip = oldClip;
                if (valorantCursorHidden) { Cursor.Show(); valorantCursorHidden = false; }
            }
            valorantViewport = false; aimTrial = null; aimCondition = null;
            for (int i = 0; i < hiddenControls.Length; i++) hiddenControls[i].Visible = previousVisibility[i];
            target.SendToBack(); target.Invalidate();
            if (!syntheticCursor)
            {
                valorantWindowTransition = true;
                try
                {
                    FormBorderStyle = valorantPreviousBorder;
                    Bounds = valorantPreviousBounds;
                    WindowState = valorantPreviousState;
                    initialBounds = Bounds;
                }
                finally { valorantWindowTransition = false; }
            }
        }
        private void StopValorant()
        {
            if (aimCompletion != null) aimCompletion.TrySetCanceled();
            EndValorantViewport();
        }
        private void ObserveValorant(MotionSample sample)
        {
            if (aimTrial == null || aimTrial.Finished) return;
            if (sample.Device == IntPtr.Zero || (sample.Device != detectedDevice.Handle && (sample.X != 0 || sample.Y != 0 || sample.Buttons != 0)))
            {
                Interrupt("Input from another or unknown device interrupted calibration."); return;
            }
            if (sample.Device != detectedDevice.Handle) return;
            aimTrial.Observe(sample);
            if (aimTrial.Finished) CompleteValorantTrial();
        }
        private void TickValorant()
        {
            if (aimTrial == null) return;
            aimTrial.Timeout(Stopwatch.GetTimestamp());
            if (aimTrial.Finished) CompleteValorantTrial();
            else target.Invalidate();
        }
        private void CompleteValorantTrial()
        {
            cursorTimer.Stop();
            var result = aimTrial.Metrics;
            valorantFeedback = result.TimedOut ? "Timeout" : result.Failed ? "Miss" : "Hit";
            aimTrial = null; aimCondition = null;
            target.Invalidate();
            var completion = aimCompletion; aimCompletion = null;
            completion.TrySetResult(result);
        }
        private void PaintValorant(Graphics g)
        {
            g.Clear(Color.FromArgb(32, 38, 43));
            int width = Math.Min(target.Width, target.Height * 16 / 9);
            int height = width * 9 / 16;
            int left = (target.Width - width) / 2, top = (target.Height - height) / 2;
            g.FillRectangle(Brushes.Black, left, top, width, height);
            if (width > 0 && height > 0 && aimCondition != null)
            {
                // Project the angular hit boundary itself so the visible shape matches scoring.
                var points = new List<PointF>();
                double yaw = aimCondition.Yaw * Math.PI / 180, pitch = aimCondition.Pitch * Math.PI / 180;
                double radius = aimCondition.Radius * Math.PI / 180;
                for (int i = 0; i < 48; i++)
                {
                    double a = 2 * Math.PI * i / 48;
                    double vx = Math.Cos(radius) * Math.Cos(pitch) * Math.Cos(yaw) + Math.Sin(radius) * (-Math.Sin(pitch) * Math.Cos(yaw) * Math.Cos(a) - Math.Sin(yaw) * Math.Sin(a));
                    double vy = Math.Cos(radius) * Math.Cos(pitch) * Math.Sin(yaw) + Math.Sin(radius) * (-Math.Sin(pitch) * Math.Sin(yaw) * Math.Cos(a) + Math.Cos(yaw) * Math.Sin(a));
                    double vz = Math.Cos(radius) * Math.Sin(pitch) + Math.Sin(radius) * Math.Cos(pitch) * Math.Cos(a);
                    var point = ValorantAim.ProjectTarget(Math.Atan2(vy, vx) * 180 / Math.PI, Math.Asin(Math.Max(-1, Math.Min(1, vz))) * 180 / Math.PI, aimTrial?.Yaw ?? 0, aimTrial?.Pitch ?? 0, width, height);
                    if (float.IsNaN(point.X) || float.IsInfinity(point.X) || float.IsNaN(point.Y) || float.IsInfinity(point.Y)) { points.Clear(); break; }
                    points.Add(new PointF(left + point.X, top + point.Y));
                }
                if (points.Count >= 3) { var state = g.Save(); g.SetClip(new Rectangle(left, top, width, height)); g.FillPolygon(Brushes.OrangeRed, points.ToArray()); g.Restore(state); }
            }
            float x = target.Width / 2f, y = target.Height / 2f;
            using (var pen = new Pen(Color.Lime, 2)) { g.DrawLine(pen, x - 7, y, x + 7, y); g.DrawLine(pen, x, y - 7, x, y + 7); }
            g.DrawString(valorantProgress + "  " + valorantFeedback + " - aim and fire once. Escape cancels.", Font, Brushes.White, left + 10, top + 10);
        }
        private async Task CheckValorantProfile(string expected)
        {
            if (stopping) throw new OperationCanceledException();
            if (lease != null) await Send("trial-query");
            if (SettingsFingerprint.Compute(readActive().ToJSON()) != expected)
                throw new InvalidOperationException("The active profile changed during calibration.");
            if (stopping) throw new OperationCanceledException();
        }

        private async Task<List<TrialMetrics>> ValorantBlock(int count, int seed, string stage, bool reverse = false)
        {
            string expected = SettingsFingerprint.Compute(active);
            await CheckValorantProfile(expected);
            var results = new List<TrialMetrics>();
            int number = 0;
            var conditions = ValorantAim.CreateConditions(count, unchecked(seed + valorantSeed));
            if (reverse) conditions.Reverse();
            foreach (var condition in conditions)
            {
                if (stopping) throw new OperationCanceledException();
                if (!pointerSettings.SequenceEqual(ReadPointerSettings())) throw new InvalidOperationException("Windows pointer settings changed during calibration.");
                valorantProgress = stage + " — " + (++number) + "/" + count + "   Overall " + valorantShotsCompleted + "/" + (valorantShotsTotal > 0 ? valorantShotsTotal.ToString() : "calculating");
                aimCondition = condition;
                target.Refresh();
                aimCompletion = new TaskCompletionSource<TrialMetrics>();
                aimTrial = new ValorantAimTrial(condition, (double)valorantSensitivity.Value, Stopwatch.GetTimestamp(), 3000);
                cursorTimer.Start();
                var result = await aimCompletion.Task;
                if (result.Invalid) throw new InvalidOperationException(result.InvalidReason);
                results.Add(result);
                valorantShotsCompleted++;
                await delay(200);
            }
            await CheckValorantProfile(expected);
            return results;
        }
        private async Task<List<TrialMetrics>> ValorantMeasure(string json, int seed, string stage, int count, bool reverse = false)
        {
            if (stopping) throw new OperationCanceledException();
            await SetTrial(json);
            if (stopping) throw new OperationCanceledException();
            await ValorantBlock(1, seed + 1000, stage + " acclimation");
            return await ValorantBlock(count, seed, stage, reverse);
        }
        private void ShowValorantComparison(IList<TrialMetrics> before, IList<TrialMetrics> after)
        {
            metricsTable.Rows.Clear();
            metricsTable.Columns[1].HeaderText = "Current"; metricsTable.Columns[2].HeaderText = "Adjustment"; metricsTable.Columns[3].HeaderText = "Change";
            foreach (string category in new[] { "Micro", "Medium", "Wide" })
            {
                var a = before.Where(m => m.Category == category).ToArray();
                var b = after == null ? new TrialMetrics[0] : after.Where(m => m.Category == category).ToArray();
                Func<TrialMetrics[], string> hits = values => values.Length == 0 ? "—" : values.Count(m => !m.Failed) + "/" + values.Length;
                Func<TrialMetrics[], double?> hitRate = values => values.Length == 0 ? (double?)null : 100.0 * values.Count(m => !m.Failed) / values.Length;
                Func<TrialMetrics[], double?> timeouts = values => values.Length == 0 ? (double?)null : values.Count(m => m.TimedOut);
                Func<TrialMetrics[], double?> moveTime = values => AverageAvailable(values.Where(m => !m.Failed).Select(m => (double?)m.MovementMs));
                Func<TrialMetrics[], double?> reaction = values => AverageAvailable(values.Select(m => m.ReactionMs));
                Func<double?, double?, string> change = (x, y) => FormatMetric(x.HasValue && y.HasValue ? y - x : null);
                metricsTable.Rows.Add(category + " first-shot hits", hits(a), hits(b), change(hitRate(a), hitRate(b)) + (b.Length == 0 ? "" : " pp"));
                metricsTable.Rows.Add(category + " timeouts", FormatMetric(timeouts(a)), FormatMetric(timeouts(b)), change(timeouts(a), timeouts(b)));
                metricsTable.Rows.Add(category + " reaction time (ms)", FormatMetric(reaction(a)), FormatMetric(reaction(b)), change(reaction(a), reaction(b)));
                metricsTable.Rows.Add(category + " movement to hit (ms)", FormatMetric(moveTime(a)), FormatMetric(moveTime(b)), change(moveTime(a), moveTime(b)));
            }
        }
        private void ResetDesktopMetrics()
        {
            metricsTable.Rows.Clear();
            foreach (var name in new[] { "First-pass error (px)", "First movement (ms)", "Corrections", "Correction time (ms)", "Jitter (px)", "Settling (ms)", "Speed (px/ms)", "Successful targets (%)", "Failed targets", "Reaction time (ms)", "Movement to hit (ms)" })
                metricsTable.Rows.Add(name, "—", "—", "—");
            metricsTable.Rows[9].Cells[0].ToolTipText = "Target appearance to detected cursor movement. At least 2 px of movement filters tiny jitter.";
            metricsTable.Rows[10].Cells[0].ToolTipText = "Detected movement start to the first sampled cursor position inside the circle. Hold time and later corrections are excluded.";
        }
        private async Task RunValorantSession()
        {
            valorantSeed = Guid.NewGuid().GetHashCode();
            valorantShotsCompleted = valorantShotsTotal = 0;
            valorantMode.Enabled = valorantSensitivity.Enabled = false;
            using (var deadline = new System.Windows.Forms.Timer { Interval = 360000 })
            {
                deadline.Tick += (sender, args) => { deadline.Stop(); DeadlineHit("The six-minute calibration limit was reached; restoring the original settings."); };
                deadline.Start();
                try
                {
                    BeginValorantViewport();
                    await ValorantBlock(3, 3, "Practice");
                    var baselineTrials = await ValorantBlock(12, 11, "Baseline");
                    searchBaseline = baselineTrials;
                    var generation = await Task.Run(() => {
                        string why;
                        var choices = CalibrationSearch.CreateCandidates(baseline, SelectedProfileIndex, baselineTrials, out why);
                        return Tuple.Create(choices, why);
                    });
                    var candidates = generation.Item1;
                    string reason = generation.Item2;
                    screeningStarted = true;
                    valorantShotsTotal = valorantShotsCompleted + candidates.Count * 5 + (candidates.Count > 0 ? 26 : 0);
                    CalibrationProposal best = null;
                    double bestScore = double.PositiveInfinity;
                    var report = new List<string>();
                    int index = 0;
                    foreach (var candidate in candidates)
                    {
                        index++;
                        if (outOfTime)
                        {
                            report.Add("Time limit reached; remaining candidates were not screened.");
                            break;
                        }
                        var measurements = await ValorantMeasure(candidate.SettingsJson, 100, "Screen " + index + "/" + candidates.Count, 4);
                        double score = ValorantScoring.RankScreen(measurements);
                        RegisterSearchResult(candidate, measurements, "hits " + measurements.Count(m => !m.Failed) + "/" + measurements.Count);
                        report.Add("Screen " + index + ": " + candidate.Description + " — " + measurements.Count(m => !m.Failed) + "/" + measurements.Count + " first-shot hits.");
                        if (score < bestScore) { best = candidate; bestScore = score; }
                    }
                    List<TrialMetrics> before = baselineTrials, after = null;
                    CalibrationComparison confirmation = null;
                    if (best != null)
                    {
                        if ((valorantSeed & 1) == 0)
                        {
                            before = await ValorantMeasure(baseline, 500, "Confirmation 1/2", 12);
                            after = await ValorantMeasure(best.SettingsJson, 500, "Confirmation 2/2", 12, true);
                        }
                        else
                        {
                            after = await ValorantMeasure(best.SettingsJson, 500, "Confirmation 1/2", 12);
                            before = await ValorantMeasure(baseline, 500, "Confirmation 2/2", 12, true);
                        }
                        confirmation = ValorantScoring.Compare(before, after);
                    }
                    bool improved = confirmation != null && confirmation.Improved;
                    await SetTrial(improved ? best.SettingsJson : baseline);
                    if (stopping) throw new OperationCanceledException();
                    deadline.Stop();
                    EndValorantViewport();
                    ShowValorantComparison(before, after);
                    phase.Text = !improved ? "FINISHED — review tested profiles" : confirmation.SelectionScore >= 1 ?
                        "FIRST-SHOT HITS: " + before.Count(m => !m.Failed) + "/" + before.Count + " to " + after.Count(m => !m.Failed) + "/" + after.Count :
                        "FIRST-SHOT MOVEMENT: " + (confirmation.ImprovementFraction * 100).ToString("0.#") + "% faster";
                    phase.ForeColor = !improved ? Color.DarkGoldenrod : Color.ForestGreen;
                    string result = confirmation == null ? reason : confirmation.Reason;
                    if (best != null) result = "Best tested: " + best.Description + ". " + DescribeChanges(baseline, best.SettingsJson) + Environment.NewLine + result;
                    SetSearchReport(result + Environment.NewLine + string.Join(Environment.NewLine, report));
                    if (best != null) SelectSearchResult(best);
                    status.Text = result;
                    if (!improved) feedback.Text = "Apply is disabled because the best tested settings were not confirmed.";
                    else
                    {
                        ShowProposal(best.SettingsJson);
                        SetCandidateReady(true);
                    }
                }
                finally
                {
                    deadline.Stop();
                    EndValorantViewport();
                    valorantMode.Enabled = !candidateReady;
                    valorantSensitivity.Enabled = !candidateReady && valorantMode.Checked;
                }
            }
        }
    }
}
