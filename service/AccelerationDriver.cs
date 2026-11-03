using Newtonsoft.Json;
using System.IO;

namespace RawAccel.Service
{
    internal sealed class AccelerationDriver : IAccelerationDriver
    {
        public string Normalize(string json)
        {
            try
            {
                var result = DriverConfig.Convert(json);
                if (result.Item2 != null) throw new InvalidDataException(result.Item2);
                return result.Item1.ToJSON();
            }
            catch (JsonException e) { throw new InvalidDataException("Invalid settings JSON: " + e.Message, e); }
        }

        public string ReadActive()
        {
            VersionHelper.ValidOrThrow();
            return DriverConfig.GetActive().ToJSON();
        }

        public void Apply(string json)
        {
            VersionHelper.ValidOrThrow();
            var result = DriverConfig.Convert(json);
            if (result.Item2 != null) throw new InvalidDataException(result.Item2);
            result.Item1.Activate();
        }
    }
}
