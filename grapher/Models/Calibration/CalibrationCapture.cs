using System;
using System.Diagnostics;

namespace grapher.Models.Calibration
{
    public sealed class CalibrationCapture
    {
        private readonly IntPtr selectedDevice;
        public TargetTrial Current { get; private set; }

        public CalibrationCapture(IntPtr selectedDevice) { this.selectedDevice = selectedDevice; }
        public void Begin(TargetTrial trial)
        {
            if (Current != null) throw new InvalidOperationException("A trial is already active.");
            if (trial == null) throw new ArgumentNullException("trial");
            Current = trial;
            if (selectedDevice == IntPtr.Zero) Invalidate("No mouse selected.");
        }
        public void Observe(MotionSample sample)
        {
            if (Current == null) return;
            if (sample.Device == IntPtr.Zero) { Invalidate("Unknown input device."); return; }
            if (sample.Device != selectedDevice)
            {
                if (sample.X != 0 || sample.Y != 0) Invalidate("Another input device moved.");
                return;
            }
            if (!sample.Relative) { Invalidate("Absolute mouse input is unsupported."); return; }
            if (Current.Motion.Count >= CalibrationDefaults.MaximumMotionSamples) { Invalidate("Mouse input buffer is full."); return; }
            if (Current.Motion.Count > 0 && sample.Ticks < Current.Motion[Current.Motion.Count - 1].Ticks) { Invalidate("Mouse timestamps are out of order."); return; }
            Current.Motion.Add(sample);
        }
        public void ObserveCursor(CursorSample sample)
        {
            if (Current == null) return;
            if (double.IsNaN(sample.X) || double.IsInfinity(sample.X) || double.IsNaN(sample.Y) || double.IsInfinity(sample.Y)) { Invalidate("Invalid cursor position."); return; }
            if (Current.Cursor.Count >= CalibrationDefaults.MaximumMotionSamples) { Invalidate("Cursor buffer is full."); return; }
            if (Current.Cursor.Count > 0)
            {
                long delta = sample.Ticks - Current.Cursor[Current.Cursor.Count - 1].Ticks;
                if (delta <= 0 || delta * 1000.0 / Stopwatch.Frequency > CalibrationDefaults.MaximumCursorGapMs) { Invalidate("Cursor sampling was interrupted."); return; }
            }
            Current.Cursor.Add(sample);
        }
        public void Invalidate(string reason)
        {
            if (Current != null && Current.InvalidReason == null) Current.InvalidReason = reason ?? "Capture interrupted.";
        }
        public TargetTrial Finish()
        {
            var trial = Current;
            Current = null;
            return trial;
        }
    }
}
