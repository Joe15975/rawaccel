using RawAccel.ServiceSupport;
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace RawAccel.Service
{
    // On-demand per-user host. Input remains in the driver; only configuration goes over this pipe.
    internal static class StandaloneTrialHost
    {
        internal static int Run(string portablePath)
        {
            string sid;
            using (var identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
            string absolute = Path.GetFullPath(portablePath);
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RawAccel", "Calibration");
            string journalBase = Path.Combine(local, ServiceProtocol.PortableIdentity(absolute));
            var engine = new SettingsEngine(new AccelerationDriver(), absolute, journalBase: journalBase);
            return Run(engine, absolute, ServiceProtocol.StandalonePipeName(sid), sid,
                @"Global\RawAccel.Calibration.Host", TimeSpan.FromMinutes(10), true);
        }

        internal static int Run(SettingsEngine engine, string portablePath, string pipeName, string sid,
            string mutexName, TimeSpan idleTimeout, bool enforceServiceDisabled)
        {
            using (var mutex = new Mutex(false, mutexName))
            {
                bool owns;
                try { owns = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns) return 1;
                try
                {
                    ListenAsync(engine, portablePath, pipeName, sid, idleTimeout, enforceServiceDisabled).GetAwaiter().GetResult();
                    return 0;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static NamedPipeServerStream Pipe(string name, string sid)
        {
            var acl = new PipeSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, acl);
        }

        private static async Task ListenAsync(SettingsEngine engine, string portablePath, string pipeName,
            string sid, TimeSpan idleTimeout, bool enforceServiceDisabled)
        {
            bool ready = false;
            string startupError = null;
            Action prepare = () =>
            {
                try
                {
                    if (enforceServiceDisabled && ServiceClient.IsEnabled)
                        throw new InvalidOperationException("The background service is enabled; use its calibration authority.");
                    engine.PrepareStandalone(); ready = true; startupError = null;
                }
                catch (Exception e) { ready = false; startupError = e.Message; }
            };
            prepare();
            try
            {
                while (true)
                {
                    using (var pipe = Pipe(pipeName, sid))
                    using (var idle = new CancellationTokenSource(idleTimeout))
                    using (idle.Token.Register(() => pipe.Dispose()))
                    {
                        try { await pipe.WaitForConnectionAsync(idle.Token).ConfigureAwait(false); }
                        catch (Exception e) when (e is OperationCanceledException || e is ObjectDisposedException) { break; }
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                        using (timeout.Token.Register(() => pipe.Dispose()))
                        {
                            ServiceResponse response;
                            bool release = false;
                            try
                            {
                                var request = await ServiceProtocol.ReadAsync<ServiceRequest>(pipe, timeout.Token).ConfigureAwait(false);
                                string caller = null;
                                pipe.RunAsClient(() => { using (var identity = WindowsIdentity.GetCurrent())
                                    caller = identity.User == null ? null : identity.User.Value; });
                                if (caller != sid) throw new UnauthorizedAccessException("Calibration host caller does not match its owner.");
                                if (request.Command == "status")
                                {
                                    if (!ready) prepare();
                                    response = ready ? engine.GetStatus() : new ServiceResponse { Success = true, SettingsApplied = false, Message = startupError, TrialProtocolVersion = 1 };
                                    response.StorageIdentity = ServiceProtocol.PortableIdentity(portablePath);
                                    response.TrialState = engine.HasPendingTrial ? "active" : "idle";
                                }
                                else if (request.Command == "host-release")
                                {
                                    release = !engine.HasPendingTrial;
                                    response = new ServiceResponse { Success = release, TrialProtocolVersion = 1,
                                        TrialState = release ? "idle" : "active",
                                        Message = release ? "Calibration host released." : "Calibration or recovery is still active.",
                                        StorageIdentity = ServiceProtocol.PortableIdentity(portablePath) };
                                }
                                else
                                {
                                    bool recovery = request.Command == "trial-recovery-query" || request.Command == "trial-recover";
                                    if (!ready && !recovery) throw new InvalidOperationException("Calibration recovery is unresolved: " + startupError);
                                    if (enforceServiceDisabled && ServiceClient.IsEnabled &&
                                        request.Command != "trial-cancel" && request.Command != "trial-query" && !recovery)
                                        throw new InvalidOperationException("The background service was enabled during calibration.");
                                    response = engine.TrialCommand(request, caller);
                                    if (request.Command == "trial-recover" && response.Success) prepare();
                                }
                            }
                            catch (Exception e) { response = new ServiceResponse { Success = false, Message = e.Message, TrialProtocolVersion = 1,
                                StorageIdentity = ServiceProtocol.PortableIdentity(portablePath) }; }
                            try { await ServiceProtocol.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false); }
                            catch (Exception e) when (e is IOException || e is OperationCanceledException || e is ObjectDisposedException) { }
                            if (release) break;
                        }
                    }
                }
            }
            finally { try { engine.EndTrialForShutdown(); } catch (Exception) { /* Journal remains for next launch. */ } }
        }
    }
}
