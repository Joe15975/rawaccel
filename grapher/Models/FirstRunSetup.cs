using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using grapher.Common;
using RawAccel.ServiceSupport;

namespace grapher.Models
{
    // First-launch setup: when the GUI starts, it brings the background service to the same
    // version as the build output beside the GUI, and offers the bundled driver installer
    // when the driver is missing or incompatible. No admin terminal required: elevation is
    // requested through UAC only when an install or update is actually needed.
    internal static class FirstRunSetup
    {
        private static readonly string[] ServicePayload = { "RawAccelService.exe", "RawAccelService.exe.config", "wrapper.dll", "Newtonsoft.Json.dll" };

        // Returns the driver version, or exits the application after guiding setup when the
        // driver is unavailable and cannot be installed from the build output.
        public static Version EnsureDriver()
        {
            try { return VersionHelper.ValidOrThrow(); }
            catch (Exception error)
            {
                string installer = Path.Combine(Application.StartupPath, "installer.exe");
                string message = "The Raw Accel driver is not installed or is incompatible with this version." + Environment.NewLine + error.Message;
                if (File.Exists(installer) &&
                    MessageBox.Show(message + Environment.NewLine + Environment.NewLine +
                        "Install the driver now? Administrator approval will be requested, and Windows must be restarted afterwards.",
                        "Raw Accel driver", MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
                {
                    try
                    {
                        var info = new ProcessStartInfo(installer) { UseShellExecute = true, Verb = "runas" };
                        using (var process = Process.Start(info))
                            Task.Run(() => process.WaitForExit()).Wait();
                        MessageBox.Show("Driver installer finished. Restart Windows to complete the installation, then open rawaccel again.",
                            "Raw Accel driver", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception installError)
                    {
                        MessageBox.Show("Driver installation failed: " + installError.Message, "Raw Accel driver", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                else
                {
                    MessageBox.Show(message + Environment.NewLine + Environment.NewLine +
                        "Install the matching official Raw Accel driver with installer.exe and restart Windows, then open rawaccel again.",
                        "Raw Accel driver", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Environment.Exit(1);
                return null; // Unreachable; satisfies the compiler.
            }
        }

        // Runs on the UI thread after the main window is shown. Installs or updates the
        // background service silently when the build output differs from the installed one.
        public static async Task EnsureServiceAsync(Form owner)
        {
            try
            {
                string startup = Application.StartupPath;
                string script = Path.Combine(startup, "Install-Service.ps1");
                if (!File.Exists(script)) return;
                foreach (string file in ServicePayload)
                    if (!File.Exists(Path.Combine(startup, file))) return;
                bool registered = ServiceRegistered();
                if (registered && !ServiceClient.IsEnabled) return; // Deliberately disabled by the user; respect that.
                if (registered && !ServiceStale()) return;          // Installed and identical to this build.
                string snapshot = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(snapshot, DriverConfig.GetActive().ToJSON());
                    int exit = await BackgroundServiceMenu.RunElevatedInstallerAsync("-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -SettingsPath \"" + snapshot + "\"");
                    if (exit != 0) throw new ApplicationException("The installer script exited with code " + exit + ".");
                    var until = DateTime.UtcNow.AddSeconds(15);
                    while (DateTime.UtcNow < until)
                    {
                        try
                        {
                            var response = await ServiceClient.SendAsync("status");
                            if (response.Success && response.SettingsApplied) return;
                        }
                        catch (ApplicationException) { }
                        await Task.Delay(500);
                    }
                    throw new ApplicationException("The service did not confirm it applied the saved settings.");
                }
                catch (Win32Exception e) when (e.NativeErrorCode == 1223)
                {
                    return; // The UAC prompt was declined; continue without the service.
                }
                catch (Exception error)
                {
                    MessageBox.Show(owner, "Automatic background service setup failed: " + error.Message +
                        Environment.NewLine + "Run Install-Service.ps1 in an administrator PowerShell for details.", "Raw Accel service");
                }
                finally { if (File.Exists(snapshot)) File.Delete(snapshot); }
            }
            catch (Exception)
            {
                // Setup must never block startup; failures were already reported above.
            }
        }

        private static bool ServiceRegistered()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.ServiceName))
                return key != null;
        }

        private static bool ServiceStale()
        {
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RawAccel", "Service");
            foreach (string file in ServicePayload)
            {
                string current = Path.Combine(installed, file);
                if (!File.Exists(current) || !File.Exists(Path.Combine(Application.StartupPath, file))) return true;
                if (Hash(current) != Hash(Path.Combine(Application.StartupPath, file))) return true;
            }
            return false;
        }

        private static string Hash(string path)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return Convert.ToBase64String(sha256.ComputeHash(stream));
        }
    }
}