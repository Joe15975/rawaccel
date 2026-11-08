using RawAccel.ServiceSupport;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace grapher.Models
{
    internal static class BackgroundServiceMenu
    {
        public static ToolStripMenuItem Create(Form owner)
        {
            var menu = new ToolStripMenuItem("&Background");
            var startup = new ToolStripMenuItem("Start with Windows");
            var status = new ToolStripMenuItem("Service status");
            var restart = new ToolStripMenuItem("Restart service");
            var save = new ToolStripMenuItem("Save current settings for startup");
            menu.DropDownItems.AddRange(new ToolStripItem[] { startup, status, restart, save });
            menu.DropDownOpening += (s, e) =>
            {
                startup.Checked = ServiceClient.IsEnabled;
                restart.Enabled = startup.Checked;
                save.Enabled = startup.Checked;
            };
            startup.Click += async (s, e) =>
            {
                menu.Enabled = false;
                owner.Enabled = false;
                string snapshot = null;
                try
                {
                    bool disable = ServiceClient.IsEnabled;
                    string script = Path.Combine(Application.StartupPath, "Install-Service.ps1");
                    if (!File.Exists(script)) throw new FileNotFoundException(
                        "Install-Service.ps1 is missing. Use the complete build output.");
                    string arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"";
                    if (disable) arguments += " -Disable";
                    else
                    {
                        snapshot = Path.GetTempFileName();
                        File.WriteAllText(snapshot, DriverConfig.GetActive().ToJSON());
                        arguments += " -SettingsPath \"" + snapshot + "\"";
                    }
                    int exit = await RunElevatedInstallerAsync(arguments);
                    if (exit != 0) throw new ApplicationException(
                        "Service setup failed. Run Install-Service.ps1 in an administrator PowerShell for details.");
                    MessageBox.Show(owner, disable ? "Start with Windows disabled. Your current mouse settings remain active."
                        : "Start with Windows enabled. Your current settings will load automatically.", "Raw Accel");
                }
                catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Raw Accel service"); }
                finally
                {
                    if (snapshot != null) File.Delete(snapshot);
                    menu.Enabled = true;
                    owner.Enabled = true;
                }
            };
            status.Click += async (s, e) =>
            {
                try
                {
                    string message = ServiceClient.WindowsStatus();
                    if (ServiceClient.IsEnabled)
                    {
                        var response = await ServiceClient.SendAsync("status");
                        message += Environment.NewLine + response.Message;
                    }
                    MessageBox.Show(owner, message, "Raw Accel service");
                }
                catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Raw Accel service"); }
            };
            restart.Click += async (s, e) =>
            {
                menu.Enabled = false;
                owner.Enabled = false;
                try
                {
                    string command = "try { $ErrorActionPreference = 'Stop'; " +
                        "Restart-Service -Name '" + ServiceProtocol.ServiceName + "'; " +
                        "(Get-Service -Name '" + ServiceProtocol.ServiceName + "').WaitForStatus('Running', " +
                        "[TimeSpan]::FromSeconds(20)); exit 0 } catch { exit 1 }";
                    var info = new ProcessStartInfo(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                            @"WindowsPowerShell\v1.0\powershell.exe"),
                        "-NoProfile -Command \"" + command + "\"")
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (var process = Process.Start(info))
                    {
                        await Task.Run(() => process.WaitForExit());
                        if (process.ExitCode != 0) throw new ApplicationException(
                            "Could not restart the service. Check Windows Services for details.");
                    }
                    MessageBox.Show(owner, "Service restarted. Check Service status to confirm settings were restored.",
                        "Raw Accel service");
                }
                catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Raw Accel service"); }
                finally { menu.Enabled = true; owner.Enabled = true; }
            };
            save.Click += async (s, e) =>
            {
                menu.Enabled = false;
                try
                {
                    var response = await ServiceClient.SendAsync("apply", DriverConfig.GetActive().ToJSON());
                    MessageBox.Show(owner, response.Message, "Raw Accel service");
                }
                catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Raw Accel service"); }
                finally { menu.Enabled = true; }
            };
            return menu;
        }

        // Runs a PowerShell command line with a UAC elevation prompt and returns its exit code.
        // Shared by the Background menu and the automatic first-run service setup.
        public static async Task<int> RunElevatedInstallerAsync(string arguments)
        {
            var info = new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"), arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var process = Process.Start(info))
            {
                await Task.Run(() => process.WaitForExit());
                return process.ExitCode;
            }
        }
    }
}
