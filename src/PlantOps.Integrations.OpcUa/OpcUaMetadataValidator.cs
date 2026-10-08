using Opc.Ua;
using PlantOps.Application.Telemetry;

namespace PlantOps.Integrations.OpcUa;

public sealed record OpcUaNodeMetadata(StatusCode Status, NodeClass NodeClass, int ValueRank,
    byte AccessLevel, byte UserAccessLevel, NodeId DataType, EUInformation? EngineeringUnits);

public sealed record OpcUaMappingValidation(string MappingKey, bool Accepted, string? Error);

public static class OpcUaMetadataValidator
{
    public const string UnitNamespace = "http://www.opcfoundation.org/UA/units/un/cefact";

    public static OpcUaMappingValidation Validate(string mappingKey, OpcUaNodeMetadata metadata,
        string rawUnit, string canonicalUnit, double scale, double offset)
    {
        string? error = null;
        if (metadata.Status == StatusCodes.BadNodeIdUnknown)
        {
            error = "NodeMissing";
        }
        else if (!StatusCode.IsGood(metadata.Status))
        {
            error = "MetadataUnreadable";
        }
        else if (metadata.NodeClass != NodeClass.Variable)
        {
            error = "NotVariable";
        }
        else if (metadata.ValueRank != ValueRanks.Scalar)
        {
            error = "NotScalar";
        }
        else if ((metadata.AccessLevel & AccessLevels.CurrentRead) == 0)
        {
            error = "NotReadable";
        }
        else if ((metadata.UserAccessLevel & AccessLevels.CurrentRead) == 0)
        {
            error = "UserNotReadable";
        }
        else if (metadata.DataType.NamespaceIndex != 0 || metadata.DataType.IdType != IdType.Numeric
            || metadata.DataType.Identifier is not uint type || type is < 2 or > 11)
        {
            // Abstract Number and custom subtypes require separate bounded hierarchy validation.
            error = "UnsupportedDataType";
        }
        else if (metadata.EngineeringUnits is null)
        {
            error = "EngineeringUnitsMissing";
        }
        else
        {
            var unit = metadata.EngineeringUnits.NamespaceUri == UnitNamespace
                ? metadata.EngineeringUnits.UnitId switch
                {
                    4403510 => "mm/s", // C16
                    5067859 => "m/s", // MTS
                    4408652 => "degC", // CEL
                    4932940 => "K", // KEL
                    _ => null
                } : null;
            error = unit is null ? "EngineeringUnitsUnknown"
                : unit != rawUnit ? "EngineeringUnitsMismatch"
                : !OpcUaConfigurationPolicy.ValidConversion(rawUnit, canonicalUnit, scale, offset) ? "InvalidConversion" : null;
        }
        return new(mappingKey, error is null, error);
    }
}
