using Microsoft.Win32;
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

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

        public static async Task<ServiceResponse> SendAsync(string command, string settings = null)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName,
                PipeAccessRights.ReadWrite, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification, HandleInheritability.None))
            using (timeout.Token.Register(() => pipe.Dispose()))
            {
                try
                {
                    await pipe.ConnectAsync(2000, timeout.Token).ConfigureAwait(false);
                    await ServiceProtocol.WriteAsync(pipe,
                        new ServiceRequest { Command = command, Settings = settings }, timeout.Token)
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
    }
}
