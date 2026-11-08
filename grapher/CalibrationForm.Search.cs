using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using grapher.Models.Calibration;
using Newtonsoft.Json.Linq;
using RawAccel.ServiceSupport;

namespace grapher
{
    public sealed partial class CalibrationForm
    {
        private sealed class SearchResult
        {
            public CalibrationProposal Proposal;
            public string Summary;
            public IList<TrialMetrics> Metrics;
            public override string ToString() { return Proposal.Description + " — " + Summary; }
        }

        private readonly TextBox searchReport = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Both, WordWrap = false, Visible = false, AccessibleName = "Tested profiles and setting changes" };
        private readonly ComboBox testedProfiles = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 245, Visible = false,
            AccessibleName = "Completed profile trials" };
        private readonly Button previewTested = new Button { Text = "Preview selected profile", AutoSize = true, Visible = false };
        private bool manualSelection;
        private bool outOfTime, screeningStarted;
        private string interruptReason;
        private int searchCompleted, searchTotal;
        private IList<TrialMetrics> searchBaseline;

        private void RegisterSearchResult(CalibrationProposal candidate, IList<TrialMetrics> measurements, string summary)
        {
            if (measurements == null || measurements.Count == 0 || measurements.Any(m => m.Invalid)) return;
            testedProfiles.Items.Add(new SearchResult { Proposal = candidate, Summary = summary, Metrics = measurements });
        }

        private void SetSearchReport(string report)
        {
            searchReport.Text = report;
            searchReport.Visible = true;
            searchReport.BringToFront();
            testedProfiles.Visible = previewTested.Visible = testedProfiles.Items.Count > 0;
            if (testedProfiles.Items.Count > 0 && testedProfiles.SelectedIndex < 0) testedProfiles.SelectedIndex = 0;
        }

        private void SelectSearchResult(CalibrationProposal candidate)
        {
            foreach (SearchResult result in testedProfiles.Items)
                if (result.Proposal.SettingsJson == candidate.SettingsJson)
                {
                    testedProfiles.SelectedItem = result;
                    break;
                }
        }

        private string DescribeChanges(string originalJson, string candidateJson)
        {
            var original = (JObject)JObject.Parse(originalJson)["profiles"][SelectedProfileIndex].DeepClone();
            var candidate = (JObject)JObject.Parse(candidateJson)["profiles"][SelectedProfileIndex].DeepClone();
            var changes = new List<string>();
            foreach (var value in candidate.Descendants().OfType<JValue>())
            {
                var previous = original.SelectToken(value.Path, false);
                if (JToken.DeepEquals(previous, value)) continue;
                changes.Add(value.Path + "    " + (previous?.ToString(Newtonsoft.Json.Formatting.None) ?? "unset") +
                    " → " + value.ToString(Newtonsoft.Json.Formatting.None));
            }
            return string.Join(Environment.NewLine, changes);
        }

        private async Task PreviewSelectedProfile()
        {
            if (running || busy || accepted || OutcomeUncertain) return;
            var selected = testedProfiles.SelectedItem as SearchResult;
            if (selected == null) return;
            stopping = false;
            interruptReason = null;
            busy = true;
            previewTested.Enabled = false;
            try
            {
                if (lease == null)
                {
                    if (SettingsFingerprint.Compute(readActive().ToJSON()) != SettingsFingerprint.Compute(baseline))
                        throw new InvalidOperationException("The active profile changed. Recalibrate before previewing these results.");
                    await Send("trial-begin", baseline);
                    renewTimer.Start();
                }
                await SetTrial(selected.Proposal.SettingsJson);
                if (stopping) throw new OperationCanceledException();
                ShowProposal(selected.Proposal.SettingsJson);
                manualSelection = true;
                SetCandidateReady(true);
                phase.Text = "MANUAL CHOICE — " + selected.Proposal.Description;
                if (valorantMode.Checked) ShowValorantComparison(searchBaseline, selected.Metrics);
                else ShowComparison(searchBaseline, selected.Metrics);
                metricsTable.Columns[1].HeaderText = "Original baseline";
                metricsTable.Columns[2].HeaderText = "Selected screening";
                status.Text = "Separate screening samples. " + selected.Summary;
                SetSearchReport(selected.Proposal.Description + Environment.NewLine + DescribeChanges(baseline, selected.Proposal.SettingsJson));
            }
            catch (Exception error) { status.Text = error.Message; SetCandidateReady(false); await CancelTrial(); }
            finally
            {
                busy = false;
                previewTested.Enabled = true;
                accept.Enabled = candidateReady && !OutcomeUncertain;
                if (closeRequested) await CloseSafely();
            }
        }

        private static double DesktopScreenRank(IList<TrialMetrics> metrics)
        {
            if (metrics.Count != 4 || metrics.Any(m => m.Invalid)) return double.PositiveInfinity;
            return metrics.Count(m => m.Failed) * 1000000.0 + metrics.Average(m =>
                Math.Min(m.AcquisitionMs, CalibrationDefaults.TimeoutMs) +
                (m.InitialError.HasValue ? Math.Min(5, Math.Abs(m.InitialError.Value) / m.Radius) * 150 : 750));
        }

        // Soft deadline: once screening has begun, running out of time skips the remaining
        // candidates and still confirms the best screened so far; before that it aborts as before.
        private void DeadlineHit(string message)
        {
            if (screeningStarted && !stopping)
            {
                outOfTime = true;
                status.Text = message + " Confirming the best screened profile.";
            }
            else Interrupt(message);
        }
        private async Task RunDesktopSearch()
        {
            using (var deadline = new System.Windows.Forms.Timer { Interval = 360000 })
            {
                deadline.Tick += (s, e) => { deadline.Stop(); DeadlineHit("The six-minute calibration limit was reached."); };
                deadline.Start();
                ResetDesktopMetrics();
                searchCompleted = 0;
                searchTotal = 0;
                await Block(3, "Practice", 3);
                var measurements = await Block(12, "Baseline", 11);
                searchBaseline = measurements;
                var generation = await Task.Run(() => {
                    string why;
                    var choices = CalibrationSearch.CreateCandidates(baseline, SelectedProfileIndex, measurements, out why);
                    return Tuple.Create(choices, why);
                });
                var candidates = generation.Item1;
                string reason = generation.Item2;
                screeningStarted = true;
                searchTotal = searchCompleted + candidates.Count * 5 + (candidates.Count > 0 ? 26 : 0);
                CalibrationProposal best = null;
                double bestScore = double.PositiveInfinity;
                var report = new List<string>();
                int index = 0;
                foreach (var candidate in candidates)
                {
                    if (stopping) throw new OperationCanceledException();
                    if (outOfTime)
                    {
                        report.Add("Time limit reached; remaining candidates were not screened.");
                        break;
                    }
                    await SetTrial(candidate.SettingsJson);
                    await Block(1, "Practice profile " + (++index) + "/" + candidates.Count, 1001);
                    var trials = await Block(4, "Screen " + index + "/" + candidates.Count, 100);
                    double score = DesktopScreenRank(trials);
                    string summary = trials.Count(m => !m.Failed) + "/4 targets; " + trials.Average(m => m.AcquisitionMs).ToString("0") + " ms average";
                    RegisterSearchResult(candidate, trials, summary);
                    report.Add(candidate.Description + " — " + summary);
                    if (score < bestScore) { best = candidate; bestScore = score; }
                }
                var before = measurements;
                List<TrialMetrics> after = null;
                CalibrationComparison confirmation = null;
                if (best != null)
                {
                    if (DateTime.UtcNow.Ticks % 2 == 0)
                    {
                        before = await MeasureSearchFinal(baseline, "Confirmation 1/2");
                        after = await MeasureSearchFinal(best.SettingsJson, "Confirmation 2/2");
                    }
                    else
                    {
                        after = await MeasureSearchFinal(best.SettingsJson, "Confirmation 1/2");
                        before = await MeasureSearchFinal(baseline, "Confirmation 2/2");
                    }
                    confirmation = CalibrationAnalysis.Compare(before, after);
                }
                bool improved = confirmation != null && confirmation.Improved;
                if (stopping) throw new OperationCanceledException();
                await SetTrial(improved ? best.SettingsJson : baseline);
                if (stopping) throw new OperationCanceledException();
                deadline.Stop();
                ShowComparison(before, after);
                ShowVerdict(confirmation);
                if (!improved) phase.Text = "FINISHED — review tested profiles";
                string result = confirmation?.Reason ?? reason;
                if (best != null) result = "Best screened profile — " + best.Description + Environment.NewLine +
                    DescribeChanges(baseline, best.SettingsJson) + Environment.NewLine + result;
                status.Text = "FINISHED — " + searchCompleted + " movements measured. Review the tested profiles below.";
                SetSearchReport(result + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, report));
                if (best != null) SelectSearchResult(best);
                if (improved)
                {
                    ShowProposal(best.SettingsJson);
                    SetCandidateReady(true);
                }
                else feedback.Text = "Finished. Review the results or preview a tested profile from the list.";
            }
        }

        private async Task<List<TrialMetrics>> MeasureSearchFinal(string json, string label)
        {
            if (stopping) throw new OperationCanceledException();
            await SetTrial(json);
            await Block(1, "Practice confirmation", 1500);
            return await Block(12, label, 500);
        }
    }
}
