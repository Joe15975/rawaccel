using System;
using System.IO;
using System.Threading.Tasks;
using RawAccel.ServiceSupport;

namespace grapher.Models.Calibration
{
    // Both hosts use the same trial engine/protocol. Disposing a dialog must never kill recovery.
    public sealed class CalibrationTrialClient : IDisposable
    {
        private readonly string pipeName;
        private readonly Func<ServiceRequest, Task<ServiceResponse>> sender;
        private bool disposed;
        public bool IsStandalone { get; private set; }

        private CalibrationTrialClient(string pipeName, bool standalone)
        {
            this.pipeName = pipeName;
            IsStandalone = standalone;
        }

        public CalibrationTrialClient(Func<ServiceRequest, Task<ServiceResponse>> sender, bool standalone)
        {
            this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
            IsStandalone = standalone;
        }

        public static async Task<CalibrationTrialClient> ConnectAsync(string portableSettingsPath)
        {
            bool standalone = !ServiceClient.IsEnabled;
            string pipe = standalone ? await ServiceClient.StartStandaloneAsync(Path.GetFullPath(portableSettingsPath)) : ServiceProtocol.PipeName;
            var client = new CalibrationTrialClient(pipe, standalone);
            var status = await client.SendAsync(new ServiceRequest { Command = "status" });
            if (!status.Success || status.TrialProtocolVersion < 1)
            {
                client.Dispose();
                throw new InvalidOperationException("The settings host does not support calibration. Update the background service or use the matching portable build.");
            }
            // A reachable host may need the user's explicit recovery choice before any new trial.
            return client;
        }

        public async Task<ServiceResponse> SendAsync(ServiceRequest request)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CalibrationTrialClient));
            try { return await (sender != null ? sender(request) : ServiceClient.SendAsync(request, pipeName)); }
            catch (ApplicationException error) when (IsStandalone)
            { throw new ApplicationException("The calibration host is unavailable. Reopen calibration to inspect recovery before another settings change.", error); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (IsStandalone && pipeName != null)
            {
                // The host refuses release while any trial/recovery is pending; it retains its timer.
                Task.Run(async () =>
                {
                    try { await ServiceClient.SendAsync(new ServiceRequest { Command = "host-release" }, pipeName).ConfigureAwait(false); }
                    catch (ApplicationException) { }
                });
            }
        }
    }
}
