using Newtonsoft.Json;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace RawAccel.ServiceSupport
{
    public sealed class ServiceRequest
    {
        public string Command { get; set; }
        public string Settings { get; set; }
        public string LeaseId { get; set; }
        public string RequestId { get; set; }
        public string ExpectedFingerprint { get; set; }
    }

    public sealed class ServiceResponse
    {
        public bool Success { get; set; }
        public bool SettingsApplied { get; set; }
        public string Message { get; set; }
        public int TrialProtocolVersion { get; set; }
        public string LeaseId { get; set; }
        public string ActiveSettings { get; set; }
        public string BaselineSettings { get; set; }
        public string TrialState { get; set; }
        public string LeaseExpiresUtc { get; set; }
        public string StorageIdentity { get; set; }
        public bool RecoverySavedChanged { get; set; }
    }

    public static class SettingsFingerprint
    {
        private static JToken Canonical(JToken token)
        {
            if (token is JObject obj)
                return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => new JProperty(p.Name, Canonical(p.Value))));
            if (token is JArray array) return new JArray(array.Select(Canonical));
            if (token.Type == JTokenType.Float)
            {
                double value = token.Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value))
                    throw new InvalidDataException("Settings contain a non-finite number.");
            }
            if (token.Type == JTokenType.String &&
                (token.Value<string>() == "NaN" || token.Value<string>() == "Infinity" || token.Value<string>() == "-Infinity"))
                throw new InvalidDataException("Settings contain a non-finite number.");
            return token.DeepClone();
        }

        public static string Compute(string json)
        {
            string canonical = Canonical(JToken.Parse(json)).ToString(Formatting.None);
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
        }
    }

    public static class ServiceProtocol
    {
        public const string ServiceName = "RawAccelService";
        public const string PipeName = "RawAccel.Settings.v1";
        public const int MaximumMessageBytes = 1024 * 1024;
        public static string TextFingerprint(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        public static string PortableIdentity(string path)
        {
            return TextFingerprint(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant());
        }
        public static string StandalonePipeName(string sid)
        {
            return "RawAccel.Calibration." + TextFingerprint(sid).Substring(0, 32);
        }
        public static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RawAccel");
        public static readonly string SettingsPath = Path.Combine(DataDirectory, "settings.json");

        // Length-prefixed UTF-8: reject oversized messages before allocating their buffers.
        public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellation)
        {
            byte[] data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
            if (data.Length > MaximumMessageBytes) throw new InvalidDataException("Settings are too large.");
            byte[] header = BitConverter.GetBytes(data.Length);
            await stream.WriteAsync(header, 0, header.Length, cancellation).ConfigureAwait(false);
            await stream.WriteAsync(data, 0, data.Length, cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }

        public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellation)
        {
            byte[] header = new byte[4];
            await ReadExactlyAsync(stream, header, cancellation).ConfigureAwait(false);
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaximumMessageBytes)
                throw new InvalidDataException("Invalid service message size.");
            byte[] data = new byte[length];
            await ReadExactlyAsync(stream, data, cancellation).ConfigureAwait(false);
            T result = JsonConvert.DeserializeObject<T>(new UTF8Encoding(false, true).GetString(data));
            if (result == null) throw new InvalidDataException("Empty service message.");
            return result;
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellation)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellation)
                    .ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Service connection closed early.");
                offset += count;
            }
        }
    }
}
