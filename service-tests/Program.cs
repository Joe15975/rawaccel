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
        public string Normalize(string json)
        {
            if (json == "invalid") throw new InvalidDataException("Invalid profile");
            return json;
        }
        public string ReadActive() { return Active; }
        public void Apply(string json)
        {
            if (Fail) throw new ApplicationException("Driver not ready");
            Active = json;
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

    private static int Main()
    {
        try
        {
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
                        Check(Send(pipeName, "status", null).Success, "Listener stopped after a bad request");
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
            Console.WriteLine(passed + " tests passed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
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
