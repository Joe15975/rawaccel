using System;
using System.Collections.Generic;

namespace grapher.Models.Calibration
{
    // Ticks are Stopwatch timestamps; cursor coordinates are target-panel client units.
    public struct MotionSample
    {
        public long Ticks;
        public IntPtr Device;
        public int X, Y;
        public ushort Flags, Buttons;
        public bool Relative;
    }

    public struct CursorSample
    {
        public long Ticks;
        public double X, Y;
    }

    public sealed class TargetTrial
    {
        public int ConditionId;
        public string Configuration;
        public double StartX, StartY, TargetX, TargetY, Radius;
        public long PresentedTicks;
        public double TimeoutMs = 5000;
        public bool Completed;
        public string InvalidReason;
        public readonly List<CursorSample> Cursor = new List<CursorSample>();
        public readonly List<MotionSample> Motion = new List<MotionSample>();
    }

    public sealed class TrialMetrics
    {
        public int ConditionId;
        public string Category;
        public bool Invalid, Failed, TimedOut;
        public string InvalidReason;
        public double Distance, Radius, MovementMs, AcquisitionMs;
        // Appearance to detected movement, then movement to first circle entry (no dwell).
        public double? ReactionMs, TimeToHitMs;
        public double Overshoot, CrossTrackError, Corrections, PathEfficiency;
        public double MinimumError, TerminalError, MeanSpeed, PeakSpeed, RawMeanSpeed;
        public double? Undershoot, Jitter, SettlingMs;
        // Signed distance outside the target circle on the first approach; null when ambiguous.
        public double? InitialError, InitialMovementMs, CorrectionMs, InitialEndX, InitialEndY;
        public bool PassedTarget;
        public int FirstMovementEndIndex = -1;
        public bool StableWindowComplete;
    }

    public sealed class CalibrationProposal
    {
        public string SettingsJson, Description;
        public double Cap, Offset;
    }

    public sealed class CalibrationComparison
    {
        public bool Improved;
        public string Reason;
        public double ImprovementFraction, BaselineMedianMs, CandidateMedianMs;
        // Null means the paired measure was unavailable or its baseline median was zero.
        public double? FirstMovementImprovementFraction, CorrectionImprovementFraction;
        public double SelectionScore;
        public string Benefit;
        public int Pairs, Wins, BaselineFailures, CandidateFailures;
    }

    public static class CalibrationDefaults
    {
        public const double DwellMs = 150;
        public const double MaximumCursorGapMs = 80;
        public const double TimeoutMs = 5000;
        public const int MaximumMotionSamples = 50000;
        public const int MinimumComparisonPairs = 12;
        public const double MinimumImprovement = 0.05;
        // Native speed is counts/ms, normalized by 1000/configured DPI when enabled.
        public const double CandidateOffset = 5;
        public const double CandidateReferenceSpeed = 30;
        public static readonly double[] CandidateCaps = { 1.08, 1.12 };
    }
}


