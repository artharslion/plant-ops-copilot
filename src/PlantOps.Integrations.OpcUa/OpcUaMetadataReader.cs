using Opc.Ua;
using Opc.Ua.Client;
using PlantOps.Application.Telemetry;

namespace PlantOps.Integrations.OpcUa;

// A single exact configured node only. Caller must authorize the session and mapping;
// this is not the Worker Browse command or a source registration.
public static class OpcUaMetadataReader
{
    public static async Task<OpcUaMappingValidation> ValidateAsync(ISession session, string mappingKey,
        OpcUaMappingInput mapping, string rawUnit, string canonicalUnit, double scale, double offset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            OpcUaConfigurationPolicy.Validate(mapping);
        }
        catch (ArgumentException)
        {
            return new(mappingKey, false, "InvalidNodeIdentity");
        }
        if (session.NamespaceUris.GetIndex(mapping.NamespaceUri) < 0)
        {
            return new(mappingKey, false, "NamespaceMissing");
        }
        var node = OpcUaNodeIdentity.Resolve(mapping, session.NamespaceUris);
        var nodeClass = (await ReadAsync(session, node, [Attributes.NodeClass], cancellationToken))[0];
        if (nodeClass.StatusCode == StatusCodes.BadNodeIdUnknown)
        {
            return new(mappingKey, false, "NodeMissing");
        }
        if (!StatusCode.IsGood(nodeClass.StatusCode) || nodeClass.Value is not int classValue)
        {
            return new(mappingKey, false, "NodeClassUnreadable");
        }
        if ((NodeClass)classValue != NodeClass.Variable)
        {
            return new(mappingKey, false, "NotVariable");
        }
        var attributes = await ReadAsync(session, node,
            [Attributes.ValueRank, Attributes.AccessLevel, Attributes.UserAccessLevel, Attributes.DataType], cancellationToken);
        string[] names = ["ValueRank", "AccessLevel", "UserAccessLevel", "DataType"];
        for (var index = 0; index < attributes.Count; index++)
        {
            if (!StatusCode.IsGood(attributes[index].StatusCode))
            {
                return new(mappingKey, false, names[index] + "Unreadable");
            }
        }
        if (attributes[0].Value is not int rank || attributes[1].Value is not byte access
            || attributes[2].Value is not byte userAccess || attributes[3].Value is not NodeId dataType)
        {
            return new(mappingKey, false, "MetadataMalformed");
        }
        var metadata = new OpcUaNodeMetadata(StatusCodes.Good, NodeClass.Variable, rank, access, userAccess, dataType, null);
        var preliminary = OpcUaMetadataValidator.Validate(mappingKey, metadata, rawUnit, canonicalUnit, scale, offset);
        if (preliminary.Error != "EngineeringUnitsMissing")
        {
            return preliminary;
        }
        var path = new BrowsePath
        {
            StartingNode = node,
            RelativePath = new RelativePath
            {
                Elements = new RelativePathElementCollection
                {
                    new RelativePathElement
                    {
                        ReferenceTypeId = ReferenceTypeIds.HasProperty,
                        IncludeSubtypes = false,
                        IsInverse = false,
                        TargetName = new QualifiedName("EngineeringUnits", 0)
                    }
                }
            }
        };
        var translated = await session.TranslateBrowsePathsToNodeIdsAsync(null, new BrowsePathCollection { path }, cancellationToken);
        ClientBase.ValidateResponse(translated.Results, new BrowsePathCollection { path });
        var property = translated.Results[0];
        if (property.StatusCode == StatusCodes.BadNoMatch)
        {
            return new(mappingKey, false, "EngineeringUnitsMissing");
        }
        if (!StatusCode.IsGood(property.StatusCode) || property.Targets.Count != 1
            || property.Targets[0].RemainingPathIndex != uint.MaxValue || property.Targets[0].TargetId.IsAbsolute)
        {
            return new(mappingKey, false, "EngineeringUnitsUnresolved");
        }
        var unitNode = ExpandedNodeId.ToNodeId(property.Targets[0].TargetId, session.NamespaceUris);
        var unitValue = (await ReadAsync(session, unitNode, [Attributes.Value], cancellationToken))[0];
        if (!StatusCode.IsGood(unitValue.StatusCode))
        {
            return new(mappingKey, false, "EngineeringUnitsUnreadable");
        }
        var units = unitValue.Value as EUInformation ?? (unitValue.Value as ExtensionObject)?.Body as EUInformation;
        if (units is null)
        {
            return new(mappingKey, false, "EngineeringUnitsMalformed");
        }
        var validated = OpcUaMetadataValidator.Validate(mappingKey, metadata with
        {
            EngineeringUnits = units
        }, rawUnit, canonicalUnit, scale, offset);
        if (!validated.Accepted)
        {
            return validated;
        }
        // Access bits alone do not prove that the active identity can Read Value.
        var value = (await ReadAsync(session, node, [Attributes.Value], cancellationToken))[0];
        if (value.StatusCode == StatusCodes.BadNodeIdUnknown)
        {
            return new(mappingKey, false, "NodeMissing");
        }
        return value.StatusCode == StatusCodes.BadUserAccessDenied || value.StatusCode == StatusCodes.BadNotReadable
            || value.StatusCode == StatusCodes.BadAttributeIdInvalid
            ? new(mappingKey, false, "ValueNotReadable") : validated;
    }

    private static async Task<DataValueCollection> ReadAsync(ISession session, NodeId node, uint[] attributes, CancellationToken cancellationToken)
    {
        var requests = new ReadValueIdCollection(attributes.Select(attribute => new ReadValueId { NodeId = node, AttributeId = attribute }));
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, requests, cancellationToken);
        ClientBase.ValidateResponse(response.Results, requests);
        return response.Results;
    }
}
