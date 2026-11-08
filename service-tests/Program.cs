using RawAccel.Service;
using RawAccel.ServiceSupport;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private sealed class Driver : IAccelerationDriver
    {
        public string Active = "original";
        public int Writes;
        public bool Fail;
        public bool FailNext;
        public string StatePath;
        public string Normalize(string json)
        {
            if (json == "invalid") throw new InvalidDataException("Invalid profile");
            return json;
        }
        public string ReadActive() { return StatePath == null ? Active : File.ReadAllText(StatePath); }
        public void Apply(string json)
        {
            if (Fail || FailNext) { FailNext = false; throw new ApplicationException("Driver not ready"); }
            Active = json;
            if (StatePath != null) File.WriteAllText(StatePath, json);
            Writes++;
        }
    }

    private static int passed;
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static void Test(string name, Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "RawAccel-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try { action(directory); passed++; Console.WriteLine("PASS " + name); }
        finally { Directory.Delete(directory, true); }
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static int Main(string[] args)
    {
        if (args.Length == 6 && args[0] == "--fake-standalone-host")
        {
            string sid;
            using (var identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
            var fake = new Driver { StatePath = args[4] };
            var fakeEngine = new SettingsEngine(fake, args[1], monotonicNow: () => Stopwatch.GetTimestamp() * 20, journalBase: args[5]);
            return StandaloneTrialHost.Run(fakeEngine, args[1], args[2], sid, args[3], TimeSpan.FromSeconds(4), false);
        }
        try
        {
            Test("Readiness confirms successful restoration or Apply and clears after restore failure", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, "saved");
                var engine = new SettingsEngine(new Driver(), path);
                Check(!engine.GetStatus().SettingsApplied, "Waiting service reported restored settings");
                engine.Restore(true);
                Check(engine.GetStatus().SettingsApplied, "Successful restoration was not confirmed");
                engine.ReportFailure("Could not restore settings");
                Check(!engine.GetStatus().SettingsApplied, "Failed restoration reported ready");
                engine.ApplyAndSave("new");
                Check(engine.GetStatus().SettingsApplied, "Successful Apply did not recover readiness");
            });
            Test("Apply persists and next boot restores with GUI absent", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                var driver = new Driver();
                new SettingsEngine(driver, path).ApplyAndSave("profile1");
                Check(File.ReadAllText(path) == "profile1" && driver.Active == "profile1", "Save failed");
                driver.Active = "original";
                new SettingsEngine(driver, path).Restore(true);
                Check(driver.Active == "profile1", "Boot did not restore saved profile");
            });
            Test("Invalid settings preserve disk and active profile", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, "saved");
                var driver = new Driver();
                Throws<InvalidDataException>(() => new SettingsEngine(driver, path).ApplyAndSave("invalid"));
                Check(File.ReadAllText(path) == "saved" && driver.Writes == 0, "Invalid settings changed state");
            });
            Test("Driver failure preserves saved settings and cleans staging file", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, "saved");
                var driver = new Driver { Fail = true };
                Throws<ApplicationException>(() => new SettingsEngine(driver, path).ApplyAndSave("new"));
                Check(File.ReadAllText(path) == "saved" && !File.Exists(path + ".pending"), "Failed write damaged settings");
            });
            Test("Commit failure rolls driver back", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, "saved");
                Directory.CreateDirectory(path + ".lastgood"); // Force atomic replacement to fail.
                var driver = new Driver();
                Throws<IOException>(() => new SettingsEngine(driver, path).ApplyAndSave("new"));
                Check(driver.Active == "original" && driver.Writes == 2 && File.ReadAllText(path) == "saved",
                    "Commit failure did not roll back");
            });
            Test("Corrupt primary restores backup without rewriting damaged file", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, "invalid");
                File.WriteAllText(path + ".lastgood", "backup");
                var driver = new Driver();
                new SettingsEngine(driver, path).Restore(true);
                Check(driver.Active == "backup" && File.ReadAllText(path) == "invalid", "Backup recovery failed");
            });
            Test("Missing configuration does not silently change sensitivity", directory =>
            {
                var driver = new Driver();
                Throws<IOException>(() => new SettingsEngine(driver, Path.Combine(directory, "missing.json")).Restore(true));
                Check(driver.Writes == 0, "Missing configuration changed sensitivity");
            });
            Test("A late startup retry cannot override a successful GUI Apply", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                var driver = new Driver();
                var engine = new SettingsEngine(driver, path);
                engine.ApplyAndSave("new");
                File.WriteAllText(path, "older");
                engine.Restore(true);
                Check(driver.Active == "new" && driver.Writes == 1, "Startup overwrote GUI change");
            });
            Test("Repeated saves retain the previous valid backup", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                var engine = new SettingsEngine(new Driver(), path);
                engine.ApplyAndSave("first"); engine.ApplyAndSave("second");
                Check(File.ReadAllText(path) == "second" && File.ReadAllText(path + ".lastgood") == "first",
                    "Backup not preserved");
            });
            Test("Resume restores the latest saved profile", directory =>
            {
                var driver = new Driver();
                var engine = new SettingsEngine(driver, Path.Combine(directory, "settings.json"));
                engine.ApplyAndSave("latest"); driver.Active = "original";
                engine.Restore(false);
                Check(driver.Active == "latest", "Resume did not restore latest profile");
            });
            Test("Protocol rejects truncated and oversized frames", directory =>
            {
                Throws<EndOfStreamException>(() => ServiceProtocol.ReadAsync<ServiceRequest>(
                    new MemoryStream(new byte[] { 2 }), CancellationToken.None).GetAwaiter().GetResult());
                Throws<InvalidDataException>(() => ServiceProtocol.ReadAsync<ServiceRequest>(
                    new MemoryStream(BitConverter.GetBytes(ServiceProtocol.MaximumMessageBytes + 1)),
                    CancellationToken.None).GetAwaiter().GetResult());
            });
            Test("Oversized saved settings cannot become an unrestorable startup profile", directory =>
            {
                var driver = new Driver();
                var engine = new SettingsEngine(driver, Path.Combine(directory, "settings.json"));
                Throws<InvalidDataException>(() => engine.ApplyAndSave(new string('a', ServiceProtocol.MaximumMessageBytes + 1)));
                Check(driver.Writes == 0, "Oversized profile changed the driver");
            });
            Test("Concurrent Apply requests leave disk and driver consistent", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                var driver = new Driver();
                var engine = new SettingsEngine(driver, path);
                var tasks = new Task[10];
                for (int i = 0; i < tasks.Length; i++)
                {
                    string profile = "profile-" + i;
                    tasks[i] = Task.Run(() => engine.ApplyAndSave(profile));
                }
                Task.WaitAll(tasks);
                Check(File.ReadAllText(path) == driver.Active && driver.Writes == 10, "Concurrent writes lost consistency");
            });
            Test("Actual wrapper validates default settings and rejects malformed JSON without touching driver", directory =>
            {
                var driver = new AccelerationDriver();
                string json = DriverConfig.GetDefault().ToJSON();
                Check(driver.Normalize(json).Contains("profiles"), "Real wrapper did not validate settings");
                Throws<InvalidDataException>(() => driver.Normalize("{malformed"));
                Throws<InvalidDataException>(() => driver.Normalize("null"));
            });
            Test("JSON angle snapping reaches native processing in whole and by-component modes", directory =>
            {
                foreach (bool whole in new[] { true, false })
                {
                    var config = DriverConfig.GetDefault();
                    var profile = config.profiles[0];
                    profile.snap = 5;
                    profile.argsX.mode = profile.argsY.mode = AccelMode.noaccel;
                    profile.inputSpeedArgs.combineMagnitudes = whole;
                    config.SetProfileAt(0, profile);
                    var parsed = DriverConfig.Convert(config.ToJSON());
                    Check(parsed.Item2 == null && parsed.Item1.profiles[0].snap == 5,
                        "JSON lost snapping angle");
                    var accel = parsed.Item1.accels[0];
                    foreach (int sign in new[] { -1, 1 })
                    {
                        var horizontal = accel.Accelerate(sign * 100, 1, 1, 1);
                        var vertical = accel.Accelerate(1, sign * 100, 1, 1);
                        Check(horizontal.Item2 == 0 && System.Math.Sign(horizontal.Item1) == sign,
                            "Near-horizontal input did not snap");
                        Check(vertical.Item1 == 0 && System.Math.Sign(vertical.Item2) == sign,
                            "Near-vertical input did not snap");
                    }
                    var diagonal = accel.Accelerate(100, 100, 1, 1);
                    Check(diagonal.Item1 == 100 && diagonal.Item2 == 100,
                        "Input outside the snap angle changed");
                }
            });
            Test("JSON snapping accepts endpoints and rejects out-of-range angles", directory =>
            {
                foreach (double angle in new[] { -1.0, 0.0, 45.0, 46.0 })
                {
                    var config = DriverConfig.GetDefault();
                    var profile = config.profiles[0];
                    profile.snap = angle;
                    config.SetProfileAt(0, profile);
                    var parsed = DriverConfig.Convert(config.ToJSON());
                    Check((parsed.Item2 == null) == (angle >= 0 && angle <= 45),
                        "Wrong snapping validation for " + angle);
                }
                var defaults = DriverConfig.GetDefault();
                var noSnap = defaults.profiles[0];
                noSnap.argsX.mode = noSnap.argsY.mode = AccelMode.noaccel;
                defaults.SetProfileAt(0, noSnap);
                var output = DriverConfig.Convert(defaults.ToJSON()).Item1.accels[0].Accelerate(100, 1, 1, 1);
                Check(output.Item1 == 100 && output.Item2 == 1, "Zero did not disable snapping");
            });
            Test("Protocol roundtrip preserves settings", directory =>
            {
                var stream = new MemoryStream();
                ServiceProtocol.WriteAsync(stream, new ServiceRequest { Command = "apply", Settings = "{\"a\":2}" },
                    CancellationToken.None).GetAwaiter().GetResult();
                stream.Position = 0;
                var result = ServiceProtocol.ReadAsync<ServiceRequest>(stream, CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Command == "apply" && result.Settings == "{\"a\":2}", "Protocol changed message");
            });
            Test("Real local pipe accepts Apply and rejects invalid commands; stop cancels idle listener", directory =>
            {
                string pipeName = "RawAccel.Test." + Guid.NewGuid();
                string path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, "startup");
                var driver = new Driver();
                using (var service = new RawAccelService(new SettingsEngine(driver, path), pipeName, directory))
                {
                    typeof(RawAccelService).GetMethod("OnStart", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(service, new object[] { new string[0] });
                    try
                    {
                        Check(Send(pipeName, "apply", "from-gui").Success, "Pipe Apply failed");
                        Check(driver.Active == "from-gui" && File.ReadAllText(path) == "from-gui", "Pipe Apply not persisted");
                        Check(!Send(pipeName, "apply", "invalid").Success, "Invalid settings accepted");
                        Check(!Send(pipeName, "execute-anything", null).Success, "Unknown command accepted");
                        var status = Send(pipeName, "status", null);
                        Check(status.Success && status.SettingsApplied,
                            "Pipe status did not confirm successful settings application after a bad request");
                        using (var abandoned = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                        {
                            abandoned.Connect(2000);
                            abandoned.Write(BitConverter.GetBytes(100), 0, 4);
                        }
                        Thread.Sleep(350);
                        Check(Send(pipeName, "status", null).Success, "Abandoned request blocked the listener");
                    }
                    finally
                    {
                        var timer = Stopwatch.StartNew();
                        typeof(RawAccelService).GetMethod("OnStop", BindingFlags.NonPublic | BindingFlags.Instance)
                            .Invoke(service, new object[0]);
                        Check(timer.Elapsed < TimeSpan.FromSeconds(3), "Idle service shutdown was slow");
                    }
                }
            });
            Test("Startup retries a temporarily unavailable driver without GUI intervention", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, "startup");
                string pipeName = "RawAccel.RetryTest." + Guid.NewGuid();
                var driver = new Driver { Fail = true };
                var engine = new SettingsEngine(driver, path);
                using (var service = new RawAccelService(engine, pipeName, directory))
                {
                    typeof(RawAccelService).GetMethod("OnStart", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(service, new object[] { new string[0] });
                    try
                    {
                        Check(SpinWait.SpinUntil(() => engine.Status.Contains("Driver not ready"), 2000), "No initial retry recorded");
                        driver.Fail = false;
                        Check(SpinWait.SpinUntil(() => engine.Status.Contains("Saved settings applied"), 4000), "Startup did not retry");
                        Check(driver.Active == "startup", "Retry did not restore settings");
                    }
                    finally
                    {
                        typeof(RawAccelService).GetMethod("OnStop", BindingFlags.NonPublic | BindingFlags.Instance)
                            .Invoke(service, new object[0]);
                    }
                }
            });
            Test("Trial cancel restores live baseline without changing saved settings", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", saved = "{\"v\":2}", candidate = "{\"v\":3}";
                File.WriteAllText(path, saved);
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path);
                var begin = engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                Check(begin.TrialState == "active" && driver.Active == candidate && File.ReadAllText(path) == saved, "Trial persisted or failed");
                Throws<IOException>(() => engine.ApplyAndSave(saved));
                var cancel = engine.TrialCommand(new ServiceRequest { Command = "trial-cancel", RequestId = "c", LeaseId = begin.LeaseId }, "owner");
                Check(cancel.TrialState == "cancelled" && driver.Active == baseline && File.ReadAllText(path) == saved, "Cancel failed");
            });
            Test("Trial commit and explicit restore validate live and saved state", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":3}";
                File.WriteAllText(path, baseline);
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path);
                var begin = engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                var commit = engine.TrialCommand(new ServiceRequest { Command = "trial-commit", RequestId = "c", LeaseId = begin.LeaseId }, "owner");
                Check(commit.TrialState == "committed" && File.ReadAllText(path) == candidate, "Commit failed");
                var restore = engine.TrialCommand(new ServiceRequest { Command = "trial-restore", RequestId = "r", LeaseId = begin.LeaseId,
                    ExpectedFingerprint = SettingsFingerprint.Compute(candidate) }, "owner");
                Check(restore.TrialState == "restored" && driver.Active == baseline && File.ReadAllText(path) == baseline, "Restore failed");
            });
            Test("Trial commit mid-preview saves the current candidate (mid-calibration keep)", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}", screening = "{\"v\":3}";
                File.WriteAllText(path, baseline);
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path);
                var begin = engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                // Mid-screening keep: switch the live candidate, then commit without confirmation.
                engine.TrialCommand(new ServiceRequest { Command = "trial-update", RequestId = "u", LeaseId = begin.LeaseId,
                    Settings = screening, ExpectedFingerprint = SettingsFingerprint.Compute(candidate) }, "owner");
                var commit = engine.TrialCommand(new ServiceRequest { Command = "trial-commit", RequestId = "c", LeaseId = begin.LeaseId }, "owner");
                Check(commit.TrialState == "committed" && File.ReadAllText(path) == screening && driver.Active == screening, "Mid-preview commit did not save the current candidate");
                var restore = engine.TrialCommand(new ServiceRequest { Command = "trial-restore", RequestId = "r", LeaseId = begin.LeaseId,
                    ExpectedFingerprint = SettingsFingerprint.Compute(screening) }, "owner");
                Check(restore.TrialState == "restored" && driver.Active == baseline && File.ReadAllText(path) == baseline, "Undo after mid-preview commit failed");
            });
            Test("Restart recovers a trial without applying different startup settings", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", saved = "{\"v\":2}", candidate = "{\"v\":3}";
                File.WriteAllText(path, saved);
                var driver = new Driver { Active = baseline };
                var oldEngine = new SettingsEngine(driver, path);
                oldEngine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                new SettingsEngine(driver, path).Restore(true);
                Check(driver.Active == baseline && File.ReadAllText(path) == saved, "Restart clobbered live baseline");
            });
            Test("External changes block trial rollback", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}", external = "{\"v\":3}";
                File.WriteAllText(path, baseline);
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path);
                var begin = engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                driver.Active = external;
                Throws<IOException>(() => engine.TrialCommand(new ServiceRequest { Command = "trial-cancel", RequestId = "c", LeaseId = begin.LeaseId }, "owner"));
                Check(driver.Active == external && File.Exists(path + ".trial"), "Conflict overwrote external settings");
            });
            Test("Recovery reconciles both sides of candidate transitions and completed commit", directory =>
            {
                foreach (bool committed in new[] { false, true })
                foreach (bool applied in new[] { false, true })
                {
                    string path = Path.Combine(directory, "settings-" + committed + "-" + applied + ".json");
                    string baseline = "{\"v\":1}", previous = "{\"v\":2}", next = "{\"v\":3}", saved = "{\"v\":4}";
                    File.WriteAllText(path, committed ? next : saved);
                    File.WriteAllText(path + ".trial", Newtonsoft.Json.JsonConvert.SerializeObject(new {
                        LeaseId = "lease", Owner = "owner", Baseline = baseline, Previous = previous, Next = next,
                        SavedFingerprint = SettingsFingerprint.Compute(saved), Phase = committed ? "commit" : "preview"
                    }));
                    var driver = new Driver { Active = applied ? next : previous };
                    var engine = new SettingsEngine(driver, path);
                    if (committed && !applied)
                    {
                        Throws<IOException>(() => engine.Restore(true));
                        Check(driver.Active == previous && File.Exists(path + ".trial"), "Commit conflict not retained");
                    }
                    else
                    {
                        engine.Restore(true);
                        Check(driver.Active == (committed ? next : baseline) && !File.Exists(path + ".trial"), "Recovery phase was mishandled");
                    }
                }
            });
            Test("Trial requests enforce owner, idempotency and finite numeric input", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(path, baseline);
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path);
                var request = new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) };
                var begin = engine.TrialCommand(request, "owner");
                Check(engine.TrialCommand(request, "owner").LeaseId == begin.LeaseId && driver.Writes == 1, "Duplicate began twice");
                Throws<UnauthorizedAccessException>(() => engine.TrialCommand(new ServiceRequest { Command = "trial-query", RequestId = "q", LeaseId = begin.LeaseId }, "other"));
                Throws<InvalidDataException>(() => engine.TrialCommand(new ServiceRequest { Command = "trial-update", RequestId = "u", LeaseId = begin.LeaseId,
                    Settings = "{\"v\":\"NaN\"}", ExpectedFingerprint = SettingsFingerprint.Compute(candidate) }, "owner"));
                engine.TrialCommand(new ServiceRequest { Command = "trial-cancel", RequestId = "c", LeaseId = begin.LeaseId }, "owner");
            });
            Test("Failed update immediately restores baseline and cleans journal", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}", next = "{\"v\":3}";
                File.WriteAllText(path, baseline);
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path);
                var begin = engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                driver.FailNext = true;
                Throws<IOException>(() => engine.TrialCommand(new ServiceRequest { Command = "trial-update", RequestId = "u", LeaseId = begin.LeaseId,
                    ExpectedFingerprint = SettingsFingerprint.Compute(candidate), Settings = next }, "owner"));
                Check(driver.Active == baseline && !File.Exists(path + ".trial") && File.ReadAllText(path) == baseline, "Update failure did not restore baseline");
            });
            Test("Commit storage failure immediately restores baseline", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(path, baseline);
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path);
                var begin = engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                Directory.CreateDirectory(path + ".lastgood");
                Throws<IOException>(() => engine.TrialCommand(new ServiceRequest { Command = "trial-commit", RequestId = "c", LeaseId = begin.LeaseId }, "owner"));
                Check(driver.Active == baseline && File.ReadAllText(path) == baseline && !File.Exists(path + ".trial"), "Commit failure did not restore baseline");
            });
            Test("Deterministic lease expiration restores baseline", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(path, baseline);
                DateTime now = DateTime.UtcNow;
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path, () => now);
                engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                now = now.AddSeconds(31); engine.ExpireTrial();
                Check(driver.Active == baseline && !File.Exists(path + ".trial"), "Expired lease did not restore baseline");
                Check(Newtonsoft.Json.JsonConvert.DeserializeObject<ServiceResponse>("{\"Success\":true}").TrialProtocolVersion == 0, "Old service falsely advertised trial capability");
            });
            Test("Startup and resume do not interrupt a live calibration", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(path, baseline);
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path);
                var begin = engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                engine.Restore(true);
                engine.Restore(false);
                Check(driver.Active == candidate && File.ReadAllText(path) == baseline && File.Exists(path + ".trial"),
                    "Restore interrupted a live trial or changed startup settings");
                engine.TrialCommand(new ServiceRequest { Command = "trial-cancel", RequestId = "c", LeaseId = begin.LeaseId }, "owner");
                Check(driver.Active == baseline, "Trial could not be cancelled after restore");
            });
            Test("Authenticated pipe trial begins and cancels", directory =>
            {
                string pipeName = "RawAccel.TrialTest." + Guid.NewGuid();
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(path, baseline);
                var driver = new Driver { Active = baseline };
                using (var service = new RawAccelService(new SettingsEngine(driver, path), pipeName, directory))
                {
                    typeof(RawAccelService).GetMethod("OnStart", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, new object[] { new string[0] });
                    try
                    {
                        var begin = SendRequest(pipeName, new ServiceRequest { Command = "trial-begin", RequestId = "begin", Settings = candidate,
                            ExpectedFingerprint = SettingsFingerprint.Compute(baseline) });
                        Check(begin.Success && begin.TrialProtocolVersion == 1, "Pipe trial begin failed: " + begin.Message);
                        var cancel = SendRequest(pipeName, new ServiceRequest { Command = "trial-cancel", RequestId = "cancel", LeaseId = begin.LeaseId });
                        Check(cancel.Success && driver.Active == baseline, "Pipe trial cancel failed: " + cancel.Message);
                    }
                    finally { typeof(RawAccelService).GetMethod("OnStop", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, new object[0]); }
                }
            });
            Test("Failed renewal keeps original deadline and timer survives early callback", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(path, baseline);
                DateTime now = DateTime.UtcNow;
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path, () => now);
                var begin = engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                now = now.AddSeconds(15);
                engine.ProcessTrialTimer(); // A callback before the deadline must leave a timer armed.
                using (var blocked = new FileStream(path + ".trial.pending", FileMode.Create, FileAccess.Write, FileShare.None))
                    Throws<IOException>(() => engine.TrialCommand(new ServiceRequest { Command = "trial-renew", RequestId = "r", LeaseId = begin.LeaseId }, "owner"));
                now = now.AddSeconds(16);
                engine.ProcessTrialTimer();
                Check(driver.Active == baseline && !File.Exists(path + ".trial"), "Failed renewal extended the old deadline");
            });
            Test("Expiry retries transient rollback failure without overwriting external settings", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(path, baseline);
                DateTime now = DateTime.UtcNow;
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path, () => now);
                engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                now = now.AddSeconds(31); driver.FailNext = true;
                engine.ProcessTrialTimer();
                Check(driver.Active == candidate && File.Exists(path + ".trial") && engine.Status.Contains("retrying"), "Recovery failure was hidden");
                engine.ProcessTrialTimer();
                Check(driver.Active == baseline && !File.Exists(path + ".trial"), "Transient rollback was not retried");
            });
            Test("Commit receipt resolves lost acknowledgement after restart", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(path, baseline);
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path);
                var begin = engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                var commit = new ServiceRequest { Command = "trial-commit", RequestId = "c", LeaseId = begin.LeaseId };
                engine.TrialCommand(commit, "owner");
                Check(File.Exists(path + ".trial.receipt") && !File.Exists(path + ".trial"), "No durable receipt");
                var restarted = new SettingsEngine(driver, path);
                var repeated = restarted.TrialCommand(commit, "owner");
                Check(repeated.TrialState == "committed" && repeated.ActiveSettings == candidate, "Lost commit could not be resolved");
                var query = restarted.TrialCommand(new ServiceRequest { Command = "trial-query", RequestId = "q", LeaseId = begin.LeaseId }, "owner");
                Check(query.TrialState == "committed", "Receipt query failed");
                Throws<UnauthorizedAccessException>(() => restarted.TrialCommand(new ServiceRequest { Command = "trial-query", RequestId = "x", LeaseId = begin.LeaseId }, "other"));
                driver.Active = baseline;
                Throws<IOException>(() => restarted.TrialCommand(new ServiceRequest { Command = "trial-query", RequestId = "q2", LeaseId = begin.LeaseId }, "owner"));
            });
            Test("Recovery materializes receipt after save but before acknowledgement", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                var commit = new ServiceRequest { Command = "trial-commit", RequestId = "c", LeaseId = "lease" };
                File.WriteAllText(path, candidate);
                File.WriteAllText(path + ".trial", Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    LeaseId = "lease", Owner = "owner", Baseline = baseline, Previous = baseline, Next = candidate,
                    SavedFingerprint = SettingsFingerprint.Compute(baseline), Phase = "commit",
                    LimitUtc = DateTime.UtcNow.AddMinutes(5), CommitRequestId = "c", CommitRequestContent = Newtonsoft.Json.JsonConvert.SerializeObject(commit)
                }));
                var driver = new Driver { Active = candidate };
                var engine = new SettingsEngine(driver, path);
                engine.Restore(true);
                Check(File.Exists(path + ".trial.receipt") && !File.Exists(path + ".trial"), "Recovery did not preserve receipt");
                Check(new SettingsEngine(driver, path).TrialCommand(commit, "owner").TrialState == "committed", "Recovered receipt cannot resolve commit");
            });
            Test("Monotonic lease expires despite wall clock moving backwards", directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(path, baseline);
                DateTime now = DateTime.UtcNow; long ticks = 100;
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path, () => now, () => ticks);
                engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                now = now.AddHours(-1); ticks += 31 * Stopwatch.Frequency;
                engine.ProcessTrialTimer();
                Check(driver.Active == baseline && !File.Exists(path + ".trial"), "Clock rollback extended the trial");
            });
            Test("Explicit recovery resolves reboot conflict only with owner and current fingerprint", directory =>
            {
                string path = Path.Combine(directory, "settings.json"), journalBase = Path.Combine(directory, "journal", "session");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}", reset = "{\"v\":3}", changed = "{\"v\":4}";
                File.WriteAllText(path, baseline);
                var driver = new Driver { Active = baseline };
                var engine = new SettingsEngine(driver, path, journalBase: journalBase);
                var begin = engine.TrialCommand(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                    ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, "owner");
                driver.Active = reset; File.WriteAllText(path, changed);
                var restarted = new SettingsEngine(driver, path, journalBase: journalBase);
                Throws<IOException>(() => restarted.PrepareStandalone());
                Throws<UnauthorizedAccessException>(() => restarted.TrialCommand(new ServiceRequest { Command = "trial-recovery-query", RequestId = "x" }, "other"));
                var pending = restarted.TrialCommand(new ServiceRequest { Command = "trial-recovery-query", RequestId = "q" }, "owner");
                Check(pending.TrialState == "recovery-pending" && pending.LeaseId == begin.LeaseId && pending.ActiveSettings == reset && pending.RecoverySavedChanged, "Recovery query incorrect");
                Throws<IOException>(() => restarted.TrialCommand(new ServiceRequest { Command = "trial-recover", RequestId = "r", LeaseId = begin.LeaseId,
                    ExpectedFingerprint = SettingsFingerprint.Compute(candidate) }, "owner"));
                var restored = restarted.TrialCommand(new ServiceRequest { Command = "trial-recover", RequestId = "r2", LeaseId = begin.LeaseId,
                    ExpectedFingerprint = SettingsFingerprint.Compute(reset) }, "owner");
                Check(restored.TrialState == "recovered" && driver.Active == baseline && File.ReadAllText(path) == changed && !File.Exists(journalBase + ".trial"), "Explicit recovery failed or changed startup file");
                var retryEngine = new SettingsEngine(driver, path, journalBase: journalBase);
                var retry = new ServiceRequest { Command = "trial-recover", RequestId = "r2", LeaseId = begin.LeaseId,
                    ExpectedFingerprint = SettingsFingerprint.Compute(reset) };
                Check(retryEngine.TrialCommand(retry, "owner").TrialState == "recovered", "Lost recovery response could not be retried after restart");
                retry.ExpectedFingerprint = SettingsFingerprint.Compute(baseline);
                Throws<InvalidDataException>(() => retryEngine.TrialCommand(retry, "owner"));
                Check(restarted.TrialCommand(new ServiceRequest { Command = "trial-recovery-query", RequestId = "q2" }, "owner").TrialState == "idle", "Recovery did not clear journal");
            });
            Test("Standalone subprocess recovers after client stops renewing", directory =>
            {
                string path = Path.Combine(directory, "settings.json"), state = Path.Combine(directory, "driver.json"), journal = Path.Combine(directory, "journal", "session");
                string pipe = "RawAccel.HostTest." + Guid.NewGuid().ToString("N"), mutex = "Local\\RawAccel.HostTest." + Guid.NewGuid().ToString("N");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(path, baseline); File.WriteAllText(state, baseline);
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string command = "--fake-standalone-host \"" + path + "\" \"" + pipe + "\" \"" + mutex + "\" \"" + state + "\" \"" + journal + "\"";
                using (var child = Process.Start(new ProcessStartInfo(exe, command) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    try
                    {
                        var status = SendRequest(pipe, new ServiceRequest { Command = "status" });
                        Check(status.Success && status.SettingsApplied && status.StorageIdentity == ServiceProtocol.PortableIdentity(path), "Host readiness failed");
                        var begin = SendRequest(pipe, new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                            ExpectedFingerprint = SettingsFingerprint.Compute(baseline) });
                        Check(begin.Success && File.ReadAllText(state) == candidate && File.ReadAllText(path) == baseline, "Host preview changed portable file");
                        Check(child.WaitForExit(10000), "Host did not exit after client stopped renewing");
                        Check(child.ExitCode == 0 && File.ReadAllText(state) == baseline && !File.Exists(journal + ".trial"), "Host did not restore expired trial");
                    }
                    finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
                }
            });
            Test("Standalone release refuses active trial, preserves commit and permits another portable path", directory =>
            {
                string firstPath = Path.Combine(directory, "first.json"), secondPath = Path.Combine(directory, "second.json");
                string state = Path.Combine(directory, "driver.json"), firstJournal = Path.Combine(directory, "journal", "first"), secondJournal = Path.Combine(directory, "journal", "second");
                string pipe = "RawAccel.HostTest." + Guid.NewGuid().ToString("N"), mutex = "Local\\RawAccel.HostTest." + Guid.NewGuid().ToString("N");
                string baseline = "{\"v\":1}", candidate = "{\"v\":2}";
                File.WriteAllText(firstPath, baseline); File.WriteAllText(secondPath, baseline); File.WriteAllText(state, baseline);
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                Func<string, string, Process> launch = (path, journal) => Process.Start(new ProcessStartInfo(exe,
                    "--fake-standalone-host \"" + path + "\" \"" + pipe + "\" \"" + mutex + "\" \"" + state + "\" \"" + journal + "\"")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                using (var first = launch(firstPath, firstJournal))
                {
                    try
                    {
                        Check(ServiceClient.SendAsync(new ServiceRequest { Command = "status" }, pipe).GetAwaiter().GetResult().TrialState == "idle", "Host not idle");
                        var begin = ServiceClient.SendAsync(new ServiceRequest { Command = "trial-begin", RequestId = "b", Settings = candidate,
                            ExpectedFingerprint = SettingsFingerprint.Compute(baseline) }, pipe).GetAwaiter().GetResult();
                        Check(begin.Success, "Begin failed");
                        Check(!ServiceClient.SendAsync(new ServiceRequest { Command = "host-release" }, pipe).GetAwaiter().GetResult().Success, "Active lease released host");
                        Check(ServiceClient.SendAsync(new ServiceRequest { Command = "trial-commit", RequestId = "c", LeaseId = begin.LeaseId }, pipe).GetAwaiter().GetResult().Success, "Commit failed");
                        Check(ServiceClient.SendAsync(new ServiceRequest { Command = "host-release" }, pipe).GetAwaiter().GetResult().Success, "Idle release failed");
                        Check(first.WaitForExit(5000) && File.ReadAllText(state) == candidate && File.ReadAllText(firstPath) == candidate, "Release reset committed driver state");
                    }
                    finally { if (!first.HasExited) { first.Kill(); first.WaitForExit(); } }
                }
                using (var second = launch(secondPath, secondJournal))
                {
                    try
                    {
                        var status = ServiceClient.SendAsync(new ServiceRequest { Command = "status" }, pipe).GetAwaiter().GetResult();
                        Check(status.Success && status.StorageIdentity == ServiceProtocol.PortableIdentity(secondPath), "Second portable path failed to acquire authority");
                        Check(ServiceClient.SendAsync(new ServiceRequest { Command = "host-release" }, pipe).GetAwaiter().GetResult().Success && second.WaitForExit(5000), "Second host failed to release");
                    }
                    finally { if (!second.HasExited) { second.Kill(); second.WaitForExit(); } }
                }
            });
            Console.WriteLine(passed + " tests passed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static ServiceResponse SendRequest(string pipeName, ServiceRequest request)
    {
        using (var pipe = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadWrite,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None))
        using (var timeout = new CancellationTokenSource(5000))
        {
            pipe.Connect(2000);
            ServiceProtocol.WriteAsync(pipe, request, timeout.Token).GetAwaiter().GetResult();
            return ServiceProtocol.ReadAsync<ServiceResponse>(pipe, timeout.Token).GetAwaiter().GetResult();
        }
    }

    private static ServiceResponse Send(string pipeName, string command, string settings)
    {
        using (var pipe = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadWrite,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None))
        using (var timeout = new CancellationTokenSource(5000))
        using (timeout.Token.Register(() => pipe.Dispose()))
        {
            pipe.Connect(2000);
            ServiceProtocol.WriteAsync(pipe, new ServiceRequest { Command = command, Settings = settings },
                timeout.Token).GetAwaiter().GetResult();
            return ServiceProtocol.ReadAsync<ServiceResponse>(pipe, timeout.Token).GetAwaiter().GetResult();
        }
    }
}
