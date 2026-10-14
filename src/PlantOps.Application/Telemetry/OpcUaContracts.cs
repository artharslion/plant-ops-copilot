using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PlantOps.Application.Telemetry;

public sealed record OpcUaConnectionInput(string EndpointReference, int PublishingIntervalMs = 1000);

public sealed record OpcUaMappingInput(string NamespaceUri, string IdentifierType, string Identifier,
    int SamplingIntervalMs = 1000, int QueueSize = 10, string SamplingMode = "Periodic");

public static class OpcUaConfigurationPolicy
{
    public static void Validate(OpcUaConnectionInput connection)
    {
        if (string.IsNullOrEmpty(connection.EndpointReference) || connection.EndpointReference.Length > 64
            || connection.EndpointReference.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_'))
            || connection.PublishingIntervalMs is < 1000 or > 60000)
        {
            throw new ArgumentException("Invalid approved endpoint reference or publishing interval.");
        }
    }

    public static void Validate(OpcUaMappingInput mapping)
    {
        if (string.IsNullOrEmpty(mapping.NamespaceUri) || mapping.NamespaceUri.Length > 512 || mapping.NamespaceUri.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(mapping.NamespaceUri, UriKind.Absolute, out _)
            || string.IsNullOrEmpty(mapping.Identifier) || mapping.Identifier.Length > 512 || mapping.Identifier.Any(char.IsControl)
            || mapping.SamplingIntervalMs is < 1000 or > 60000 || mapping.QueueSize is < 1 or > 100
            || mapping.SamplingMode is not ("Periodic" or "Changes"))
        {
            throw new ArgumentException("Invalid OPC UA mapping or requested sampling parameters.");
        }
        if (mapping.IdentifierType == "Numeric")
        {
            if (!uint.TryParse(mapping.Identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var identifier)
                || identifier.ToString(CultureInfo.InvariantCulture) != mapping.Identifier)
            {
                throw new ArgumentException("Numeric identifiers must use canonical unsigned decimal form.");
            }
        }
        else if (mapping.IdentifierType == "Guid")
        {
            if (!Guid.TryParseExact(mapping.Identifier, "D", out var identifier)
                || identifier.ToString("D") != mapping.Identifier)
            {
                throw new ArgumentException("Guid identifiers must use canonical lowercase D form.");
            }
        }
        else if (mapping.IdentifierType != "String")
        {
            throw new ArgumentException("Only Numeric, String and Guid identifiers are supported; Opaque is rejected.");
        }
    }

    public static string Key(OpcUaMappingInput mapping)
    {
        Validate(mapping);
        // Length-prefixed JSON identity avoids separator collisions in string identifiers.
        var identity = System.Text.Json.JsonSerializer.Serialize(new[] { mapping.NamespaceUri, mapping.IdentifierType, mapping.Identifier });
        return "opcua:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static bool ValidConversion(string rawUnit, string canonicalUnit, double scale, double offset) =>
        (rawUnit, canonicalUnit, scale, offset) switch
        {
            ("mm/s", "mm/s", 1, 0) or ("m/s", "m/s", 1, 0) or ("degC", "degC", 1, 0) or ("K", "K", 1, 0) => true,
            ("mm/s", "m/s", 0.001, 0) or ("m/s", "mm/s", 1000, 0) => true,
            ("degC", "K", 1, 273.15) or ("K", "degC", 1, -273.15) => true,
            _ => false
        };
}
