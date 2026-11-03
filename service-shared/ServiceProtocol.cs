using Newtonsoft.Json;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RawAccel.ServiceSupport
{
    public sealed class ServiceRequest
    {
        public string Command { get; set; }
        public string Settings { get; set; }
    }

    public sealed class ServiceResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public static class ServiceProtocol
    {
        public const string ServiceName = "RawAccelService";
        public const string PipeName = "RawAccel.Settings.v1";
        public const int MaximumMessageBytes = 1024 * 1024;
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
