using Opc.Ua;
using PlantOps.Integrations.OpcUa;
using Xunit;

namespace PlantOps.Tests;

public sealed class OpcUaMetadataTests
{
    [Fact]
    public void ValidScalarUsesUnitIdentityInsteadOfDisplayText()
    {
        var metadata = Valid() with
        {
            EngineeringUnits = new EUInformation { NamespaceUri = OpcUaMetadataValidator.UnitNamespace, UnitId = 4403510, DisplayName = "misleading" }
        };
        Assert.True(OpcUaMetadataValidator.Validate("valid", metadata, "mm/s", "m/s", 0.001, 0).Accepted);
    }

    [Theory]
    [InlineData("missing", "NodeMissing")]
    [InlineData("object", "NotVariable")]
    [InlineData("array", "NotScalar")]
    [InlineData("access", "NotReadable")]
    [InlineData("user", "UserNotReadable")]
    [InlineData("type", "UnsupportedDataType")]
    [InlineData("unitMissing", "EngineeringUnitsMissing")]
    [InlineData("unitUnknown", "EngineeringUnitsUnknown")]
    [InlineData("unitConflict", "EngineeringUnitsMismatch")]
    [InlineData("uncertain", "MetadataUnreadable")]
    public void InvalidMetadataReturnsSpecificMappingError(string scenario, string error)
    {
        var metadata = scenario switch
        {
            "missing" => Valid() with { Status = StatusCodes.BadNodeIdUnknown },
            "object" => Valid() with { NodeClass = NodeClass.Object },
            "array" => Valid() with { ValueRank = ValueRanks.OneDimension },
            "access" => Valid() with { AccessLevel = AccessLevels.None },
            "user" => Valid() with { UserAccessLevel = AccessLevels.None },
            "type" => Valid() with { DataType = DataTypeIds.String },
            "unitMissing" => Valid() with { EngineeringUnits = null },
            "unitUnknown" => Valid() with { EngineeringUnits = new EUInformation { NamespaceUri = "urn:unknown", UnitId = 4403510 } },
            "unitConflict" => Valid() with { EngineeringUnits = new EUInformation { NamespaceUri = OpcUaMetadataValidator.UnitNamespace, UnitId = 4408652 } },
            _ => Valid() with { Status = StatusCodes.Uncertain }
        };
        var result = OpcUaMetadataValidator.Validate(scenario, metadata, "mm/s", "mm/s", 1, 0);
        Assert.False(result.Accepted);
        Assert.Equal(scenario, result.MappingKey);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void ConversionAndAbstractTypesFailClosed()
    {
        Assert.Equal("InvalidConversion", OpcUaMetadataValidator.Validate("scale", Valid(), "mm/s", "mm/s", 2, 0).Error);
        Assert.Equal("UnsupportedDataType", OpcUaMetadataValidator.Validate("abstract", Valid() with
        {
            DataType = DataTypeIds.Number
        }, "mm/s", "mm/s", 1, 0).Error);
        Assert.Equal("NotScalar", OpcUaMetadataValidator.Validate("any", Valid() with
        {
            ValueRank = ValueRanks.Any
        }, "mm/s", "mm/s", 1, 0).Error);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(7u)]
    [InlineData(8u)]
    [InlineData(9u)]
    [InlineData(10u)]
    [InlineData(11u)]
    public void ConcreteNumericTypesAreAllowed(uint identifier)
        => Assert.True(OpcUaMetadataValidator.Validate("numeric", Valid() with
        {
            DataType = new NodeId(identifier)
        }, "mm/s", "mm/s", 1, 0).Accepted);

    [Theory]
    [InlineData(4403510, "mm/s")]
    [InlineData(5067859, "m/s")]
    [InlineData(4408652, "degC")]
    [InlineData(4932940, "K")]
    public void KnownUnitIdentitiesPermitOnlyMatchingRawUnit(int unitId, string rawUnit)
    {
        var metadata = Valid() with
        {
            EngineeringUnits = new EUInformation { NamespaceUri = OpcUaMetadataValidator.UnitNamespace, UnitId = unitId }
        };
        Assert.True(OpcUaMetadataValidator.Validate("unit", metadata, rawUnit, rawUnit, 1, 0).Accepted);
    }

    [Fact]
    public void FamiliarTypeAndUnitIdentifiersInOtherNamespacesAreRejected()
    {
        Assert.Equal("UnsupportedDataType", OpcUaMetadataValidator.Validate("custom", Valid() with
        {
            DataType = new NodeId(11u, 2)
        }, "mm/s", "mm/s", 1, 0).Error);
        Assert.Equal("EngineeringUnitsUnknown", OpcUaMetadataValidator.Validate("unknown", Valid() with
        {
            EngineeringUnits = new EUInformation { NamespaceUri = OpcUaMetadataValidator.UnitNamespace, UnitId = -1, DisplayName = "mm/s" }
        }, "mm/s", "mm/s", 1, 0).Error);
    }

    private static OpcUaNodeMetadata Valid() => new(StatusCodes.Good, NodeClass.Variable, ValueRanks.Scalar,
        AccessLevels.CurrentRead, AccessLevels.CurrentRead, DataTypeIds.Double,
        new EUInformation { NamespaceUri = OpcUaMetadataValidator.UnitNamespace, UnitId = 4403510 });
}
