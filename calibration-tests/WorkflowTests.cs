extern alias trialhost;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using grapher;
using grapher.Models.Calibration;
using grapher.Models.Mouse;
using grapher.Models.Serialized;
using grapher.Models.Theming;
using Newtonsoft.Json;
using RawAccel.ServiceSupport;
using Host = trialhost::RawAccel.Service;
using HostProtocol = trialhost::RawAccel.ServiceSupport;

public static class WorkflowTests
{
    private const string Owner = "test-user";
    private static readonly IntPtr Mouse = new IntPtr(1234);
    private static FieldInfo Field(object obj, string name) { return obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic); }
    private static object Get(object obj, string name) { return Field(obj, name).GetValue(obj); }
    private static void Set(object obj, string name, object value) { Field(obj, name).SetValue(obj, value); }
    private static object Invoke(object obj, string name, params object[] args)
    {
        return obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Invoke(obj, args);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task YieldOnce(int milliseconds) { await Task.Yield(); }
    private static string Fingerprint(string json) { return SettingsFingerprint.Compute(json); }

    private sealed class MemoryDriver : Host.IAccelerationDriver
    {
        public string Active, Baseline;
        public bool Inconclusive, FailBaselineRestore;
        private readonly Dictionary<string, int> candidates = new Dictionary<string, int>();
        public string Normalize(string json)
        {
            var parsed = DriverConfig.Convert(json);
            if (parsed.Item1 == null || parsed.Item2 != null) throw new InvalidDataException(parsed.Item2 ?? "Invalid settings");
            return parsed.Item1.ToJSON();
        }
        public string ReadActive() { return Active; }
        public void Apply(string json)
        {
            json = Normalize(json);
            if (FailBaselineRestore && Fingerprint(json) == Fingerprint(Baseline) && Fingerprint(Active) != Fingerprint(Baseline))
                throw new IOException("Injected restore failure");
            Active = json;
            string key = Fingerprint(json);
            if (key != Fingerprint(Baseline) && !candidates.ContainsKey(key)) candidates.Add(key, candidates.Count + 1);
        }
        public int Duration()
        {
            if (Inconclusive) return 1200;
            string key = Fingerprint(Active);
            if (key == Fingerprint(Baseline)) return 1200;
            int rank;
            return candidates.TryGetValue(key, out rank) ? Math.Max(450, 1000 - rank * 200) : 1200;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string DirectoryPath, Path, Original;
        public readonly MemoryDriver Driver;
        public readonly Host.SettingsEngine Engine;
        public readonly CalibrationTrialClient Client;
        public readonly CalibrationForm Form;
        public readonly List<string> Commands = new List<string>();
        public readonly Dictionary<string, List<TrialMetrics>> Metrics = new Dictionary<string, List<TrialMetrics>>();
        public int Scored, Centers;
        public int InterruptAt;
        public int KeepAt;
        private readonly System.Windows.Forms.Timer feeder;
        private readonly Label status;
        private readonly SettingsManager settings;
        private readonly MouseWatcher watcher;
        private TargetTrial fed;
        private ValorantAimTrial lastShot;
        public bool UseValorant, InterruptValorantCandidate, LoseConfirmation;
        public int ValorantShots;
        public bool ReachedConfirmation;

        public Fixture(bool standalone, bool inconclusive = false)
        {
            DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RawAccel-workflow-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(DirectoryPath);
            Path = System.IO.Path.Combine(DirectoryPath, "settings.json");
            var config = DriverConfig.GetDefault();
            var profile = new Profile { name = "Second" };
            config.profiles.Add(profile);
            config.devices.Add(new DeviceSettings { id = "test-device", name = "Test mouse", profile = "Second" });
            var parsed = DriverConfig.Convert(config.ToJSON());
            Check(parsed.Item1 != null && parsed.Item2 == null, "Fixture configuration is invalid: " + parsed.Item2);
            Original = parsed.Item1.ToJSON();
            File.WriteAllText(Path, Original);
            Driver = new MemoryDriver { Active = Original, Baseline = Original, Inconclusive = inconclusive };
            Engine = new Host.SettingsEngine(Driver, Path);
            Engine.PrepareStandalone();
            Client = new CalibrationTrialClient(Send, standalone);
            settings = (SettingsManager)FormatterServices.GetUninitializedObject(typeof(SettingsManager));
            var device = (MultiHandleDevice)FormatterServices.GetUninitializedObject(typeof(MultiHandleDevice));
            typeof(MultiHandleDevice).GetField("id").SetValue(device, "test-device");
            typeof(MultiHandleDevice).GetField("name").SetValue(device, "Test mouse");
            typeof(MultiHandleDevice).GetField("handles").SetValue(device, new List<IntPtr> { Mouse });
            Set(settings, "<SystemDevices>k__BackingField", new List<MultiHandleDevice> { device });
            Set(settings, "<ActiveNormTaggedHandles>k__BackingField", new List<ValueTuple<IntPtr, bool>>());
            settings.ReloadAfterCalibration(Original, true);
            watcher = (MouseWatcher)FormatterServices.GetUninitializedObject(typeof(MouseWatcher));
            watcher.PauseCharts = true;
            Theme.CurrentScheme = ColorScheme.LightTheme;
            CalibrationForm form = null;
            Func<Point> cursor = () =>
            {
                if (form == null) return new Point(0, 0);
                var panel = (Panel)Get(form, "target");
                return panel.PointToScreen(new Point(panel.ClientSize.Width / 2, panel.ClientSize.Height / 2));
            };
            Form = form = new CalibrationForm(settings, watcher,
                () => DriverConfig.Convert(Driver.Active).Item1, Client, YieldOnce, cursor, () => true);
            Form.ShowInTaskbar = false; Form.Opacity = 0; Form.Show(); Form.PerformLayout();
            status = (Label)Get(Form, "status");
            feeder = new System.Windows.Forms.Timer { Interval = 1 };
            feeder.Tick += Feed;
        }
        private Task<ServiceResponse> Send(ServiceRequest request)
        {
            Commands.Add(request.Command);
            try
            {
                HostProtocol.ServiceResponse response;
                if (request.Command == "status") response = Engine.GetStatus();
                else
                {
                    var hostRequest = JsonConvert.DeserializeObject<HostProtocol.ServiceRequest>(JsonConvert.SerializeObject(request));
                    response = Engine.TrialCommand(hostRequest, Owner);
                }
                return Task.FromResult(JsonConvert.DeserializeObject<ServiceResponse>(JsonConvert.SerializeObject(response)));
            }
            catch (Exception ex) { return Task.FromResult(new ServiceResponse { Success = false, Message = ex.Message }); }
        }
        private static long Ticks(double ms) { return (long)(Stopwatch.Frequency * ms / 1000); }
        private void RawPacket(IntPtr device, long ticks, int x, int y, MouseWatcher.RawMouseButtons buttons)
        {
            var packet = new MouseWatcher.RawInput();
            packet.Header.Type = MouseWatcher.RawInputType.Mouse;
            packet.Header.Device = device;
            packet.Data.Mouse.LastX = x;
            packet.Data.Mouse.LastY = y;
            packet.Data.Mouse._Data.ButtonFlags = buttons;
            Invoke(watcher, "ProcessMouseInput", packet, ticks);
        }
        private void FeedValorant()
        {
            var trial = (ValorantAimTrial)Get(Form, "aimTrial");
            if (trial == null || ReferenceEquals(trial, lastShot)) return;
            lastShot = trial;
            var condition = (ValorantAimCondition)Get(Form, "aimCondition");
            long onset = (long)Get(trial, "presentedTicks") + Ticks(150);
            bool originalActive = Fingerprint(Driver.Active) == Fingerprint(Original);
            if (InterruptValorantCandidate && !originalActive)
            {
                RawPacket(new IntPtr(4321), onset, 0, 0, MouseWatcher.RawMouseButtons.LeftDown);
                return;
            }
            bool confirming = ((string)Get(Form, "valorantProgress")).StartsWith("Confirmation");
            ReachedConfirmation |= confirming;
            bool overshoot = condition.Category == "Micro" && (originalActive || Driver.Inconclusive || (LoseConfirmation && confirming));
            double scale = overshoot ? 1 + 2 * condition.Radius / trial.Metrics.Distance : 1;
            double degrees = ValorantAim.DegreesPerCount * (double)((NumericUpDown)Get(Form, "valorantSensitivity")).Value;
            RawPacket(Mouse, onset, (int)Math.Round(scale * condition.Yaw / degrees),
                (int)Math.Round(-scale * condition.Pitch / degrees), 0);
            RawPacket(Mouse, onset + Ticks(180), 0, 0, MouseWatcher.RawMouseButtons.LeftDown);
            RawPacket(Mouse, onset + Ticks(181), 0, 0, MouseWatcher.RawMouseButtons.LeftUp);
            ValorantShots++;
        }
        private void Feed(object sender, EventArgs e)
        {
            if (UseValorant) { FeedValorant(); return; }
            var capture = (CalibrationCapture)Get(Form, "capture");
            var trial = capture == null ? null : capture.Current;
            if (trial == null || ReferenceEquals(fed, trial)) return;
            fed = trial;
            if (trial.ConditionId < 0) Centers++;
            else
            {
                Scored++;
                if (Scored == 1)
                {
                    Check(!((Button)Get(Form, "accept")).Enabled, "Apply must stay disabled while measurements are running.");
                    ((Task)Invoke(Form, "AcceptCandidate")).GetAwaiter().GetResult();
                    Check(!Commands.Contains("trial-commit"), "A trial must not be saved before a recommendation is confirmed.");
                }
                if (InterruptAt > 0 && Scored >= InterruptAt)
                {
                    Invoke(Form, "Interrupt", "Injected cancellation");
                    return;
                }
                if (KeepAt > 0 && Scored >= KeepAt)
                {
                    var button = (Button)Get(Form, "keep");
                    Check(button.Enabled && button.Visible, "Keep this setting must be clickable while a candidate is active.");
                    button.PerformClick();
                    return;
                }
                int duration = Driver.Duration();
                int travel = duration - 200;
                double distance = Math.Sqrt(Math.Pow(trial.TargetX - trial.StartX, 2) + Math.Pow(trial.TargetY - trial.StartY, 2));
                double beforeEntry = Math.Max(0, 1 - (trial.Radius + 2) / distance);
                trial.Cursor.Clear();
                for (int ms = 0; ms <= duration; ms += 20)
                {
                    double t = ms >= travel ? 1 : beforeEntry * ms / travel;
                    trial.Cursor.Add(new CursorSample { Ticks = trial.PresentedTicks + Ticks(ms),
                        X = trial.StartX + (trial.TargetX - trial.StartX) * t,
                        Y = trial.StartY + (trial.TargetY - trial.StartY) * t });
                }
                trial.Completed = true;
                string label = status.Text.Split(new[] { " — " }, StringSplitOptions.None)[0];
                if (!Metrics.ContainsKey(label)) Metrics[label] = new List<TrialMetrics>();
                Metrics[label].Add(CalibrationAnalysis.Analyze(trial));
            }
            if (trial.ConditionId < 0) trial.Completed = true;
            Invoke(Form, "FinishCurrent");
        }
        public void RunSession() { Pump((Task)Invoke(Form, "RunSession"), "calibration session", () => status.Text, () => Feed(null, EventArgs.Empty)); }
        public void RunValorant()
        {
            UseValorant = true;
            ((CheckBox)Get(Form, "valorantMode")).Checked = true;
            ((NumericUpDown)Get(Form, "valorantSensitivity")).Value = .32m;
            RunSession();
        }
        public void Call(string method) { Pump((Task)Invoke(Form, method), method, () => status.Text, () => Feed(null, EventArgs.Empty)); }
        public string Status { get { return status.Text; } }
        public void Dispose()
        {
            feeder.Stop(); feeder.Dispose();
            Driver.FailBaselineRestore = false;
            Engine.EndTrialForShutdown();
            Set(Form, "closing", true);
            Form.Close();
            Application.DoEvents();
            if (!Form.IsDisposed) Form.Dispose();
            string target = System.IO.Path.GetFullPath(DirectoryPath).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!string.Equals(System.IO.Path.GetDirectoryName(target), temp, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(target).StartsWith("RawAccel-workflow-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a directory outside the workflow test temporary folder.");
            System.IO.Directory.Delete(target, true);
        }
    }

    private static void Pump(Task task, string phase, Func<string> status, Action feed)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(120)) { Application.DoEvents(); feed(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException(phase + " timed out: " + status());
        task.GetAwaiter().GetResult();
        Application.DoEvents();
    }
    private static void FullWorkflow(bool standalone)
    {
        using (var f = new Fixture(standalone))
        {
            f.RunSession();
            Check(f.Form.SelectedProfileIndex == 1, "Selected device did not map to second profile.");
            Check(!f.Form.SettingsAccepted && !f.Commands.Contains("trial-commit") &&
                Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Original),
                "A recommendation must not be saved without explicit acceptance: " + f.Status);
            Check(((Label)Get(f.Form, "phase")).Text.StartsWith("BETTER — "), "Confirmed improvement must have a single clear verdict.");
            Check(((Button)Get(f.Form, "accept")).Enabled, "Confirmed recommendation must offer acceptance.");
            f.Call("AcceptCandidate");
            Check(f.Form.SettingsAccepted, "Explicit acceptance did not save the recommendation: " + f.Status);
            int screened = ((ComboBox)Get(f.Form, "testedProfiles")).Items.Count;
            Check(screened >= 2, "Bounded candidate screening did not finish.");
            Check(f.Commands.Count(c => c == "trial-update") >= screened, "Screened profiles were not applied to the live test driver.");
            Check(f.Scored == 15 + screened * 5 + 26, "The finite search exceeded or skipped its advertised trial count.");
            Check(Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Driver.Active), "Commit did not persist active settings.");
            Check(Fingerprint(f.Driver.Active) != Fingerprint(f.Original), "Committed candidate was unchanged.");
            Check(((Label)Get(f.Form, "settingsState")).Text.StartsWith("SETTINGS UPDATED — active now and saved"), "Verified commit must clearly report applied and saved settings.");
            Check(Fingerprint(f.Driver.Active) != Fingerprint(f.Original), "Committed candidate was unchanged.");
            Check(((Label)Get(f.Form, "settingsState")).Text.StartsWith("SETTINGS UPDATED — active now and saved"), "Verified commit must clearly report applied and saved settings.");
            Check(((Button)Get(f.Form, "accept")).Text == "Applied && saved" && !((Button)Get(f.Form, "accept")).Enabled,
                "A successful save must leave a visible completed-action label and prevent duplicate saves.");
            var before = DriverConfig.Convert(f.Original).Item1;
            var after = DriverConfig.Convert(f.Driver.Active).Item1;
            Check(JsonConvert.SerializeObject(before.profiles[0]) == JsonConvert.SerializeObject(after.profiles[0]), "Unselected profile changed.");
            Check(after.devices[0].profile == "Second", "Device assignment changed.");
            f.Call("RestoreCommitted");
            Check(!f.Form.SettingsAccepted && Fingerprint(f.Driver.Active) == Fingerprint(f.Original), "Restore did not recover baseline: " + f.Status);
            Check(Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Original), "Restore did not persist the baseline.");
            Check(((Label)Get(f.Form, "settingsState")).Text.StartsWith("PREVIOUS SETTINGS RESTORED"), "Restore must replace the saved-candidate indicator.");
        }
    }
    private static void CancellationAndRecalibration()
    {
        using (var f = new Fixture(false))
        {
            f.InterruptAt = 5; f.RunSession();
            Check(Fingerprint(f.Driver.Active) == Fingerprint(f.Original), "Cancel failed to restore original settings.");
            Check(f.Commands.Contains("trial-cancel"), "Cancellation did not reach the engine.");
            f.InterruptAt = 0; f.RunSession();
            f.Call("AcceptCandidate");
            Check(f.Form.SettingsAccepted && Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Driver.Active),
                "Recalibration did not produce and save a result: " + f.Status);
            f.Call("RestoreCommitted");
            Check(Fingerprint(f.Driver.Active) == Fingerprint(f.Original), "Recalibration restore did not recover baseline.");
        }
        using (var f = new Fixture(true))
        {
            f.InterruptAt = 24;
            f.RunSession();
            Check(((ComboBox)Get(f.Form, "testedProfiles")).Items.Count > 0 &&
                ((TextBox)Get(f.Form, "searchReport")).Text.Contains("Completed screening results"),
                "Interruption must retain the completed profile trials for review.");
            Check(Fingerprint(f.Driver.Active) == Fingerprint(f.Original), "Interrupted search did not restore the original.");
            f.Call("PreviewSelectedProfile");
            Check(((Button)Get(f.Form, "accept")).Enabled, "A completed trial could not be previewed after interruption.");
            Invoke(f.Form, "Interrupt", "Injected focus loss during manual preview");
            f.Call("CancelTrial");
            f.Call("PreviewSelectedProfile");
            Check(((Button)Get(f.Form, "accept")).Enabled &&
                Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Original),
                "A second preview after focus loss must work without saving.");
            f.Call("CancelTrial");
        }
    }
    private static void Inconclusive()
    {
        using (var f = new Fixture(true, true))
        {
            f.RunSession();
            Check(!f.Form.SettingsAccepted && !((Button)Get(f.Form, "accept")).Enabled, "Inconclusive trial offered acceptance.");
            Check(Fingerprint(f.Driver.Active) == Fingerprint(f.Original), "Inconclusive trial changed settings.");
            Check(Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Original), "Inconclusive trial changed saved settings.");
            Check(((Label)Get(f.Form, "phase")).Text.StartsWith("FINISHED"), "An inconclusive completed search must identify completion.");
            Check(((TextBox)Get(f.Form, "searchReport")).Text.Contains("Best screened profile") &&
                ((ComboBox)Get(f.Form, "testedProfiles")).Items.Count >= 2,
                "Completed screening must leave the tested profiles and settings available for review.");
            Check(((Label)Get(f.Form, "settingsState")).Text.StartsWith("FINISHED"), "Completed search must explicitly report its end.");
            f.Call("PreviewSelectedProfile");
            Check(((Button)Get(f.Form, "accept")).Enabled && ((Button)Get(f.Form, "accept")).Text.Contains("selected profile"),
                "A user-selected tested profile should be previewable and manually saveable.");
            Check(Fingerprint(f.Driver.Active) != Fingerprint(f.Original) &&
                Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Original), "Manual preview must update live settings before saving.");
            f.Call("AcceptCandidate");
            Check(f.Form.SettingsAccepted && Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Driver.Active),
                "The explicitly selected profile failed to persist.");
            f.Call("RestoreCommitted");
            Check(Fingerprint(f.Driver.Active) == Fingerprint(f.Original), "Manual selection restore failed.");
        }
    }
    private static void FailedRestore()
    {
        using (var f = new Fixture(true))
        {
            f.RunSession();
            f.Call("AcceptCandidate");
            Check(f.Form.SettingsAccepted, "Expected an accepted candidate before testing restore failure: " + f.Status);
            f.Driver.FailBaselineRestore = true;
            f.Call("RestoreCommitted");
            Check(f.Form.OutcomeUncertain, "Failed restoration did not report an unresolved outcome.");
            Check(((Label)Get(f.Form, "settingsState")).Text.StartsWith("SETTINGS STATUS UNKNOWN"), "Failed restoration must never claim settings were restored.");
            Check(((Button)Get(f.Form, "leavePending")).Visible, "Failed restoration did not offer recovery-pending exit.");
            Check(Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Driver.Active), "Failed restore must leave the saved candidate intact.");
        }
    }
    private static void ValorantWorkflow()
    {
        using (var f = new Fixture(false))
        {
            f.RunValorant();
            Check(f.ReachedConfirmation && f.ValorantShots >= 3 + 12 + 2 * (1 + 12), "Expected baseline, comparison and fresh confirmation shots.");
            Check(!f.Form.SettingsAccepted && !f.Commands.Contains("trial-commit"), "Confirmed settings must wait for acceptance.");
            f.Call("AcceptCandidate");
            Check(f.Form.SettingsAccepted, "Explicit acceptance failed: " + f.Status);
            Check(Fingerprint(f.Driver.Active) != Fingerprint(f.Original) &&
                Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Driver.Active), "Confirmed settings failed to persist.");
            f.Call("RestoreCommitted");
            Check(Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Original), "Restore did not persist the original.");
            Check(Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Original), "Restore did not persist the original.");
        }
        using (var f = new Fixture(true, true))
        {
            f.RunValorant();
            Check(!((Button)Get(f.Form, "accept")).Enabled && Fingerprint(f.Driver.Active) == Fingerprint(f.Original),
                "Identical first-shot outcomes should retain the original profile.");
        }
        using (var f = new Fixture(true))
        {
            f.LoseConfirmation = true;
            f.RunValorant();
            Check(f.ReachedConfirmation && !((Button)Get(f.Form, "accept")).Enabled &&
                Fingerprint(f.Driver.Active) == Fingerprint(f.Original), "An exploratory winner that fails fresh confirmation must be rejected.");
        }
        using (var f = new Fixture(true))
        {
            f.InterruptValorantCandidate = true;
            f.RunValorant();
            Check(!((bool)Get(f.Form, "valorantViewport")) && f.Commands.Contains("trial-cancel") &&
                !((Button)Get(f.Form, "accept")).Enabled && Fingerprint(f.Driver.Active) == Fingerprint(f.Original),
                "Input from another mouse should end the test and restore the live baseline.");
        }
    }
    private static void MidRunKeep()
    {
        using (var f = new Fixture(false))
        {
            f.KeepAt = 18;
            f.RunSession();
            Check(f.Form.SettingsAccepted, "Mid-run keep did not save the live candidate: " + f.Status);
            Check(Fingerprint(f.Driver.Active) != Fingerprint(f.Original), "Keep must save a trial candidate, not the baseline.");
            Check(Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Driver.Active), "Keep did not persist the live candidate.");
            Check(((Button)Get(f.Form, "restore")).Visible, "Keep must expose the undo button.");
            f.Call("RestoreCommitted");
            Check(Fingerprint(f.Driver.Active) == Fingerprint(f.Original) &&
                Fingerprint(File.ReadAllText(f.Path)) == Fingerprint(f.Original), "Undo after keep did not recover the baseline.");
        }
    }
    public static void Run()
    {
        Console.WriteLine("GUI workflow: mid-run keep saves the live candidate");
        MidRunKeep();
        Console.WriteLine("GUI workflow: service-backed acceptance and restore");
        FullWorkflow(false);
        Console.WriteLine("GUI workflow: standalone acceptance and restore");
        FullWorkflow(true);
        Console.WriteLine("GUI workflow: cancel and recalibrate");
        CancellationAndRecalibration();
        Console.WriteLine("GUI workflow: inconclusive result");
        Inconclusive();
        Console.WriteLine("GUI workflow: failed restore");
        FailedRestore();
        Console.WriteLine("GUI workflow: Valorant raw first-click acceptance, restore, inconclusive result and interruption");
        ValorantWorkflow();
        Console.WriteLine("Hermetic GUI workflows passed for service and standalone transports.");
    }
}
