using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using grapher.Models.Calibration;
using grapher.Models.Mouse;
using grapher.Models.Serialized;
using grapher.Models.Theming;
using grapher.Common;
using RawAccel.ServiceSupport;
using Microsoft.Win32;

namespace grapher
{
    public sealed partial class CalibrationForm : Form
    {
        private sealed class TargetPanel : Panel
        {
            public TargetPanel() { DoubleBuffered = true; }
        }

        private sealed class DeviceChoice
        {
            public string Name, Id;
            public IntPtr Handle;
            public int ProfileIndex;
        }

        private readonly SettingsManager settings;
        private readonly MouseWatcher watcher;
        private readonly Func<DriverConfig> readActive;
        private readonly Func<int, Task> delay;
        private readonly Func<Point> cursorPosition;
        private readonly bool syntheticCursor;
        private readonly Func<bool> focusIsValid;
        private readonly CalibrationTrialClient suppliedClient;
        private readonly List<DeviceChoice> devices = new List<DeviceChoice>();
        private DeviceChoice detectedDevice;
        private readonly Label phase = new Label { Dock = DockStyle.Top, Height = 36, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12, FontStyle.Bold), AccessibleName = "Calibration result and phase" };
        private readonly Label settingsState = new Label { Dock = DockStyle.Top, Height = 42, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), AccessibleName = "Settings application status" };
        private readonly Label feedback = new Label { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Movement feedback" };
        private readonly Label status = new Label { Dock = DockStyle.Top, Height = 65, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Calibration progress" };
        private readonly DataGridView metricsTable = new DataGridView { Dock = DockStyle.Bottom, Height = 254, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ScrollBars = ScrollBars.Vertical, AccessibleName = "Calibration measurements" };
        private readonly Panel target = new TargetPanel { Dock = DockStyle.Fill, BackColor = Color.White, AccessibleName = "Pointing target area" };
        private readonly CheckBox valorantMode = new CheckBox { Text = "Valorant mode", AutoSize = true, AccessibleName = "Enable Valorant calibration" };
        private readonly NumericUpDown valorantSensitivity = new NumericUpDown { DecimalPlaces = 3, Minimum = 0, Maximum = 10, Increment = 0.01M, Value = 0, Width = 75, AccessibleName = "Valorant hip-fire sensitivity" };
        private readonly Label valorantHint = new Label { Text = "Hip-fire sensitivity (keep mouse DPI fixed):", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly Button start = new Button { Text = "&Start", AutoSize = true };
        private readonly Button keep = new Button { Text = "&Keep this setting", AutoSize = true, Visible = false };
        private readonly Button accept = new Button { Text = "&Apply recommended settings", AutoSize = true, Enabled = false, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Padding = new Padding(6, 3, 6, 3) };
        private readonly Button restore = new Button { Text = "&Restore previous settings", AutoSize = true, Visible = false };
        private readonly Button recover = new Button { Text = "&Restore previous settings", AutoSize = true, Visible = false };
        private readonly Button again = new Button { Text = "&Recalibrate", AutoSize = true, Visible = false };
        private readonly Button cancel = new Button { Text = "&Cancel", AutoSize = true };
        private readonly Button leavePending = new Button { Text = "Close; &recovery pending", AutoSize = true, Visible = false };
        private readonly System.Windows.Forms.Timer cursorTimer = new System.Windows.Forms.Timer { Interval = 10 };
        private readonly System.Windows.Forms.Timer renewTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        private readonly SemaphoreSlim serviceGate = new SemaphoreSlim(1, 1);
        private CalibrationCapture capture;
        private CalibrationTrialClient client;
        private TaskCompletionSource<TargetTrial> trialCompletion;
        private string baseline, active, lease, committedLease, proposal;
        private string recoveryLease, recoveryCurrent, recoveryBaseline;
        private bool recoverySavedChanged;
        private bool running, stopping, closing, accepted, closeRequested, busy, candidateReady, keeping;
        private bool renewInFlight;
        private bool beginSent;
        private long insideTicks;
        private double dwellProgress;
        private bool showCompleted;
        private TrialMetrics liveMetrics;
        private readonly List<PointF> trail = new List<PointF>();
        private PointF targetCenter;
        private float targetRadius;
        private Rectangle initialBounds;
        private int[] pointerSettings;
        private Task cancelTask;
        private ServiceRequest pendingRecoveryRequest;
        private PointF[] originalCurve, suggestedCurve;
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, [Out] int[] value, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, ref int value, uint flags);

        public bool SettingsAccepted { get { return accepted; } }
        public string AcceptedSettingsJson { get; private set; }
        public bool OutcomeUncertain { get; private set; }
        public bool RecoveryRestored { get; private set; }
        public string VerifiedRecoverySettingsJson { get; private set; }
        public int SelectedProfileIndex { get; private set; }

        public CalibrationForm(SettingsManager settings, MouseWatcher watcher)
            : this(settings, watcher, DriverConfig.GetActive, null, Task.Delay)
        {
        }

        public CalibrationForm(SettingsManager settings, MouseWatcher watcher, Func<DriverConfig> readActive,
            CalibrationTrialClient preconnectedClient, Func<int, Task> delay, Func<Point> cursorPosition = null, Func<bool> focusIsValid = null)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
            this.readActive = readActive ?? throw new ArgumentNullException(nameof(readActive));
            this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
            syntheticCursor = cursorPosition != null;
            this.cursorPosition = cursorPosition ?? (() => Cursor.Position);
            this.focusIsValid = focusIsValid;
            suppliedClient = preconnectedClient;
            Text = "Mouse calibration — curve search";
            StartPosition = FormStartPosition.CenterParent;
            var work = Screen.FromPoint(this.cursorPosition()).WorkingArea;
            MinimumSize = new Size(Math.Min(650, work.Width), Math.Min(700, work.Height));
            Size = new Size(Math.Min(850, work.Width), Math.Min(750, work.Height));
            KeyPreview = true;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            buttons.Controls.AddRange(new Control[] { valorantMode, valorantHint, valorantSensitivity, start, accept, keep, testedProfiles, previewTested, again, restore, recover, cancel, leavePending });
            target.Controls.Add(searchReport);
            previewTested.Click += async (s, e) => await PreviewSelectedProfile();
            metricsTable.Columns.Add("metric", "Measurement");
            metricsTable.Columns.Add("original", "Current");
            metricsTable.Columns.Add("adjustment", "Adjustment");
            metricsTable.Columns.Add("change", "Change");
            // Live updates and the before/after summary address each measurement by row.
            foreach (DataGridViewColumn column in metricsTable.Columns)
                column.SortMode = DataGridViewColumnSortMode.NotSortable;
            metricsTable.RowTemplate.Height = 20;
            metricsTable.ColumnHeadersHeight = 24;
            ResetDesktopMetrics();
            Controls.Add(target); Controls.Add(status); Controls.Add(feedback); Controls.Add(settingsState); Controls.Add(phase); Controls.Add(metricsTable); Controls.Add(buttons);
            phase.Text = "Ready to calibrate";
            settingsState.Text = "NOT SAVED — finish calibration to enable Apply recommended settings.";
            const string desktopInstructions = "Move your mouse, then press Start. Move in one quick motion, then correct naturally; both are measured. Hold inside each circle briefly. Shared profiles affect other mice; using settings replaces unapplied edits.";
            status.Text = desktopInstructions;
            target.Paint += PaintTarget;
            valorantMode.CheckedChanged += (s, e) =>
            {
                valorantSensitivity.Visible = valorantHint.Visible = valorantMode.Checked;
                valorantSensitivity.Enabled = valorantMode.Checked;
                status.Text = valorantMode.Checked
                    ? "Enter your Valorant hip-fire sensitivity and keep mouse DPI fixed. Aim at each target and click once. Escape cancels. Applying a result updates the selected mouse's profile, including other mice sharing it."
                    : desktopInstructions;
            };
            valorantSensitivity.Visible = valorantHint.Visible = false;
            start.Click += async (s, e) => await RunSession();
            again.Click += async (s, e) => { await CancelTrial(); if (lease == null) await RunSession(); };
            accept.Click += async (s, e) => await AcceptCandidate();
            keep.Click += (s, e) => RequestKeep();
            restore.Click += async (s, e) => await RestoreCommitted();
            recover.Click += async (s, e) => await RecoverPrevious();
            cancel.Click += async (s, e) => await CloseSafely();
            leavePending.Click += (s, e) => { closing = true; Close(); };
            KeyDown += async (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Enter && keep.Enabled)
                {
                    e.Handled = true; e.SuppressKeyPress = true; RequestKeep(); return;
                }
                if (e.KeyCode != Keys.Escape) return;
                e.Handled = true;
                if (valorantViewport && running && !stopping && lease != null && baseline != null && active != null &&
                    SettingsFingerprint.Compute(active) != SettingsFingerprint.Compute(baseline))
                {
                    // Freeze the running trial so the decision dialog cannot score a timeout or a stray click.
                    cursorTimer.Stop();
                    aimTrial = null;
                    if (valorantCursorHidden) { Cursor.Show(); valorantCursorHidden = false; }
                    target.Capture = false;
                    Cursor.Clip = oldClip;
                    if (MessageBox.Show(this, "Keep the current trial settings? Yes saves them now; No restores the original settings.",
                        "Calibration", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        RequestKeep();
                        return;
                    }
                }
                await CloseSafely();
            };
            FormClosing += OnClosing;
            Deactivate += async (s, e) => { if (this.focusIsValid != null && this.focusIsValid()) return; if (running || busy) Interrupt("Calibration stopped because the window lost focus."); else if (lease != null) { SetCandidateReady(false); status.Text = "Window lost focus; restoring original settings."; await CancelTrial(); } };
            Move += (s, e) => { if (!valorantWindowTransition && (running || lease != null) && Bounds != initialBounds) DisplayChanged(s, e); };
            Resize += (s, e) => { if (!valorantWindowTransition && (running || lease != null) && Bounds != initialBounds) DisplayChanged(s, e); };
            DpiChanged += (s, e) => DisplayChanged(s, e);
            cursorTimer.Tick += CursorTick;
            renewTimer.Tick += async (s, e) => await Renew();
            settings.DeviceChange += DeviceChanged;
            watcher.CalibrationSample += OnSample;
            watcher.CalibrationError += OnCaptureError;
            SystemEvents.SessionSwitch += SessionSwitch;
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            Shown += (s, e) =>
            {
                var area = Screen.FromControl(Owner ?? this).WorkingArea;
                Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
            };
            PopulateDevices();
            Theme.Apply(this);
            target.BackColor = SystemColors.Window;
        }

        private void PopulateDevices()
        {
            var selected = detectedDevice;
            string previous = selected == null ? null : selected.Id;
            IntPtr previousHandle = selected == null ? IntPtr.Zero : selected.Handle;
            devices.Clear();
            var config = settings.ActiveConfig;
            foreach (var device in settings.SystemDevices)
            {
                var assignment = config.devices.Find(d => d.id == device.id);
                if (assignment == null ? config.defaultDeviceConfig.disable : assignment.config.disable) continue;
                int profile = 0;
                if (assignment != null && !string.IsNullOrEmpty(assignment.profile))
                {
                    int found = config.profiles.FindIndex(p => p.name == assignment.profile);
                    if (found >= 0) profile = found;
                }
                foreach (var handle in device.handles.Where(h => h != IntPtr.Zero))
                    devices.Add(new DeviceChoice { Id = device.id, Name = (string.IsNullOrWhiteSpace(device.name) ? device.id : device.name) + " — " + config.profiles[profile].name, Handle = handle, ProfileIndex = profile });
            }
            detectedDevice = devices.Find(d => d.Handle == watcher.LastMovingDevice) ??
                devices.Find(d => d.Handle == previousHandle) ?? (previous == null ? null : devices.Find(d => d.Id == previous));
            if (detectedDevice == null && devices.Count == 1) detectedDevice = devices[0];
            UpdateDetectedMouse();
        }

        private void UpdateDetectedMouse()
        {
            start.Enabled = detectedDevice != null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopValorant();
                settings.DeviceChange -= DeviceChanged;
                watcher.CalibrationSample -= OnSample;
                watcher.CalibrationError -= OnCaptureError;
                SystemEvents.SessionSwitch -= SessionSwitch;
                SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                cursorTimer.Dispose(); renewTimer.Dispose();
                if (client != null) client.Dispose();
            }
            base.Dispose(disposing);
        }

        private void PaintTarget(object sender, PaintEventArgs e)
        {
            if (valorantViewport) { PaintValorant(e.Graphics); return; }
            if (trialCompletion == null && !showCompleted)
            {
                if (suggestedCurve != null) PaintProposal(e.Graphics);
                return;
            }
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var trailPen = new Pen(Color.FromArgb(110, SystemColors.Highlight), 2))
                if (trail.Count > 1) e.Graphics.DrawLines(trailPen, trail.ToArray());
            using (var brush = new SolidBrush(showCompleted ? Color.ForestGreen : SystemColors.Highlight))
            using (var pen = new Pen(SystemColors.WindowText, 2))
            using (var centerPen = new Pen(SystemColors.HighlightText, 2))
            using (var progressPen = new Pen(showCompleted ? Color.ForestGreen : SystemColors.WindowText, 4))
            {
                var rectangle = new RectangleF(targetCenter.X - targetRadius, targetCenter.Y - targetRadius, 2 * targetRadius, 2 * targetRadius);
                e.Graphics.FillEllipse(brush, rectangle); e.Graphics.DrawEllipse(pen, rectangle);
                float marker = Math.Max(3, Math.Min(7, targetRadius / 3));
                e.Graphics.DrawLine(centerPen, targetCenter.X - marker, targetCenter.Y, targetCenter.X + marker, targetCenter.Y);
                e.Graphics.DrawLine(centerPen, targetCenter.X, targetCenter.Y - marker, targetCenter.X, targetCenter.Y + marker);
                var ring = RectangleF.Inflate(rectangle, 5, 5);
                if (dwellProgress > 0) e.Graphics.DrawArc(progressPen, ring, -90, (float)(360 * Math.Min(1, dwellProgress)));
                if (liveMetrics != null && liveMetrics.InitialEndX.HasValue && liveMetrics.InitialEndY.HasValue)
                {
                    float x = (float)liveMetrics.InitialEndX.Value, y = (float)liveMetrics.InitialEndY.Value;
                    e.Graphics.DrawEllipse(pen, x - 5, y - 5, 10, 10);
                }
            }
        }

        private void ShowProposal(string json)
        {
            proposal = json;
            var before = DriverConfig.Convert(baseline).Item1;
            var after = DriverConfig.Convert(json).Item1;
            originalCurve = new PointF[60]; suggestedCurve = new PointF[60];
            using (var original = new ManagedAccel(before.profiles[SelectedProfileIndex]))
            using (var suggested = new ManagedAccel(after.profiles[SelectedProfileIndex]))
                for (int i = 0; i < 60; i++)
                {
                    double speed = i + 1;
                    var oldOutput = original.Accelerate(i + 1, 0, 1, 1);
                    var newOutput = suggested.Accelerate(i + 1, 0, 1, 1);
                    double originalOutput = Math.Sqrt(oldOutput.Item1 * oldOutput.Item1 + oldOutput.Item2 * oldOutput.Item2);
                    double suggestedOutput = Math.Sqrt(newOutput.Item1 * newOutput.Item1 + newOutput.Item2 * newOutput.Item2);
                    double relative = originalOutput > 1e-12 ? suggestedOutput / originalOutput : suggestedOutput <= 1e-12 ? 1 : double.NaN;
                    if (double.IsNaN(relative) || double.IsInfinity(relative) || relative < .25 - 1e-8 || relative > 4 + 1e-8)
                        throw new InvalidOperationException("The proposed curve exceeds the allowed change in the preview. Original settings will be retained.");
                    originalCurve[i] = new PointF((float)speed, 1);
                    suggestedCurve[i] = new PointF((float)speed, (float)relative);
                }
            target.Invalidate();
        }
        private void PaintProposal(Graphics graphics)
        {
            var plot = new RectangleF(55, 45, Math.Max(1, target.Width - 85), Math.Max(1, target.Height - 90));
            float maximum = Math.Max(originalCurve.Max(p => p.Y), suggestedCurve.Max(p => p.Y)) * 1.15f;
            Func<PointF[], PointF[]> points = curve => curve.Select(p => new PointF(plot.Left + p.X / 60 * plot.Width,
                plot.Bottom - p.Y / maximum * plot.Height)).ToArray();
            using (var axes = new Pen(SystemColors.WindowText))
            using (var oldPen = new Pen(SystemColors.GrayText, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
            using (var newPen = new Pen(SystemColors.Highlight, 2))
            using (var text = new SolidBrush(SystemColors.WindowText))
            {
                graphics.DrawLine(axes, plot.Left, plot.Top, plot.Left, plot.Bottom);
                graphics.DrawLine(axes, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
                graphics.DrawLines(oldPen, points(originalCurve)); graphics.DrawLines(newPen, points(suggestedCurve));
                graphics.DrawString("Horizontal input: original (dashed), suggested (solid), relative sensitivity", Font, text, plot.Left, 15);
                graphics.DrawString("Input speed  0–60", Font, text, plot.Left, plot.Bottom + 10);
            }
        }

        private async void DeviceChanged(object sender, EventArgs e)
        {
            if (running || busy) { Interrupt("Mouse devices changed during calibration."); return; }
            if (lease != null) { SetCandidateReady(false); status.Text = "Mouse devices changed; restoring original settings."; await CancelTrial(); }
            PopulateDevices();
        }
        private void DisplayChanged(object sender, EventArgs e)
        {
            if (IsHandleCreated) BeginInvoke((Action)(async () => { if (running || busy) Interrupt("Display settings changed."); else if (lease != null) { SetCandidateReady(false); status.Text = "Display settings changed; restoring original settings."; await CancelTrial(); } }));
        }
        private static int[] ReadPointerSettings()
        {
            var values = new int[3]; int speed = 0;
            if (!SystemParametersInfo(3, 0, values, 0) || !SystemParametersInfo(0x70, 0, ref speed, 0))
                throw new InvalidOperationException("Windows pointer settings could not be read.");
            return new[] { values[0], values[1], values[2], speed };
        }
        private void SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock && IsHandleCreated)
                BeginInvoke((Action)(async () => { if (running || busy) Interrupt("Windows session was locked."); else if (lease != null) { SetCandidateReady(false); status.Text = "Windows session was locked; restoring original settings."; await CancelTrial(); } }));
        }
        private void OnSample(MotionSample sample)
        {
            if (running && valorantViewport) { ObserveValorant(sample); return; }
            if (running && capture != null) { capture.Observe(sample); return; }
            if (running || busy || lease != null || OutcomeUncertain || !sample.Relative || sample.Device == IntPtr.Zero ||
                (sample.X == 0 && sample.Y == 0) || (detectedDevice != null && detectedDevice.Handle == sample.Device)) return;
            var moving = devices.Find(d => d.Handle == sample.Device);
            if (moving == null) return;
            detectedDevice = moving;
            UpdateDetectedMouse();
        }
        private void OnCaptureError(string error) { if (running && valorantViewport) Interrupt(error); else if (running && capture != null) capture.Invalidate(error); }
        private void Interrupt(string message)
        {
            interruptReason = message;
            stopping = true;
            StopValorant();
            if (capture != null) capture.Invalidate(message);
            FinishCurrent();
        }
        private void FinishCurrent()
        {
            if (trialCompletion == null) return;
            cursorTimer.Stop();
            var done = trialCompletion; trialCompletion = null;
            var trial = capture.Finish();
            showCompleted = trial != null && trial.ConditionId >= 0 && trial.Completed;
            if (showCompleted) dwellProgress = 1;
            target.Invalidate(); done.TrySetResult(trial);
        }
        private static string FirstMovementFeedback(TrialMetrics metrics)
        {
            if (metrics == null || !metrics.InitialError.HasValue) return "First movement not yet clear";
            bool corrected = metrics.Corrections > 0 || (metrics.CorrectionMs.HasValue && metrics.CorrectionMs.Value > 0);
            if (metrics.PassedTarget || metrics.InitialError.Value > 0)
                return corrected ? "Went past target — correction recorded" : "First movement went past target";
            if (metrics.InitialError.Value < 0)
                return corrected ? "Stopped short — correction recorded" : "First movement stopped short";
            return "On target";
        }
        private void CursorTick(object sender, EventArgs e)
        {
            if (valorantViewport) { TickValorant(); return; }
            if (capture == null || capture.Current == null) return;
            var trial = capture.Current;
            var screenPoint = cursorPosition();
            var pt = target.PointToClient(screenPoint);
            var screen = Screen.FromControl(this).Bounds;
            if (!screen.Contains(screenPoint) || screenPoint.X <= screen.Left || screenPoint.X >= screen.Right - 1 || screenPoint.Y <= screen.Top || screenPoint.Y >= screen.Bottom - 1)
                capture.Invalidate("Cursor reached a display boundary.");
            long now = Stopwatch.GetTimestamp();
            capture.ObserveCursor(new CursorSample { Ticks = now, X = pt.X, Y = pt.Y });
            if (trial.ConditionId >= 0)
            {
                trail.Add(pt);
                if (trail.Count > 256) trail.RemoveAt(0);
            }
            double dx = pt.X - trial.TargetX, dy = pt.Y - trial.TargetY;
            if (dx * dx + dy * dy <= trial.Radius * trial.Radius)
            {
                if (insideTicks == 0) insideTicks = now;
                dwellProgress = Math.Min(1, (now - insideTicks) * 1000.0 / Stopwatch.Frequency / CalibrationDefaults.DwellMs);
                if ((now - insideTicks) * 1000.0 / Stopwatch.Frequency >= CalibrationDefaults.DwellMs) { trial.Completed = true; FinishCurrent(); return; }
            }
            else { insideTicks = 0; dwellProgress = 0; }
            target.Invalidate();
            if (trial.InvalidReason != null || (now - trial.PresentedTicks) * 1000.0 / Stopwatch.Frequency >= CalibrationDefaults.TimeoutMs) FinishCurrent();
        }

        private async Task<TargetTrial> RunTarget(int condition, string fingerprint)
        {
            var size = target.ClientSize;
            if (size.Width < 300 || size.Height < 180) throw new InvalidOperationException("The target area is too small.");
            var pt = target.PointToClient(cursorPosition());
            float radius = condition < 0 ? 8 : Math.Max(16, Math.Min(size.Width, size.Height) * (.03f + (condition / 8) * .005f));
            var position = PlaceDesktopTarget(size, pt, condition, radius);
            float x = position.X, y = position.Y;
            if (x < radius + 12 || x > size.Width - radius - 12 || y < radius + 12 || y > size.Height - radius - 12)
                throw new InvalidOperationException("Target does not fit on this display. Enlarge the target area and try again.");
            targetCenter = new PointF(x, y); targetRadius = radius; insideTicks = 0; dwellProgress = 0;
            showCompleted = false; trail.Clear(); liveMetrics = null;
            var trial = new TargetTrial { ConditionId = condition, Configuration = fingerprint, TargetX = x, TargetY = y, Radius = radius, TimeoutMs = 3000 };
            trialCompletion = new TaskCompletionSource<TargetTrial>();
            target.Refresh();
            pt = target.PointToClient(cursorPosition());
            trial.StartX = pt.X; trial.StartY = pt.Y; trial.PresentedTicks = Stopwatch.GetTimestamp();
            capture.Begin(trial);
            capture.ObserveCursor(new CursorSample { Ticks = trial.PresentedTicks, X = pt.X, Y = pt.Y });
            var task = trialCompletion.Task; cursorTimer.Start();
            return await task;
        }

        private static PointF PlaceDesktopTarget(Size size, Point start, int condition, float radius)
        {
            if (condition < 0) return new PointF(size.Width / 2f, size.Height / 2f);
            double angle = (condition % 8) * Math.PI / 4;
            double dx = Math.Cos(angle), dy = Math.Sin(angle), inset = radius + 20;
            double availableX = Math.Abs(dx) < 1e-8 ? double.PositiveInfinity :
                (dx > 0 ? size.Width - inset - start.X : start.X - inset) / Math.Abs(dx);
            double availableY = Math.Abs(dy) < 1e-8 ? double.PositiveInfinity :
                (dy > 0 ? size.Height - inset - start.Y : start.Y - inset) / Math.Abs(dy);
            double distance = Math.Max(0, Math.Min(availableX, availableY)) *
                (condition / 8 == 0 ? .30 : condition / 8 == 1 ? .60 : .92);
            return new PointF((float)(start.X + dx * distance), (float)(start.Y + dy * distance));
        }

        private async Task<List<TrialMetrics>> Block(int count, string label, int seed)
        {
            string expected = active;
            if (lease != null) await Send("trial-query");
            if (SettingsFingerprint.Compute(readActive().ToJSON()) != SettingsFingerprint.Compute(expected))
                throw new InvalidOperationException("Active settings changed before measurement.");
            var result = new List<TrialMetrics>();
            var random = new Random(seed);
            // Compare cardinal directions, then confirm on fresh diagonal directions.
            var order = Enumerable.Range(0, count).Select(i => count == 12 || count == 6 ?
                (i % 3) * 8 + (i / 3) * 2 + (seed == 500 ? 1 : 0) :
                count == 3 ? i * 8 + seed % 8 : i % 24).OrderBy(i => random.Next()).ToArray();
            for (int i = 0; i < order.Length; i++)
            {
                if (stopping) throw new OperationCanceledException();
                if (!pointerSettings.SequenceEqual(ReadPointerSettings())) throw new InvalidOperationException("Windows pointer settings changed during calibration.");
                string phaseName = label.StartsWith("Practice") ? "Practice" : label == "Baseline" ? "Baseline" :
                    label.StartsWith("Refinement") ? "Refining adjustment" : label.StartsWith("Confirmation") ? "Confirmation" : label;
                phase.Text = phaseName + " — " + (i + 1) + "/" + count + "   Overall " + searchCompleted + "/" + (searchTotal > 0 ? searchTotal.ToString() : "calculating");
                TargetTrial trial = null;
                for (int retry = 0; retry < 3; retry++)
                {
                    status.Text = label + " — target " + (i + 1) + " of " + count + ". First hold on the small center marker.";
                    feedback.Text = "Return to center";
                    var center = await RunTarget(-1, SettingsFingerprint.Compute(active));
                    if (stopping) throw new OperationCanceledException();
                    if (center.InvalidReason != null || !center.Completed) continue;
                    status.Text = label + " — target " + (i + 1) + " of " + count + ". Move quickly, then hold inside the circle.";
                    feedback.Text = "Move to the target, then correct naturally";
                    trial = await RunTarget(order[i], SettingsFingerprint.Compute(active));
                    if (stopping) throw new OperationCanceledException();
                    if (trial.InvalidReason == null) break;
                }
                if (trial == null || trial.InvalidReason != null) throw new InvalidOperationException("Too many interrupted measurements; keep the window focused and return to the center marker.");
                // Analyze a completed, detached capture away from the input/UI thread.
                var metrics = await Task.Run(() => CalibrationAnalysis.Analyze(trial));
                if (stopping) throw new OperationCanceledException();
                result.Add(metrics);
                searchCompleted++;
                liveMetrics = metrics;
                feedback.Text = metrics.Failed ? "Target not reached" : FirstMovementFeedback(metrics);
                ShowLiveMetrics(metrics, true);
                target.Refresh(); feedback.Refresh();
                await delay(150);
                showCompleted = false;
                target.Invalidate();
            }
            if (lease != null) await Send("trial-query");
            if (SettingsFingerprint.Compute(readActive().ToJSON()) != SettingsFingerprint.Compute(expected))
                throw new InvalidOperationException("Active settings changed during measurement.");
            return result;
        }

        private async Task<ServiceResponse> Send(string command, string json = null)
        {
            await serviceGate.WaitAsync();
            try
            {
                var request = new ServiceRequest { Command = command, Settings = json, LeaseId = lease, RequestId = Guid.NewGuid().ToString("N"), ExpectedFingerprint = active == null ? null : SettingsFingerprint.Compute(active) };
                ServiceResponse response;
                try { response = await client.SendAsync(request); }
                catch (ApplicationException) { response = await client.SendAsync(request); }
                if (!response.Success)
                {
                    if (command == "trial-begin")
                    {
                        beginSent = false;
                        try { await QueryRecovery(); }
                        catch (Exception error) { RecoveryPending("The host rejected the trial and recovery could not be checked: " + error.Message); }
                    }
                    throw new InvalidOperationException(response.Message ?? "Calibration service request failed.");
                }
                if (!string.IsNullOrEmpty(response.LeaseId)) lease = response.LeaseId;
                if (!string.IsNullOrEmpty(response.ActiveSettings)) active = response.ActiveSettings;
                return response;
            }
            finally { serviceGate.Release(); }
        }
        private async Task Renew()
        {
            if (lease == null || stopping || busy || renewInFlight || closing) return;
            renewInFlight = true;
            try { await Send("trial-renew"); }
            catch (Exception ex) { if (running) Interrupt("Trial renewal failed: " + ex.Message); else { SetCandidateReady(false); status.Text = "Trial renewal failed: " + ex.Message; await CancelTrial(); } }
            finally { renewInFlight = false; }
        }
        private async Task SetTrial(string json)
        {
            if (stopping) throw new OperationCanceledException();
            if (SettingsFingerprint.Compute(active) != SettingsFingerprint.Compute(json))
            {
                settingsState.Text = "SWITCHING TRIAL SETTINGS — not saved.";
                keep.Enabled = false;
                await Send("trial-update", json);
                ShowTrialState();
                UpdateKeepButton();
                await delay(1200);
            }
            else ShowTrialState();
            UpdateKeepButton();
        }
        private void ShowTrialState()
        {
            bool original = SettingsFingerprint.Compute(active) == SettingsFingerprint.Compute(baseline);
            settingsState.Text = original ? "ORIGINAL SETTINGS ACTIVE — saved settings unchanged." :
                "TRIAL SETTINGS ACTIVE — not saved. Keep this setting (Ctrl+Enter) saves it now and ends calibration; or continue testing.";
            settingsState.ForeColor = ForeColor;
        }
        private void UpdateKeepButton()
        {
            keep.Visible = running && !closing;
            keep.Enabled = running && !stopping && !keeping && lease != null && baseline != null && active != null &&
                SettingsFingerprint.Compute(active) != SettingsFingerprint.Compute(baseline);
        }
        // Stops the running session without discarding the live trial settings; RunSession's
        // finally block then saves them through CommitCandidate.
        private void RequestKeep()
        {
            if (!running || stopping || busy || lease == null) return;
            keeping = true;
            stopping = true;
            interruptReason = "Keeping the current trial settings.";
            if (valorantViewport) StopValorant(); else FinishCurrent();
        }
        private void SetCandidateReady(bool ready)
        {
            bool wasReady = candidateReady;
            candidateReady = ready;
            valorantMode.Enabled = !ready && !running && testedProfiles.Items.Count == 0;
            valorantSensitivity.Enabled = valorantMode.Enabled && valorantMode.Checked;
            accept.Enabled = ready && !busy && !OutcomeUncertain;
            accept.Text = accepted && !OutcomeUncertain ? "Applied && saved" : manualSelection ? "&Apply selected profile" : "&Apply recommended settings";
            AcceptButton = accept.Enabled ? accept : null;
            if (ready)
            {
                settingsState.Text = manualSelection ? "MANUAL PREVIEW ACTIVE — Apply selected profile to save your choice." :
                    "RECOMMENDATION READY — not saved. Click Apply recommended settings below, then Done.";
                feedback.Text = manualSelection ? "This is your manual choice. Apply saves it. Done or Cancel restores the original until saved." :
                    "Next: click Apply recommended settings to apply now and save for startup.";
            }
            else if (wasReady && !accepted)
                feedback.Text = "This recommendation is no longer available to save. Recalibrate to try again.";
        }
        private void ShowVerdict(CalibrationComparison comparison)
        {
            if (comparison == null || !comparison.Improved)
            {
                phase.Text = "NO CONFIRMED IMPROVEMENT";
                phase.ForeColor = BackColor.GetBrightness() < .5 ? Color.Khaki : Color.DarkGoldenrod;
                return;
            }
            phase.Text = "BETTER — " + comparison.Benefit + " (this test)";
            phase.ForeColor = BackColor.GetBrightness() < .5 ? Color.LightGreen : Color.ForestGreen;
        }
        private static double? AverageAvailable(IEnumerable<double?> values)
        {
            var valid = values.Where(v => v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value)).Select(v => v.Value).ToArray();
            return valid.Length == 0 ? (double?)null : valid.Average();
        }
        private static string FormatMetric(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) ? value.Value.ToString("0.##") : "—";
        }
        private static double?[] Summarize(IList<TrialMetrics> trials)
        {
            if (trials == null || trials.Count == 0) return new double?[11];
            var usable = trials.Where(m => m != null && !m.Invalid).ToArray();
            if (usable.Length == 0) return new double?[11];
            return new double?[] {
                AverageAvailable(usable.Select(m => m.InitialError.HasValue ? (double?)Math.Abs(m.InitialError.Value) : null)),
                AverageAvailable(usable.Select(m => m.InitialMovementMs)),
                usable.Average(m => m.Corrections),
                AverageAvailable(usable.Select(m => m.CorrectionMs)),
                AverageAvailable(usable.Select(m => m.Jitter)),
                AverageAvailable(usable.Select(m => m.SettlingMs)),
                usable.Average(m => m.MeanSpeed),
                100.0 * usable.Count(m => !m.Failed) / usable.Length,
                usable.Count(m => m.Failed),
                AverageAvailable(usable.Select(m => m.ReactionMs)),
                AverageAvailable(usable.Select(m => m.TimeToHitMs))
            };
        }
        private void ShowLiveMetrics(TrialMetrics metrics, bool completed = false)
        {
            if (metrics == null || metrics.Invalid) return;
            metricsTable.Rows[0].Cells[0].Value = "First error +/- (px)";
            metricsTable.Columns[1].HeaderText = completed ? "Last target" : "Live";
            metricsTable.Columns[2].HeaderText = "Adjustment";
            metricsTable.Columns[3].HeaderText = "Change";
            double?[] values = { metrics.InitialError, metrics.InitialMovementMs, metrics.Corrections, metrics.CorrectionMs,
                metrics.Jitter, metrics.SettlingMs, metrics.MeanSpeed, completed ? (metrics.Failed ? 0 : 100) : (double?)null,
                completed ? (metrics.Failed ? 1 : 0) : (double?)null, metrics.ReactionMs, metrics.TimeToHitMs };
            for (int i = 0; i < values.Length; i++)
            {
                metricsTable.Rows[i].Cells[1].Value = FormatMetric(values[i]);
                metricsTable.Rows[i].Cells[2].Value = metricsTable.Rows[i].Cells[3].Value = "—";
            }
        }
        private void ShowComparison(IList<TrialMetrics> original, IList<TrialMetrics> adjusted)
        {
            metricsTable.Rows[0].Cells[0].Value = "Mean first error (px)";
            metricsTable.Columns[1].HeaderText = "Before";
            metricsTable.Columns[2].HeaderText = "After trial";
            metricsTable.Columns[3].HeaderText = "Change";
            var before = Summarize(original); var after = Summarize(adjusted);
            for (int i = 0; i < before.Length; i++)
            {
                metricsTable.Rows[i].Cells[1].Value = FormatMetric(before[i]);
                metricsTable.Rows[i].Cells[2].Value = FormatMetric(after[i]);
                metricsTable.Rows[i].Cells[3].Value = FormatMetric(before[i].HasValue && after[i].HasValue ? after[i] - before[i] : null);
            }
        }
        private static string MetricSummary(IList<TrialMetrics> metrics)
        {
            var undershoots = metrics.Where(m => m.Undershoot.HasValue).Select(m => m.Undershoot.Value).ToArray();
            var jitter = metrics.Where(m => m.Jitter.HasValue).Select(m => m.Jitter.Value).ToArray();
            var settling = metrics.Where(m => m.SettlingMs.HasValue).Select(m => m.SettlingMs.Value).ToArray();
            return "overshoot " + metrics.Average(m => m.Overshoot).ToString("0.0") +
                ", undershoot " + (undershoots.Length == 0 ? "unavailable" : undershoots.Average().ToString("0.0")) +
                ", corrections " + metrics.Average(m => m.Corrections).ToString("0.0") +
                ", jitter " + (jitter.Length == 0 ? "unavailable" : jitter.Average().ToString("0.0")) +
                " units; settling " + (settling.Length == 0 ? "unavailable" : settling.Average().ToString("0")) +
                " ms; speed " + metrics.Average(m => m.MeanSpeed).ToString("0.00") + " units/ms";
        }

        private async Task RunSession()
        {
            if (running || busy || lease != null || OutcomeUncertain) return;
            if (valorantMode.Checked && valorantSensitivity.Value <= 0) { status.Text = "Enter your Valorant hip-fire sensitivity before starting, and keep mouse DPI fixed."; valorantSensitivity.Focus(); return; }
            PopulateDevices();
            if (detectedDevice == null) return;
            // Give pointing trials the available desktop area; show the measurements at review.
            if (!valorantMode.Checked && !syntheticCursor) WindowState = FormWindowState.Maximized;
            metricsTable.Visible = false;
            PerformLayout();
            running = true; stopping = false; beginSent = false; initialBounds = Bounds;
            keeping = false; outOfTime = false; screeningStarted = false;
            interruptReason = null;
            manualSelection = false;
            testedProfiles.Items.Clear();
            testedProfiles.Enabled = previewTested.Enabled = true;
            testedProfiles.Visible = previewTested.Visible = searchReport.Visible = false;
            phase.Text = "MEASURING — no result yet"; phase.ForeColor = ForeColor;
            settingsState.Text = "CHECKING SETTINGS — no new settings saved.";
            start.Enabled = false; SetCandidateReady(false); again.Visible = false;
            UpdateKeepButton();
            proposal = null;
            originalCurve = suggestedCurve = null; target.Invalidate();
            try
            {
                client = client ?? suppliedClient ?? await CalibrationTrialClient.ConnectAsync(Constants.DefaultSettingsFileName);
                ServiceResponse info;
                try { info = await Send("status"); }
                catch (ApplicationException) when (suppliedClient == null)
                {
                    client.Dispose();
                    client = await CalibrationTrialClient.ConnectAsync(Constants.DefaultSettingsFileName);
                    info = await Send("status");
                }
                if (!info.Success || info.TrialProtocolVersion < 1) throw new InvalidOperationException("The calibration trial host is incompatible.");
                if (!info.SettingsApplied)
                {
                    await QueryRecovery();
                    return;
                }
                if (stopping) throw new OperationCanceledException();
                var currentConfig = readActive();
                if (currentConfig.profiles.Count == 0) throw new InvalidOperationException("No active profile is available.");
                baseline = currentConfig.ToJSON(); active = baseline;
                var roundTrip = DriverConfig.Convert(baseline);
                if (roundTrip.Item1 == null || roundTrip.Item2 != null || roundTrip.Item1.profiles.Count != currentConfig.profiles.Count ||
                    currentConfig.profiles.Where((p, i) => p.minimumSpeed != roundTrip.Item1.profiles[i].minimumSpeed).Any())
                    throw new InvalidOperationException("The current settings serializer cannot preserve every profile. No settings were changed.");
                pointerSettings = ReadPointerSettings();
                if (SettingsFingerprint.Compute(baseline) != SettingsFingerprint.Compute(settings.ActiveConfig.ToJSON()))
                {
                    // The standalone authority may have just recovered an interrupted session.
                    settings.ReloadAfterCalibration(baseline, false);
                    RecoveryRestored = true; VerifiedRecoverySettingsJson = baseline;
                }
                PopulateDevices();
                if (detectedDevice == null) throw new InvalidOperationException("The detected mouse is no longer available.");
                SelectedProfileIndex = detectedDevice.ProfileIndex;
                capture = new CalibrationCapture(detectedDevice.Handle);
                if (SettingsFingerprint.Compute(active) != SettingsFingerprint.Compute(baseline))
                    throw new InvalidOperationException("Active settings changed before calibration began.");
                if (stopping) throw new OperationCanceledException();
                beginSent = true;
                await Send("trial-begin", baseline); ShowTrialState(); renewTimer.Start();
                if (valorantMode.Checked) { await RunValorantSession(); return; }
                await RunDesktopSearch();
            }
            catch (OperationCanceledException)
            {
                phase.Text = "STOPPED — " + testedProfiles.Items.Count + " profiles completed";
                phase.ForeColor = ForeColor;
                status.Text = interruptReason ?? "Calibration stopped; restoring the original settings.";
                if (testedProfiles.Items.Count > 0)
                    SetSearchReport(status.Text + Environment.NewLine + "Completed screening results" + Environment.NewLine +
                        string.Join(Environment.NewLine, testedProfiles.Items.Cast<SearchResult>().Select(r => r.ToString())));
            }
            catch (Exception ex)
            {
                stopping = true;
                phase.Text = "INCOMPLETE — no confirmed result"; phase.ForeColor = ForeColor;
                settingsState.Text = "SETTINGS STATUS NOT VERIFIED — checking restoration.";
                status.Text = "Calibration stopped: " + ex.Message;
                if (lease == null && beginSent)
                    RecoveryPending("The trial may have started, but its response was lost. Lease expiry/recovery is pending; restoration was not verified.");
            }
            finally
            {
                running = false; cursorTimer.Stop(); EndValorantViewport();
                metricsTable.Visible = true;
                UpdateKeepButton();
                valorantMode.Enabled = !candidateReady && testedProfiles.Items.Count == 0;
                valorantSensitivity.Enabled = valorantMode.Enabled && valorantMode.Checked;
                if (keeping && lease != null && !candidateReady && !accepted)
                {
                    phase.Text = "KEEPING CURRENT SETTINGS";
                    phase.ForeColor = ForeColor;
                    await CommitCandidate(active);
                }
                else if (lease != null && !candidateReady && (stopping || testedProfiles.Items.Count == 0)) await CancelTrial();
                if (lease != null && !candidateReady && !stopping && testedProfiles.Items.Count > 0)
                    settingsState.Text = "FINISHED — original settings active. Preview a tested profile, or Done to finish.";
                if (!stopping && !OutcomeUncertain) cancel.Text = "&Done";
                again.Visible = !OutcomeUncertain; start.Visible = false;
                if (closeRequested) await CloseSafely();
            }
        }
        private Task CancelTrial()
        {
            if (cancelTask == null || cancelTask.IsCompleted) cancelTask = CancelTrialCore();
            return cancelTask;
        }
        private async Task CancelTrialCore()
        {
            renewTimer.Stop();
            if (lease == null) return;
            try
            {
                settingsState.Text = "RESTORING ORIGINAL SETTINGS…";
                await Send("trial-cancel"); lease = null; OutcomeUncertain = false; leavePending.Visible = false;
                settingsState.Text = "SETTINGS NOT UPDATED — original settings restored; saved settings unchanged.";
                settingsState.ForeColor = ForeColor;
                SetCandidateReady(false);
            }
            catch (Exception ex) { RecoveryPending("Restore could not be confirmed: " + ex.Message); }
        }
        private void RecoveryPending(string message)
        {
            OutcomeUncertain = true; leavePending.Visible = true; SetCandidateReady(false);
            settingsState.Text = "SETTINGS STATUS UNKNOWN — restoration must be verified.";
            settingsState.ForeColor = BackColor.GetBrightness() < .5 ? Color.LightSalmon : Color.Firebrick;
            if (client != null) recover.Visible = true;
            status.Text = message + " Closing will not confirm restoration. Wait for recovery and verify the active settings before another change.";
        }
        private async Task QueryRecovery()
        {
            var result = await client.SendAsync(new ServiceRequest { Command = "trial-recovery-query", RequestId = Guid.NewGuid().ToString("N") });
            if (!result.Success) throw new InvalidOperationException(result.Message ?? "Could not inspect recovery state.");
            if (result.TrialState == "idle" && baseline != null && result.ActiveSettings != null &&
                SettingsFingerprint.Compute(result.ActiveSettings) == SettingsFingerprint.Compute(baseline))
            {
                lease = null; OutcomeUncertain = false; recover.Visible = leavePending.Visible = false;
                RecoveryRestored = true; VerifiedRecoverySettingsJson = result.ActiveSettings;
                settingsState.Text = "ORIGINAL SETTINGS ACTIVE — restoration verified; saved settings unchanged.";
                settingsState.ForeColor = ForeColor;
                status.Text = "No recovery remains pending. The active settings match the original baseline.";
                return;
            }
            if (result.TrialState != "recovery-pending" || string.IsNullOrEmpty(result.LeaseId) || string.IsNullOrEmpty(result.BaselineSettings) || string.IsNullOrEmpty(result.ActiveSettings))
            {
                RecoveryPending("The host is not ready and no pending recovery was returned. Verify driver availability and active settings, then restart calibration.");
                recover.Visible = false;
                return;
            }
            recoveryLease = result.LeaseId; recoveryCurrent = result.ActiveSettings; recoveryBaseline = result.BaselineSettings;
            recoverySavedChanged = result.RecoverySavedChanged;
            OutcomeUncertain = true; leavePending.Visible = true; recover.Visible = true;
            settingsState.Text = "RECOVERY PENDING — original settings have not been restored.";
            status.Text = "Recovery is pending because current settings changed. Restore previous settings will replace the current active settings with the pre-calibration baseline. The startup file remains unchanged." +
                (recoverySavedChanged ? " The startup file also changed during calibration." : "");
        }
        private async Task RecoverPrevious()
        {
            if (client == null || busy) return;
            busy = true; recover.Enabled = false;
            try
            {
                if (recoveryLease == null && pendingRecoveryRequest == null) { await QueryRecovery(); return; }
                string warning = "Restore the pre-calibration live settings? This will replace the current active settings. The startup settings file will not be changed." +
                    (recoverySavedChanged ? " The startup file changed during calibration." : "");
                if (pendingRecoveryRequest == null && MessageBox.Show(this, warning, "Restore previous settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                var request = pendingRecoveryRequest ?? new ServiceRequest { Command = "trial-recover", LeaseId = recoveryLease, ExpectedFingerprint = SettingsFingerprint.Compute(recoveryCurrent), RequestId = Guid.NewGuid().ToString("N") };
                pendingRecoveryRequest = request;
                ServiceResponse result;
                try { result = await client.SendAsync(request); }
                catch (ApplicationException) { result = await client.SendAsync(request); }
                if (!result.Success || string.IsNullOrEmpty(result.ActiveSettings) || SettingsFingerprint.Compute(result.ActiveSettings) != SettingsFingerprint.Compute(recoveryBaseline))
                    throw new InvalidOperationException(result.Message ?? "Restoration could not be verified.");
                RecoveryRestored = true; VerifiedRecoverySettingsJson = result.ActiveSettings;
                OutcomeUncertain = false; recover.Visible = leavePending.Visible = false;
                recoveryLease = null; pendingRecoveryRequest = null;
                lease = committedLease = null; accepted = false; AcceptedSettingsJson = null; renewTimer.Stop();
                SetCandidateReady(false);
                settingsState.Text = "ORIGINAL SETTINGS RESTORED — saved settings unchanged.";
                settingsState.ForeColor = ForeColor;
                status.Text = "Previous live settings restored. Startup settings were not changed. Select Done.";
                cancel.Text = "&Done";
            }
            catch (Exception ex) { recoveryLease = null; RecoveryPending("Recovery could not be confirmed: " + ex.Message); }
            finally { busy = false; recover.Enabled = true; }
        }
        private async Task AcceptCandidate()
        {
            if (busy || running || !candidateReady || proposal == null || lease == null) return;
            busy = true;
            accept.Enabled = false;
            try
            {
                if (!pointerSettings.SequenceEqual(ReadPointerSettings())) throw new InvalidOperationException("Windows pointer settings changed after the comparison.");
                settingsState.Text = "APPLYING & SAVING — waiting for verification…";
                committedLease = lease;
                var result = await Send("trial-commit");
                if (result.TrialState != "committed" || string.IsNullOrEmpty(result.ActiveSettings) ||
                    SettingsFingerprint.Compute(result.ActiveSettings) != SettingsFingerprint.Compute(proposal))
                    throw new InvalidOperationException("The service did not verify the saved candidate settings.");
                lease = null; renewTimer.Stop(); accepted = true; AcceptedSettingsJson = result.ActiveSettings;
                ShowSavedState();
                status.Text = "Settings saved. Restore previous settings is available until Done; restoring also saves the original live settings for startup.";
                SetCandidateReady(false); again.Visible = false; restore.Visible = true; cancel.Text = "&Done";
            }
            catch (Exception ex)
            {
                try
                {
                    var state = await Send("trial-query");
                    if (state.TrialState == "committed" && !string.IsNullOrEmpty(state.ActiveSettings) &&
                        SettingsFingerprint.Compute(state.ActiveSettings) == SettingsFingerprint.Compute(proposal))
                    {
                        lease = null; renewTimer.Stop(); accepted = true; AcceptedSettingsJson = state.ActiveSettings;
                        ShowSavedState();
                        SetCandidateReady(false); again.Visible = false; restore.Visible = true; cancel.Text = "&Done";
                        status.Text = "Settings were saved. Restore previous settings is available until Done; restoring saves the original live settings for startup.";
                    }
                    else { status.Text = "Could not save settings: " + ex.Message; await CancelTrial(); }
                }
                catch (Exception queryError) { RecoveryPending("Commit outcome is unknown: " + queryError.Message); renewTimer.Stop(); }
            }
            finally { busy = false; accept.Enabled = candidateReady && !accepted && !OutcomeUncertain; AcceptButton = accept.Enabled ? accept : null; }
            if (stopping && accepted) await RestoreCommitted();
            if (closeRequested && !accepted) await CloseSafely();
        }
        private async Task CommitCandidate(string expectedJson)
        {
            if (busy || lease == null || accepted) return;
            busy = true;
            accept.Enabled = false;
            keep.Enabled = false;
            try
            {
                if (!pointerSettings.SequenceEqual(ReadPointerSettings())) throw new InvalidOperationException("Windows pointer settings changed before saving.");
                settingsState.Text = "APPLYING & SAVING — waiting for verification…";
                committedLease = lease;
                var result = await Send("trial-commit");
                if (result.TrialState != "committed" || string.IsNullOrEmpty(result.ActiveSettings) ||
                    SettingsFingerprint.Compute(result.ActiveSettings) != SettingsFingerprint.Compute(expectedJson))
                    throw new InvalidOperationException("The service did not verify the saved candidate settings.");
                FinishCommit(result.ActiveSettings);
                status.Text = "Settings saved. Restore previous settings is available until Done; restoring also saves the original live settings for startup.";
            }
            catch (Exception ex)
            {
                try
                {
                    var state = await Send("trial-query");
                    if (state.TrialState == "committed" && !string.IsNullOrEmpty(state.ActiveSettings) &&
                        SettingsFingerprint.Compute(state.ActiveSettings) == SettingsFingerprint.Compute(expectedJson))
                    {
                        FinishCommit(state.ActiveSettings);
                        status.Text = "Settings were saved. Restore previous settings is available until Done; restoring saves the original live settings for startup.";
                    }
                    else { status.Text = "Could not save settings: " + ex.Message; await CancelTrial(); }
                }
                catch (Exception queryError) { RecoveryPending("Commit outcome is unknown: " + queryError.Message); renewTimer.Stop(); }
            }
            finally { busy = false; accept.Enabled = candidateReady && !accepted && !OutcomeUncertain; AcceptButton = accept.Enabled ? accept : null; }
        }
        private void FinishCommit(string savedJson)
        {
            lease = null; renewTimer.Stop(); accepted = true; AcceptedSettingsJson = savedJson;
            ShowSavedState();
            SetCandidateReady(false); again.Visible = false; restore.Visible = true; cancel.Text = "&Done";
        }
        private void ShowSavedState()
        {
            testedProfiles.Enabled = previewTested.Enabled = false;
            SetCandidateReady(false);
            settingsState.Text = "SETTINGS UPDATED — active now and saved for startup. Select Done to keep them.";
            settingsState.ForeColor = BackColor.GetBrightness() < .5 ? Color.LightGreen : Color.ForestGreen;
            feedback.Text = "New settings are active. Done keeps them; Restore previous settings undoes the change.";
        }
        private async Task RestoreCommitted()
        {
            if (busy || !accepted || committedLease == null) return;
            busy = true; restore.Enabled = false;
            try
            {
                settingsState.Text = "RESTORING PREVIOUS SETTINGS — waiting for verification…";
                lease = committedLease;
                var response = await Send("trial-restore");
                if (string.IsNullOrEmpty(response.ActiveSettings) || SettingsFingerprint.Compute(response.ActiveSettings) != SettingsFingerprint.Compute(baseline))
                    throw new InvalidOperationException("The restored live settings could not be verified.");
                lease = null; committedLease = null; accepted = false; AcceptedSettingsJson = null;
                testedProfiles.Enabled = previewTested.Enabled = true;
                SetCandidateReady(false);
                settingsState.Text = "PREVIOUS SETTINGS RESTORED — active now and saved for startup.";
                settingsState.ForeColor = ForeColor;
                feedback.Text = "Your original settings are active again. The verdict above describes the tested adjustment.";
                restore.Visible = false; status.Text = "Previous live settings were restored and saved for startup.";
            }
            catch (Exception ex) { lease = null; RecoveryPending("Restore could not be confirmed: " + ex.Message); }
            finally { busy = false; restore.Enabled = true; }
        }
        private async Task CloseSafely()
        {
            if (busy) { closeRequested = true; stopping = true; return; }
            if (running) { closeRequested = true; Interrupt("Calibration cancelled."); return; }
            await CancelTrial();
            if (lease != null || OutcomeUncertain) return;
            closing = true; Close();
        }
        private async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (closing) return;
            e.Cancel = true;
            await CloseSafely();
        }
    }
}
