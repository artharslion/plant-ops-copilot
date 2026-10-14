using System.Globalization;
using Opc.Ua;
using PlantOps.Application.Telemetry;

namespace PlantOps.Integrations.OpcUa;

public static class OpcUaNodeIdentity
{
    public static NodeId Resolve(OpcUaMappingInput mapping, NamespaceTable namespaces)
    {
        OpcUaConfigurationPolicy.Validate(mapping);
        var index = namespaces.GetIndex(mapping.NamespaceUri);
        if (index < 0)
        {
            throw new ArgumentException("Configured Namespace URI is absent from this session.");
        }
        return mapping.IdentifierType switch
        {
            "String" => new NodeId(mapping.Identifier, (ushort)index),
            "Numeric" => new NodeId(uint.Parse(mapping.Identifier, CultureInfo.InvariantCulture), (ushort)index),
            "Guid" => new NodeId(Guid.ParseExact(mapping.Identifier, "D"), (ushort)index),
            _ => throw new ArgumentException("Unsupported identifier type.")
        };
    }
}
