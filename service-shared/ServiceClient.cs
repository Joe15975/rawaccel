using Microsoft.Win32;
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Reflection;

namespace RawAccel.ServiceSupport
{
    public static class ServiceClient
    {
        public static bool IsEnabled
        {
            get
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.ServiceName))
                    return key != null && Convert.ToInt32(key.GetValue("Start", 4)) != 4;
            }
        }

        public static string WindowsStatus()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.ServiceName))
            {
                if (key == null) return "Not installed";
                using (var controller = new ServiceController(ServiceProtocol.ServiceName))
                    return controller.Status + (IsEnabled ? " (startup enabled)" : " (startup disabled)");
            }
        }

        public static ServiceResponse Send(string command, string settings = null)
        {
            // Existing GUI callers are synchronous; keep async pipe continuations off the UI context.
            return Task.Run(() => SendAsync(command, settings)).GetAwaiter().GetResult();
        }

        public static ServiceResponse Send(ServiceRequest request)
        {
            return Task.Run(() => SendAsync(request)).GetAwaiter().GetResult();
        }

        public static async Task<ServiceResponse> SendAsync(string command, string settings = null)
        {
            return await SendAsync(new ServiceRequest { Command = command, Settings = settings }).ConfigureAwait(false);
        }

        public static async Task<ServiceResponse> SendAsync(ServiceRequest request)
        {
            return await SendAsync(request, ServiceProtocol.PipeName).ConfigureAwait(false);
        }

        public static async Task<ServiceResponse> SendAsync(ServiceRequest request, string pipeName)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (var pipe = new NamedPipeClientStream(".", pipeName,
                PipeAccessRights.ReadWrite, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification, HandleInheritability.None))
            using (timeout.Token.Register(() => pipe.Dispose()))
            {
                try
                {
                    await pipe.ConnectAsync(2000, timeout.Token).ConfigureAwait(false);
                    await ServiceProtocol.WriteAsync(pipe,
                        request, timeout.Token)
                        .ConfigureAwait(false);
                    return await ServiceProtocol.ReadAsync<ServiceResponse>(pipe, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException || e is System.TimeoutException ||
                    e is OperationCanceledException || e is ObjectDisposedException || e is UnauthorizedAccessException)
                {
                    throw new ApplicationException(
                        "Cannot reach the Raw Accel service. Check Background > Service status, " +
                        "or disable Start with Windows to use standalone mode.", e);
                }
            }
        }

        public static async Task<string> StartStandaloneAsync(string portableSettingsPath)
        {
            string absolute = Path.GetFullPath(portableSettingsPath);
            if (absolute.IndexOf('"') >= 0) throw new InvalidDataException("Invalid portable settings path.");
            string sid;
            using (var identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
            string pipeName = ServiceProtocol.StandalonePipeName(sid);
            string expected = ServiceProtocol.PortableIdentity(absolute);
            ServiceResponse existing = null;
            try { existing = await SendAsync(new ServiceRequest { Command = "status" }, pipeName).ConfigureAwait(false); }
            catch (ApplicationException) { }
            if (existing != null)
            {
                if (existing.StorageIdentity == expected)
                {
                    if (!existing.Success || existing.TrialProtocolVersion < 1) throw new InvalidOperationException(existing.Message);
                    return pipeName;
                }
                var released = await SendAsync(new ServiceRequest { Command = "host-release" }, pipeName).ConfigureAwait(false);
                if (!released.Success) throw new InvalidOperationException("Another portable calibration has an active trial or pending recovery. " + released.Message);
                bool closed = false;
                var closeDeadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < closeDeadline)
                {
                    try { await SendAsync(new ServiceRequest { Command = "status" }, pipeName).ConfigureAwait(false); }
                    catch (ApplicationException) { closed = true; break; }
                    await Task.Delay(100).ConfigureAwait(false);
                }
                if (!closed) throw new InvalidOperationException("The previous calibration host did not release its authority.");
            }
            string executable = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "RawAccelService.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("The calibration host executable is missing.", executable);
            Process.Start(new ProcessStartInfo(executable, "--calibration-host \"" + absolute + "\"")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            var until = DateTime.UtcNow.AddSeconds(15);
            Exception last = null;
            while (DateTime.UtcNow < until)
            {
                try
                {
                    var response = await SendAsync(new ServiceRequest { Command = "status" }, pipeName).ConfigureAwait(false);
                    if (response.StorageIdentity != expected) throw new InvalidOperationException("Another calibration host is using different portable settings.");
                    if (!response.Success || response.TrialProtocolVersion < 1) throw new InvalidOperationException(response.Message);
                    return pipeName;
                }
                catch (ApplicationException e) { last = e; }
                await Task.Delay(200).ConfigureAwait(false);
            }
            throw new ApplicationException("The standalone calibration host did not become ready.", last);
        }
    }
}
