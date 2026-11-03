using System;
using System.IO;
using System.Text;

namespace RawAccel.Service
{
    public interface IAccelerationDriver
    {
        string Normalize(string json);
        string ReadActive();
        void Apply(string json);
    }

    // All writes (boot, GUI and resume) share this lock. No mouse input runs through the service.
    public sealed class SettingsEngine
    {
        private readonly object gate = new object();
        private readonly IAccelerationDriver driver;
        private readonly string path;
        private bool userApplied;
        private string status = "Waiting to restore startup settings.";

        public SettingsEngine(IAccelerationDriver driver, string path)
        {
            this.driver = driver;
            this.path = path;
        }

        public string Status { get { lock (gate) return status; } }

        public void ReportFailure(string message)
        {
            lock (gate) status = message;
        }

        public void Restore(bool startup)
        {
            lock (gate)
            {
                // A GUI Apply while the driver was starting must win over the boot retry.
                if (startup && userApplied) return;
                string json;
                bool backup = false;
                try { json = ReadValidated(path); }
                catch (Exception e) when (e is InvalidDataException || e is IOException)
                {
                    if (!File.Exists(path + ".lastgood")) throw;
                    json = ReadValidated(path + ".lastgood");
                    backup = true;
                }
                driver.Apply(json);
                status = backup ? "Restored backup settings; save current settings to repair startup settings."
                    : "Saved settings applied at " + DateTime.Now.ToString("g") + ".";
            }
        }

        public void ApplyAndSave(string json)
        {
            lock (gate)
            {
                string normalized = driver.Normalize(json);
                if (Encoding.UTF8.GetByteCount(normalized) > ServiceSupport.ServiceProtocol.MaximumMessageBytes)
                    throw new InvalidDataException("Settings are too large.");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string staging = path + ".pending";
                try
                {
                    // Fail on storage errors before changing the live mouse settings.
                    using (var file = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] bytes = new UTF8Encoding(false).GetBytes(normalized);
                        file.Write(bytes, 0, bytes.Length);
                        file.Flush(true);
                    }
                    string previous = driver.ReadActive();
                    driver.Apply(normalized);
                    try
                    {
                        if (File.Exists(path)) File.Replace(staging, path, path + ".lastgood");
                        else File.Move(staging, path);
                    }
                    catch (Exception saveError)
                    {
                        try { driver.Apply(previous); }
                        catch (Exception rollbackError)
                        {
                            throw new IOException("Settings could not be saved or rolled back. " +
                                "Live settings may differ from startup settings: " + rollbackError.Message, saveError);
                        }
                        throw new IOException("Settings could not be saved. Previous live settings restored.", saveError);
                    }
                    userApplied = true;
                    status = "Settings applied and saved at " + DateTime.Now.ToString("g") + ".";
                }
                finally
                {
                    if (File.Exists(staging)) File.Delete(staging);
                }
            }
        }

        private string ReadValidated(string file)
        {
            if (new FileInfo(file).Length > ServiceSupport.ServiceProtocol.MaximumMessageBytes)
                throw new InvalidDataException("Startup settings are too large.");
            return driver.Normalize(File.ReadAllText(file));
        }
    }
}
