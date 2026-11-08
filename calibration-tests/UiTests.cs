using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using grapher;
using grapher.Models.Mouse;
using grapher.Models.Serialized;
using grapher.Models.Theming;
using grapher.Models.Calibration;

public static class UiTests
{
    private static FieldInfo Field(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static MultiHandleDevice Mouse(string id, int handle)
    {
        var device = (MultiHandleDevice)FormatterServices.GetUninitializedObject(typeof(MultiHandleDevice));
        device.id = id; device.name = id; device.handles = new List<IntPtr> { new IntPtr(handle) };
        return device;
    }
    private static void Move(MouseWatcher watcher, int handle, bool relative = true, int x = 1)
    {
        var packet = new MouseWatcher.RawInput();
        packet.Header.Type = MouseWatcher.RawInputType.Mouse;
        packet.Header.Device = new IntPtr(handle);
        packet.Data.Mouse.LastX = x;
        packet.Data.Mouse.Flags = relative ? 0 : MouseWatcher.RawMouseFlags.MoveAbsolute;
        typeof(MouseWatcher).GetMethod("ProcessMouseInput", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(watcher, new object[] { packet, System.Diagnostics.Stopwatch.GetTimestamp() });
    }
    private static object Call(object target, string method, params object[] args)
    {
        return target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Invoke(target, args);
    }
    private static void Render(Form form, string path)
    {
        form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); form.PerformLayout();
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path); }
    }
    public static void CheckResources()
    {
        var assembly = typeof(RawAcceleration).Assembly;
        foreach (var name in new[] { "grapher.RawAcceleration", "grapher.AboutBox", "grapher.Properties.Resources" })
        {
            Check(assembly.GetManifestResourceNames().Contains(name + ".resources"), "Missing GUI manifest resource: " + name + ".resources");
            var manager = new System.Resources.ResourceManager(name, assembly);
            try
            {
                Check(manager.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true) != null,
                    "GUI resource set could not be loaded: " + name);
                if (name == "grapher.RawAcceleration")
                    Check(manager.GetObject("$this.Icon") is Icon, "Main window icon must load through ResourceManager.");
                if (name == "grapher.AboutBox")
                    Check(manager.GetObject("logoPictureBox.Image") is Image, "About window image must load through ResourceManager.");
            }
            finally { manager.ReleaseAllResources(); }
        }
    }
    // Only constructor/event state is exercised; no driver read/write or service request is made.
    public static void Run(string imagePath = null)
    {
        CheckResources();
        var place = typeof(CalibrationForm).GetMethod("PlaceDesktopTarget", BindingFlags.Static | BindingFlags.NonPublic);
        var area = new Size(1800, 800);
        var center = new Point(900, 400);
        var far = (PointF)place.Invoke(null, new object[] { area, center, 16, 20f });
        Check(far.X > 1650 && Math.Abs(far.Y - 400) < 1, "Long horizontal targets must use the wide free space.");
        for (int condition = 0; condition < 24; condition++)
        {
            var point = (PointF)place.Invoke(null, new object[] { area, center, condition, 20f });
            Check(point.X >= 32 && point.X <= area.Width - 32 && point.Y >= 32 && point.Y <= area.Height - 32,
                "Targets must remain inside the usable area in every direction.");
        }
        var settings = (SettingsManager)FormatterServices.GetUninitializedObject(typeof(SettingsManager));
        Field(settings, "<SystemDevices>k__BackingField").SetValue(settings, new List<MultiHandleDevice>());
        Field(settings, "<ActiveNormTaggedHandles>k__BackingField").SetValue(settings, new List<ValueTuple<IntPtr, bool>>());
        string original = DriverConfig.GetDefault().ToJSON();
        settings.ReloadAfterCalibration(original, true);
        Check(!ReferenceEquals(settings.ActiveConfig, settings.UserConfig), "Accepted reload must not alias editable and active configurations.");
        settings.UserProfile.snap = 10;
        Check(settings.ActiveProfile.snap == 0, "Editing reloaded settings must not change the active snapshot.");
        var multi = DriverConfig.GetDefault();
        multi.profiles.Add(new Profile { name = "Second" });
        settings.ReloadAfterCalibration(multi.ToJSON(), true);
        settings.SelectProfileForEditing(1);
        settings.UserProfile.snap = 12;
        Check(settings.EditingProfileIndex == 1 && settings.UserConfig.profiles[1].snap == 12 &&
            settings.UserConfig.profiles[0].snap == 0 && settings.ActiveProfile.snap == 0,
            "Manual edits must target the calibrated profile without changing another profile or the active snapshot.");
        var watcher = (MouseWatcher)FormatterServices.GetUninitializedObject(typeof(MouseWatcher));
        Theme.CurrentScheme = ColorScheme.LightTheme;
        using (var form = new CalibrationForm(settings, watcher))
        {
            Check(!((Button)Field(form, "start").GetValue(form)).Enabled, "No mouse must disable Start.");
            Check(!((Button)Field(form, "accept").GetValue(form)).Enabled &&
                ((Button)Field(form, "accept").GetValue(form)).Text.Contains("Apply recommended settings"),
                "Apply must be discoverable but disabled until there is a confirmed recommendation.");
            Field(form, "busy").SetValue(form, true);
            typeof(CalibrationForm).GetMethod("DeviceChanged", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Invoke(form, new object[] { null, EventArgs.Empty });
            Check((bool)Field(form, "stopping").GetValue(form), "A device change during commit must request interruption.");
            var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
            typeof(CalibrationForm).GetMethod("OnClosing", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Invoke(form, new object[] { null, closing });
            Check(closing.Cancel && (bool)Field(form, "closeRequested").GetValue(form), "Closing must wait for a pending commit.");
            Field(form, "busy").SetValue(form, false);
            Field(form, "stopping").SetValue(form, false);
            if (imagePath != null)
            {
                form.ShowInTaskbar = false; form.Opacity = 0;
                form.Show(); form.PerformLayout();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(imagePath);
                }
            }
        }
        Check(Field(watcher, "CalibrationSample").GetValue(watcher) == null, "Disposing the dialog must detach its raw-input listener.");

        watcher.PauseCharts = true;
        Field(settings, "<SystemDevices>k__BackingField").SetValue(settings, new List<MultiHandleDevice> { Mouse("first", 1) });
        using (var form = new CalibrationForm(settings, watcher))
        {
            Check(((Button)Field(form, "start").GetValue(form)).Enabled, "An existing single mouse must be detected without selecting it.");
            Check(!form.Controls.Cast<Control>().Any(c => c is ComboBox), "Calibration must not require a mouse dropdown.");
            var second = Mouse("second", 2);
            settings.SystemDevices.Add(second);
            typeof(CalibrationForm).GetMethod("PopulateDevices", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
            Move(watcher, 2);
            Check(watcher.LastMovingDevice == new IntPtr(2), "Raw movement must identify the physical mouse even while charts are paused.");
            var detected = Field(form, "detectedDevice").GetValue(form);
            Check((IntPtr)detected.GetType().GetField("Handle").GetValue(detected) == new IntPtr(2), "Calibration must automatically use the moving mouse.");
            Check(!form.Controls.OfType<Label>().Any(label => label.Text.Contains("HID") || label.Text.Contains("Mouse detected")), "Device identifiers must remain internal.");
            Move(watcher, 1, x: 0);
            Move(watcher, 1, relative: false);
            Check(watcher.LastMovingDevice == new IntPtr(2), "Button-only or absolute input must not replace the detected mouse.");
        }
        using (var form = new CalibrationForm(settings, watcher))
        {
            var detected = Field(form, "detectedDevice").GetValue(form);
            Check((IntPtr)detected.GetType().GetField("Handle").GetValue(detected) == new IntPtr(2), "Opening calibration must reuse the mouse that opened it.");
            var before = new List<TrialMetrics>(); var after = new List<TrialMetrics>();
            for (int i = 0; i < 12; i++)
            {
                before.Add(new TrialMetrics { InitialError = i % 2 == 0 ? 10 : -10, InitialMovementMs = 300, Corrections = 2, CorrectionMs = 200, Jitter = 2, SettlingMs = 550, MeanSpeed = .5, ReactionMs = 200, TimeToHitMs = 500 });
                after.Add(new TrialMetrics { InitialError = i % 2 == 0 ? 5 : -5, InitialMovementMs = 250, Corrections = 1, CorrectionMs = 100, Jitter = 1, SettlingMs = 400, MeanSpeed = .6, ReactionMs = 180, TimeToHitMs = 350 });
            }
            Call(form, "ShowComparison", before, after);
            var table = (DataGridView)Field(form, "metricsTable").GetValue(form);
            Check(Convert.ToDouble(table.Rows[0].Cells[1].Value) == 10 && Convert.ToDouble(table.Rows[0].Cells[2].Value) == 5 &&
                Convert.ToDouble(table.Rows[0].Cells[3].Value) == -5, "Opposite first-stop errors must not cancel each other in the before/after table.");
            Check(Convert.ToDouble(table.Rows[3].Cells[1].Value) == 200 && Convert.ToDouble(table.Rows[3].Cells[2].Value) == 100,
                "Before/after correction times must match measured samples.");
            Check(Convert.ToDouble(table.Rows[9].Cells[1].Value) == 200 && Convert.ToDouble(table.Rows[9].Cells[2].Value) == 180 &&
                Convert.ToDouble(table.Rows[9].Cells[3].Value) == -20 && Convert.ToDouble(table.Rows[10].Cells[3].Value) == -150,
                "Before/after reaction and movement-to-hit measurements must show their actual changes.");
            var verdict = (Label)Field(form, "phase").GetValue(form);
            string catalogReason;
            string defaultProfile = DriverConfig.GetDefault().ToJSON();
            var natural = CalibrationSearch.CreateCandidates(defaultProfile, 0, null, out catalogReason).First(p => p.Description == "Natural");
            string changedSettings = (string)Call(form, "DescribeChanges", defaultProfile, natural.SettingsJson);
            Check(changedSettings.Contains("1.1") && changedSettings.Contains("→"),
                "The results must show nested parameter changes, including the actual curve limit.");
            Check(table.Columns.Cast<DataGridViewColumn>().All(c => c.SortMode == DataGridViewColumnSortMode.NotSortable),
                "Reordering fixed metric rows can associate values with the wrong label.");
            Call(form, "ShowVerdict", new CalibrationComparison { Improved = true, Benefit = "18% faster target acquisition" });
            Check(verdict.Text.Contains("18% faster target acquisition"), "Headline must use measured time improvement, not an invented score.");
            Call(form, "ShowVerdict", new CalibrationComparison { Improved = true, Benefit = "50% less first-movement error" });
            Check(verdict.Text.Contains("50% less first-movement error") && !verdict.Text.Contains("faster"), "An accuracy improvement must not be described as faster acquisition.");
            Call(form, "ShowVerdict", new CalibrationComparison { Improved = true, Benefit = "25% less correction time" });
            Check(verdict.Text.Contains("25% less correction time"), "Correction-time improvement must be labeled with its measured metric.");
            Call(form, "ShowVerdict", new CalibrationComparison { Improved = false, ImprovementFraction = .4 });
            Check(verdict.Text == "NO CONFIRMED IMPROVEMENT", "A fast but rejected comparison must not receive a positive verdict.");
            if (imagePath != null)
            {
                Field(form, "baseline").SetValue(form, settings.ActiveConfig.ToJSON());
                string reason;
                var candidates = CalibrationOptimizer.CreateCandidates(settings.ActiveConfig.ToJSON(), 0, out reason);
                Call(form, "ShowProposal", candidates[0].SettingsJson);
                Field(form, "active").SetValue(form, candidates[0].SettingsJson);
                Call(form, "ShowTrialState");
                Call(form, "ShowVerdict", new CalibrationComparison { Improved = true, Benefit = "50% less first-movement error" });
                ((Button)Field(form, "start").GetValue(form)).Visible = false;
                Call(form, "SetCandidateReady", true);
                ((Label)Field(form, "status").GetValue(form)).Text = "Example measurements for layout review. Before/after values are averages; changes are after minus before.";
                Render(form, Path.Combine(Path.GetDirectoryName(imagePath), Path.GetFileNameWithoutExtension(imagePath) + "-results.png"));
                Field(form, "accepted").SetValue(form, true);
                Call(form, "ShowSavedState");
                Check(!((Button)Field(form, "accept").GetValue(form)).Enabled &&
                    ((Button)Field(form, "accept").GetValue(form)).Text == "Applied && saved", "Saved button must confirm the completed action.");
                ((Button)Field(form, "restore").GetValue(form)).Visible = true;
                ((Button)Field(form, "cancel").GetValue(form)).Text = "Done";
                Render(form, Path.Combine(Path.GetDirectoryName(imagePath), Path.GetFileNameWithoutExtension(imagePath) + "-saved.png"));
            }
            var live = new TrialMetrics { InitialError = 10, InitialMovementMs = 250, Corrections = 1, CorrectionMs = 100,
                InitialEndX = 280, InitialEndY = 140, PassedTarget = true, MeanSpeed = .6, ReactionMs = 180, TimeToHitMs = 350 };
            Call(form, "ShowLiveMetrics", live, false);
            Check(table.Columns[1].HeaderText == "Live" && Convert.ToDouble(table.Rows[0].Cells[1].Value) == 10 &&
                table.Rows[4].Cells[1].Value.ToString() == "—", "Live numbers must show measured error and leave unknown jitter unavailable.");
            Check(Convert.ToDouble(table.Rows[9].Cells[1].Value) == 180 && Convert.ToDouble(table.Rows[10].Cells[1].Value) == 350,
                "Reaction and movement-to-hit numbers must update during calibration.");
            if (imagePath != null)
            {
                ((Label)Field(form, "phase").GetValue(form)).Text = "Baseline — 8 / 24";
                Field(form, "accepted").SetValue(form, false);
                Call(form, "SetCandidateReady", false);
                Field(form, "active").SetValue(form, settings.ActiveConfig.ToJSON());
                Call(form, "ShowTrialState");
                ((Button)Field(form, "restore").GetValue(form)).Visible = false;
                ((Label)Field(form, "feedback").GetValue(form)).Text = "Went past target — correction recorded";
                ((Label)Field(form, "status").GetValue(form)).Text = "Move in one quick motion, then correct naturally. The trail shows the movement and the orange marker shows the first stop.";
                Field(form, "trialCompletion").SetValue(form, new TaskCompletionSource<TargetTrial>());
                Field(form, "targetCenter").SetValue(form, new PointF(250, 140));
                Field(form, "targetRadius").SetValue(form, 20f);
                Field(form, "dwellProgress").SetValue(form, .65);
                Field(form, "liveMetrics").SetValue(form, live);
                ((List<PointF>)Field(form, "trail").GetValue(form)).AddRange(new[] { new PointF(100, 140), new PointF(180, 145), new PointF(280, 140), new PointF(255, 140) });
                Render(form, Path.Combine(Path.GetDirectoryName(imagePath), Path.GetFileNameWithoutExtension(imagePath) + "-live.png"));
            }
        }
    }
}
