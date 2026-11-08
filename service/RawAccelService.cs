using RawAccel.ServiceSupport;
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace RawAccel.Service
{
    internal sealed class RawAccelService : ServiceBase
    {
        private readonly SettingsEngine engine;
        private readonly string pipeName;
        private readonly string logDirectory;
        private readonly object logGate = new object();
        private CancellationTokenSource shutdown;
        private Task listener;
        private Task restore;
        private int restoring;

        public RawAccelService() : this(new SettingsEngine(new AccelerationDriver(), ServiceProtocol.SettingsPath),
            ServiceProtocol.PipeName, ServiceProtocol.DataDirectory) { }

        internal RawAccelService(SettingsEngine engine, string pipeName, string logDirectory)
        {
            this.engine = engine;
            this.pipeName = pipeName;
            this.logDirectory = logDirectory;
            ServiceName = ServiceProtocol.ServiceName;
            CanStop = true;
            CanShutdown = true;
            CanHandlePowerEvent = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            shutdown = new CancellationTokenSource();
            listener = Task.Run(() => ListenAsync(shutdown.Token));
            QueueRestore(true);
            Log("Service started.");
        }

        private void QueueRestore(bool startup)
        {
            if (shutdown.IsCancellationRequested || Interlocked.CompareExchange(ref restoring, 1, 0) != 0) return;
            restore = Task.Run(async () =>
            {
                try
                {
                    for (int attempt = 0; attempt < 30 && !shutdown.IsCancellationRequested; attempt++)
                    {
                        try { engine.Restore(startup); Log(engine.Status); return; }
                        catch (Exception e)
                        {
                            engine.ReportFailure("Could not restore settings: " + e.Message);
                            if (attempt == 0 || attempt == 29) Log(engine.Status);
                            // Invalid files need user correction; only driver availability is retried.
                            if (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException) return;
                        }
                        await Task.Delay(2000, shutdown.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                finally { Interlocked.Exchange(ref restoring, 0); }
            });
        }

        protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
        {
            if (powerStatus == PowerBroadcastStatus.ResumeAutomatic ||
                powerStatus == PowerBroadcastStatus.ResumeSuspend) QueueRestore(false);
            return true;
        }

        internal static NamedPipeServerStream CreatePipe(string name)
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            // No network callers, even if Windows file sharing is enabled.
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (var sid in new[] { WellKnownSidType.LocalServiceSid, WellKnownSidType.LocalSystemSid,
                WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        }

        private async Task ListenAsync(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = CreatePipe(pipeName))
                    using (cancellation.Register(() => pipe.Dispose()))
                    {
                        await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                        // An abandoned or partial request cannot hold up other clients indefinitely.
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                        using (timeout.Token.Register(() => pipe.Dispose()))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(8));
                            ServiceResponse response;
                            try
                            {
                                var request = await ServiceProtocol.ReadAsync<ServiceRequest>(pipe, timeout.Token)
                                    .ConfigureAwait(false);
                                switch (request.Command)
                                {
                                    case "status":
                                        response = engine.GetStatus();
                                        break;
                                    case "apply":
                                        if (string.IsNullOrWhiteSpace(request.Settings))
                                            throw new InvalidDataException("No settings supplied.");
                                        engine.ApplyAndSave(request.Settings);
                                        Log(engine.Status);
                                        response = new ServiceResponse { Success = true, Message = engine.Status };
                                        break;
                                    case "trial-begin":
                                    case "trial-update":
                                    case "trial-renew":
                                    case "trial-query":
                                    case "trial-cancel":
                                    case "trial-commit":
                                    case "trial-restore":
                                    case "trial-recovery-query":
                                    case "trial-recover":
                                        string owner = null;
                                        pipe.RunAsClient(() => { using (var identity = WindowsIdentity.GetCurrent())
                                            owner = identity.User == null ? null : identity.User.Value; });
                                        response = engine.TrialCommand(request, owner);
                                        break;
                                    default: throw new InvalidDataException("Unknown service command.");
                                }
                            }
                            catch (Exception e)
                            {
                                Log("Request failed: " + e.Message);
                                response = new ServiceResponse { Success = false, Message = e.Message };
                            }
                            await ServiceProtocol.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception e)
                {
                    if (cancellation.IsCancellationRequested) break;
                    Log("Service connection failed: " + e.Message);
                    try { await Task.Delay(250, cancellation).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        protected override void OnStop()
        {
            shutdown.Cancel();
            Task.WaitAll(new[] { listener, restore ?? Task.CompletedTask }, TimeSpan.FromSeconds(12));
            try { engine.EndTrialForShutdown(); }
            catch (Exception e) { engine.ReportFailure("Calibration recovery failed: " + e.Message); Log(engine.Status); }
            Log("Service stopped. Applied driver settings remain active until reset or reboot.");
        }

        protected override void OnShutdown() { OnStop(); }

        private void Log(string message)
        {
            try
            {
                lock (logGate)
                {
                    string path = Path.Combine(logDirectory, "service.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    {
                        if (File.Exists(path + ".old")) File.Delete(path + ".old");
                        File.Move(path, path + ".old");
                    }
                    File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
