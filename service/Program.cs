using RawAccel.ServiceSupport;
using System;
using System.IO;
using System.ServiceProcess;
using System.Text;

namespace RawAccel.Service
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--export")
                {
                    string json = new AccelerationDriver().ReadActive();
                    File.WriteAllText(args[1], json, new UTF8Encoding(false));
                    Console.WriteLine("Exported the settings currently active in the Raw Accel driver.");
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--validate")
                {
                    if (new FileInfo(args[1]).Length > ServiceProtocol.MaximumMessageBytes)
                        throw new InvalidDataException("Settings are too large.");
                    new AccelerationDriver().Normalize(File.ReadAllText(args[1]));
                    return 0;
                }
                if (args.Length == 1 && args[0] == "--status")
                {
                    Console.WriteLine(ServiceClient.WindowsStatus());
                    if (ServiceClient.IsEnabled) Console.WriteLine(ServiceClient.Send("status").Message);
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--apply")
                {
                    var response = ServiceClient.Send("apply", File.ReadAllText(args[1]));
                    Console.WriteLine(response.Message);
                    return response.Success ? 0 : 1;
                }
                if (args.Length == 0 && !Environment.UserInteractive)
                {
                    ServiceBase.Run(new RawAccelService());
                    return 0;
                }
                Console.WriteLine("RawAccelService --export <settings.json> | --validate <settings.json> | --status | --apply <settings.json>\n" +
                    "Install using Install-Service.ps1 from an administrator PowerShell.");
                return args.Length == 0 ? 0 : 1;
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
    }
}
