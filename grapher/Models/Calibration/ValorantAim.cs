using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;

namespace grapher.Models.Calibration
{
    public sealed class ValorantAimCondition
    {
        public int Id;
        public string Category;
        public double Yaw, Pitch, Radius;
    }

    public static class ValorantAim
    {
        public const double HorizontalFov = 103.0;
        public const double DegreesPerCount = 0.07;
        private const double Rad = Math.PI / 180.0;

        // Counts differ by at most one per category; the seed makes both runs reproducible.
        public static List<ValorantAimCondition> CreateConditions(int count, int seed)
        {
            if (count < 0) throw new ArgumentOutOfRangeException("count");
            var random = new Random(seed);
            var result = new List<ValorantAimCondition>();
            string[] categories = { "Micro", "Medium", "Wide" };
            double[] distances = { 3, 12, 25 };
            double[] radii = { 0.6, 1.0, 1.5 };
            // Yaw caps stop targets from spawning far out on the horizontal axis; the wider
            // direction spread moves the variety into pitch instead.
            double[] maximumYaw = { 3.6, 12.0, 18.0 };
            const double directionSpread = 1.4; // Total width in radians (±40°) of the direction jitter.
            for (int i = 0; i < count; i++)
            {
                int category = i % 3;
                // Alternate sides within each category, varying the vertical component.
                double angle = ((i / 3) % 2 == 0 ? 0 : Math.PI) + (random.NextDouble() - 0.5) * directionSpread;
                double distance = distances[category] * (0.8 + random.NextDouble() * 0.4);
                double cap = maximumYaw[category];
                result.Add(new ValorantAimCondition { Id = i, Category = categories[category],
                    Yaw = Math.Max(-cap, Math.Min(cap, distance * Math.Cos(angle))), Pitch = distance * Math.Sin(angle), Radius = radii[(i / 3) % radii.Length] });
            }
            for (int i = result.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                var temp = result[i]; result[i] = result[j]; result[j] = temp;
            }
            return result;
        }

        // The vertical field of view follows from the 16:9 horizontal-FOV reference.
        public static PointF Project(double yaw, double pitch, int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("width");
            double focal = width / (2.0 * Math.Tan(HorizontalFov * Rad / 2));
            return new PointF((float)(width / 2.0 + focal * Math.Tan(yaw * Rad)),
                (float)(height / 2.0 - focal * Math.Tan(pitch * Rad) / Math.Cos(yaw * Rad)));
        }

        public static PointF ProjectTarget(double targetYaw, double targetPitch, double viewYaw, double viewPitch, int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("width");
            double ty = targetYaw * Rad, tp = targetPitch * Rad, vy = viewYaw * Rad, vp = viewPitch * Rad;
            double x = Math.Cos(tp) * Math.Sin(ty), y = Math.Sin(tp), z = Math.Cos(tp) * Math.Cos(ty);
            double right = Math.Cos(vy) * x - Math.Sin(vy) * z;
            double up = -Math.Sin(vp) * Math.Sin(vy) * x + Math.Cos(vp) * y - Math.Sin(vp) * Math.Cos(vy) * z;
            double forward = Math.Cos(vp) * Math.Sin(vy) * x + Math.Sin(vp) * y + Math.Cos(vp) * Math.Cos(vy) * z;
            if (forward <= 0) return new PointF(float.NaN, float.NaN);
            double focal = width / (2.0 * Math.Tan(HorizontalFov * Rad / 2));
            return new PointF((float)(width / 2.0 + focal * right / forward), (float)(height / 2.0 - focal * up / forward));
        }
        // Signed distance beyond the target along the original great-circle approach.
        // A lateral miss has no meaningful over/undershoot and returns null.
        public static double? ShotBias(double yaw, double pitch, double targetYaw, double targetPitch, double radius)
        {
            double error = AngularError(yaw, pitch, targetYaw, targetPitch);
            if (error <= radius + 1e-9) return 0;
            double ty = targetYaw * Rad, tp = targetPitch * Rad;
            double tx = Math.Cos(tp) * Math.Sin(ty), tyv = Math.Sin(tp), tz = Math.Cos(tp) * Math.Cos(ty);
            double length = Math.Sqrt(tx * tx + tyv * tyv);
            if (length < 1e-12) return null;
            double ux = tx / length, uy = tyv / length;
            double sy = yaw * Rad, sp = pitch * Rad;
            double sx = Math.Cos(sp) * Math.Sin(sy), sv = Math.Sin(sp), sz = Math.Cos(sp) * Math.Cos(sy);
            double cross = Math.Asin(Math.Max(-1, Math.Min(1, -uy * sx + ux * sv))) / Rad;
            if (Math.Abs(cross) > radius) return null;
            double along = Math.Atan2(ux * sx + uy * sv, sz) / Rad;
            double targetDistance = Math.Atan2(length, tz) / Rad;
            // Circle intersection at this cross-track offset on the sphere.
            double ratio = Math.Cos(radius * Rad) / Math.Cos(cross * Rad);
            double extent = Math.Acos(Math.Max(-1, Math.Min(1, ratio))) / Rad;
            double delta = along - targetDistance;
            if (delta > extent) return delta - extent;
            if (delta < -extent) return delta + extent;
            return null;
        }
        public static double AngularError(double yaw, double pitch, double targetYaw, double targetPitch)
        {
            double p1 = pitch * Rad, p2 = targetPitch * Rad;
            double dot = Math.Sin(p1) * Math.Sin(p2) + Math.Cos(p1) * Math.Cos(p2) * Math.Cos((yaw - targetYaw) * Rad);
            return Math.Acos(Math.Max(-1, Math.Min(1, dot))) / Rad;
        }
    }

    public sealed class ValorantAimTrial
    {
        private readonly ValorantAimCondition condition;
        private readonly double sensitivity, timeoutMs;
        private readonly long presentedTicks;
        private long lastTicks, onsetTicks;
        private bool hasOnset;
        private double minimumError;
        public double Yaw { get; private set; }
        public double Pitch { get; private set; }
        public bool Finished { get; private set; }
        public TrialMetrics Metrics { get; private set; }

        public ValorantAimTrial(ValorantAimCondition condition, double sensitivity, long presentedTicks, double timeoutMs)
        {
            if (condition == null) throw new ArgumentNullException("condition");
            if (double.IsNaN(sensitivity) || double.IsInfinity(sensitivity) || sensitivity <= 0) throw new ArgumentOutOfRangeException("sensitivity");
            if (double.IsNaN(timeoutMs) || double.IsInfinity(timeoutMs) || timeoutMs <= 0) throw new ArgumentOutOfRangeException("timeoutMs");
            this.condition = condition; this.sensitivity = sensitivity; this.presentedTicks = presentedTicks; this.timeoutMs = timeoutMs;
            lastTicks = presentedTicks;
            minimumError = ValorantAim.AngularError(0, 0, condition.Yaw, condition.Pitch);
            Metrics = new TrialMetrics { ConditionId = condition.Id, Category = condition.Category, Distance = minimumError, Radius = condition.Radius };
        }

        private double Elapsed(long ticks) { return (ticks - presentedTicks) * 1000.0 / Stopwatch.Frequency; }
        public void Observe(MotionSample sample)
        {
            if (Finished) return;
            if (sample.Ticks < lastTicks || !sample.Relative)
            {
                Metrics.Invalid = true; Metrics.InvalidReason = sample.Relative ? "Mouse timestamps are out of order." : "Absolute mouse input is unsupported.";
                Finished = true; return;
            }
            if (Elapsed(sample.Ticks) >= timeoutMs) { Timeout(sample.Ticks); return; }
            lastTicks = sample.Ticks;
            if (sample.X != 0 || sample.Y != 0)
            {
                if (!hasOnset) { onsetTicks = sample.Ticks; hasOnset = true; }
                Yaw += sample.X * sensitivity * ValorantAim.DegreesPerCount;
                Pitch = Math.Max(-89, Math.Min(89, Pitch - sample.Y * sensitivity * ValorantAim.DegreesPerCount));
                minimumError = Math.Min(minimumError, ValorantAim.AngularError(Yaw, Pitch, condition.Yaw, condition.Pitch));
            }
            if ((sample.Buttons & 1) != 0) Shoot(sample.Ticks);
        }
        public void Shoot(long ticks)
        {
            if (Finished) return;
            if (ticks < lastTicks) { Metrics.Invalid = true; Metrics.InvalidReason = "Mouse timestamps are out of order."; Finished = true; return; }
            if (Elapsed(ticks) >= timeoutMs) { Timeout(ticks); return; }
            Finish(ticks, false);
        }
        public void Timeout(long ticks)
        {
            if (Finished || Elapsed(ticks) < timeoutMs) return;
            Finish(presentedTicks + (long)(timeoutMs * Stopwatch.Frequency / 1000.0), true);
        }
        private void Finish(long ticks, bool timedOut)
        {
            Finished = true;
            Metrics.TimedOut = timedOut;
            Metrics.AcquisitionMs = Elapsed(ticks);
            Metrics.TerminalError = ValorantAim.AngularError(Yaw, Pitch, condition.Yaw, condition.Pitch);
            Metrics.MinimumError = minimumError;
            Metrics.Failed = timedOut || Metrics.TerminalError > condition.Radius + 1e-9;
            if (!timedOut) Metrics.InitialError = ValorantAim.ShotBias(Yaw, Pitch, condition.Yaw, condition.Pitch, condition.Radius);
            if (hasOnset)
            {
                Metrics.ReactionMs = Elapsed(onsetTicks);
                Metrics.MovementMs = (ticks - onsetTicks) * 1000.0 / Stopwatch.Frequency;
            }
        }
    }
}



