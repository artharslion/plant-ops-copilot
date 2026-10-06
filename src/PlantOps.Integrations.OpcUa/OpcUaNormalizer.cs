using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Opc.Ua;
using PlantOps.Application.Telemetry;

namespace PlantOps.Integrations.OpcUa;

public static class OpcUaNormalizer
{
    public static NormalizedSample Normalize(SourceConfiguration configuration, DataValue observation,
        DateTimeOffset receivedAt, Guid envelopeId)
    {
        if (configuration.SourceKind != "OpcUa" || envelopeId == Guid.Empty
            || !OpcUaConfigurationPolicy.ValidConversion(configuration.RawUnit, configuration.CanonicalUnit, configuration.Scale, configuration.Offset))
        {
            throw new ArgumentException("Invalid OPC UA configuration or envelope identity.");
        }
        var raw = observation.Value;
        var type = raw?.GetType().Name ?? "Null";
        var numeric = Number(raw);
        var status = observation.StatusCode.Code;
        var quality = StatusCode.IsBad(status) ? "Bad" : StatusCode.IsUncertain(status) ? "Uncertain" : "Good";
        var symbol = observation.StatusCode.ToString();
        var value = numeric * configuration.Scale + configuration.Offset;
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            quality = "Bad";
            value = null;
            symbol = "UnsupportedOrInvalidNumericValue";
        }
        if (quality == "Bad")
        {
            value = null;
        }
        var rawText = raw switch
        {
            null => null,
            IFormattable number when raw is byte or sbyte or short or ushort or int or uint or long or ulong or float or double
                => number.ToString(null, CultureInfo.InvariantCulture),
            string text => text.Length > 512 ? text[..512] : text,
            _ => "Rejected:" + type
        };
        var rawJson = JsonSerializer.Serialize(new
        {
            type,
            value = rawText
        });
        var sourceAt = Timestamp(observation.SourceTimestamp);
        var serverAt = Timestamp(observation.ServerTimestamp);
        var trustedAt = sourceAt ?? serverAt;
        var identity = JsonSerializer.Serialize(new
        {
            configuration.MappingId,
            configuration.MappingVersion,
            Timestamp = trustedAt,
            Envelope = trustedAt.HasValue ? (Guid?)null : envelopeId,
            Raw = rawJson,
            Status = status
        });
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new(configuration, value, quality, rawJson, status, symbol, sourceAt, serverAt, receivedAt.ToUniversalTime(), key);
    }

    private static DateTimeOffset? Timestamp(DateTime timestamp) => timestamp <= DateTime.FromFileTimeUtc(0)
        ? null : new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));

    private static double? Number(object? value) => value switch
    {
        byte number => number,
        sbyte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number when number is >= -9007199254740992 and <= 9007199254740992 => number,
        ulong number when number <= 9007199254740992 => number,
        float number when float.IsFinite(number) => number,
        double number when double.IsFinite(number) => number,
        _ => null
    };
}
