using Opc.Ua;
using PlantOps.Application.Telemetry;
using PlantOps.Integrations.OpcUa;
using Xunit;

namespace PlantOps.Tests;

public sealed class OpcUaMappingTests
{
    [Theory]
    [InlineData("Numeric", "12")]
    [InlineData("String", "North.Utilities.P101.VibrationRms")]
    [InlineData("Guid", "12345678-1234-1234-1234-123456789abc")]
    public void IdentityResolvesByUriAfterNamespaceReordering(string type, string identifier)
    {
        var mapping = new OpcUaMappingInput("urn:plant-ops:simulator", type, identifier);
        var first = new NamespaceTable();
        first.Append(mapping.NamespaceUri);
        var reordered = new NamespaceTable();
        reordered.Append("urn:other");
        reordered.Append(mapping.NamespaceUri);
        Assert.Equal((ushort)1, OpcUaNodeIdentity.Resolve(mapping, first).NamespaceIndex);
        Assert.Equal((ushort)2, OpcUaNodeIdentity.Resolve(mapping, reordered).NamespaceIndex);
        Assert.Equal(OpcUaNodeIdentity.Resolve(mapping, first).Identifier, OpcUaNodeIdentity.Resolve(mapping, reordered).Identifier);
        Assert.Throws<ArgumentException>(() => OpcUaNodeIdentity.Resolve(mapping, new NamespaceTable()));
    }

    [Theory]
    [InlineData("Opaque", "AQID")]
    [InlineData("Numeric", "012")]
    [InlineData("Numeric", "4294967296")]
    [InlineData("Numeric", "-1")]
    [InlineData("Guid", "not-a-guid")]
    [InlineData("String", "")]
    public void AmbiguousAndUnsupportedIdentifiersAreRejected(string type, string identifier)
        => Assert.Throws<ArgumentException>(() => OpcUaConfigurationPolicy.Validate(new OpcUaMappingInput("urn:plant-ops:simulator", type, identifier)));

    [Fact]
    public void ConversionRequiresExplicitKnownDimensionAndCoefficients()
    {
        Assert.True(OpcUaConfigurationPolicy.ValidConversion("m/s", "mm/s", 1000, 0));
        Assert.True(OpcUaConfigurationPolicy.ValidConversion("degC", "K", 1, 273.15));
        Assert.False(OpcUaConfigurationPolicy.ValidConversion("unknown", "mm/s", 1, 0));
        Assert.False(OpcUaConfigurationPolicy.ValidConversion("degC", "mm/s", 1, 0));
        Assert.False(OpcUaConfigurationPolicy.ValidConversion("mm/s", "mm/s", 2, 0));
    }
}
