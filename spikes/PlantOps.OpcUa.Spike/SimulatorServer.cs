using Opc.Ua;
using Opc.Ua.Server;
using PlantOps.Integrations.OpcUa;

namespace PlantOps.OpcUa.Spike;

internal sealed record SimulatorOptions(bool ReorderNamespaces = false, bool Constant = true,
    string Quality = "Good", string Timestamp = "Normal", bool Overflow = false, bool DenyRead = false,
    int MinimumSamplingMs = 1000);

internal sealed class SimulatorServer(SimulatorOptions? options = null) : StandardServer
{
    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        => new(server, configuration, null, new SimulatorNodes(server, configuration, options ?? new()));

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "PlantOps fictional experiment",
        ProductName = "PlantOps read-only simulator",
        ProductUri = "urn:plantops:spike",
        SoftwareVersion = "1.0.0",
        BuildNumber = "1",
        BuildDate = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc)
    };
}

// Original business nodes; no reference-server NodeManager code is copied.
internal sealed class SimulatorNodes(IServerInternal server, ApplicationConfiguration configuration, SimulatorOptions options)
    : CustomNodeManager2(server, configuration, options.ReorderNamespaces
        ? ["urn:plant-ops:padding", Program.NamespaceUri] : [Program.NamespaceUri])
{
    private Timer? timer;

    private readonly List<BaseDataVariableState> variables = [];

    private readonly DateTime startedAt = DateTime.UtcNow;

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            for (var index = 0; index < 100; index++)
            {
                var identifier = index == 0 ? "North.Utilities.P101.VibrationRms" : $"{(index < 50 ? "North" : "South")}.Point{index:D3}.VibrationRms";
                var indexOfNamespace = Server.NamespaceUris.GetIndexOrAppend(Program.NamespaceUri);
                var variable = new BaseDataVariableState(null)
                {
                    NodeId = new NodeId(identifier, indexOfNamespace),
                    BrowseName = new QualifiedName(identifier, indexOfNamespace),
                    DisplayName = identifier,
                    TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                    DataType = DataTypeIds.Double,
                    ValueRank = ValueRanks.Scalar,
                    AccessLevel = options.DenyRead ? AccessLevels.None : AccessLevels.CurrentRead,
                    UserAccessLevel = options.DenyRead ? AccessLevels.None : AccessLevels.CurrentRead,
                    MinimumSamplingInterval = options.MinimumSamplingMs,
                    Value = 4.0,
                    StatusCode = StatusCodes.Good,
                    Timestamp = DateTime.UtcNow
                };
                AddUnits(variable, 4403510);
                Register(variable, externalReferences);
                variables.Add(variable);
            }
            CreateMetadataFixtures(externalReferences);
            timer = new Timer(_ => UpdateValues(), null, 1000, 1000);
        }
    }

    private static void AddUnits(BaseDataVariableState variable, int unitId, string? unitNamespace = null)
    {
        var units = new PropertyState<EUInformation>(variable)
        {
            NodeId = new NodeId(variable.NodeId.Identifier + ".EngineeringUnits", variable.NodeId.NamespaceIndex),
            BrowseName = new QualifiedName("EngineeringUnits", 0),
            DisplayName = "EngineeringUnits",
            TypeDefinitionId = VariableTypeIds.PropertyType,
            ReferenceTypeId = ReferenceTypeIds.HasProperty,
            DataType = DataTypeIds.EUInformation,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = new EUInformation
            {
                NamespaceUri = unitNamespace ?? OpcUaMetadataValidator.UnitNamespace,
                UnitId = unitId,
                DisplayName = "mm/s",
                Description = "Simulator engineering unit"
            }
        };
        variable.AddChild(units);
    }

    private void Register(NodeState node, IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        node.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
        if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
        {
            externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
        }
        references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, node.NodeId));
        AddPredefinedNode(SystemContext, node);
    }

    private void CreateMetadataFixtures(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        var ns = Server.NamespaceUris.GetIndexOrAppend(Program.NamespaceUri);
        Register(new BaseObjectState(null)
        {
            NodeId = new NodeId("Metadata.Object", ns),
            BrowseName = new QualifiedName("Metadata.Object", ns),
            DisplayName = "Metadata.Object",
            TypeDefinitionId = ObjectTypeIds.BaseObjectType
        }, externalReferences);
        foreach (var name in new[] { "Array", "Access", "UserAccess", "ValueDenied", "SensorBad", "String", "UnitsMissing", "UnitsUnknown", "UnitsConflict", "UnitsMalformed", "UnitsUnreadable" })
        {
            var variable = new BaseDataVariableState(null)
            {
                NodeId = new NodeId("Metadata." + name, ns),
                BrowseName = new QualifiedName("Metadata." + name, ns),
                DisplayName = "Metadata." + name,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                DataType = name == "String" ? DataTypeIds.String : DataTypeIds.Double,
                ValueRank = name == "Array" ? ValueRanks.OneDimension : ValueRanks.Scalar,
                AccessLevel = name == "Access" ? AccessLevels.None : AccessLevels.CurrentRead,
                UserAccessLevel = name is "Access" or "UserAccess" ? AccessLevels.None : AccessLevels.CurrentRead,
                Value = name == "String" ? "4.0" : name == "Array" ? new[] { 4.0 } : (object)4.0,
                StatusCode = name == "SensorBad" ? StatusCodes.BadSensorFailure : StatusCodes.Good,
                Timestamp = DateTime.UtcNow
            };
            if (name != "UnitsMissing")
            {
                AddUnits(variable, name == "UnitsConflict" ? 4408652 : 4403510, name == "UnitsUnknown" ? "urn:unknown" : null);
            }
            if (name == "UnitsMalformed")
            {
                var property = (PropertyState<EUInformation>)variable.FindChild(SystemContext, new QualifiedName("EngineeringUnits", 0));
                property.Value = null!;
            }
            if (name == "ValueDenied")
            {
                variable.OnReadValue = DenyValue;
            }
            if (name == "UnitsUnreadable")
            {
                var property = (PropertyState<EUInformation>)variable.FindChild(SystemContext, new QualifiedName("EngineeringUnits", 0));
                property.OnReadValue = DenyValue;
            }
            Register(variable, externalReferences);
        }
    }

    private static ServiceResult DenyValue(ISystemContext context, NodeState node, NumericRange range,
        QualifiedName encoding, ref object value, ref StatusCode status, ref DateTime timestamp)
        => StatusCodes.BadUserAccessDenied;

    private void UpdateValues()
    {
        lock (Lock)
        {
            foreach (var variable in variables)
            {
                var second = (int)(DateTime.UtcNow - startedAt).TotalSeconds % 120;
                variable.Value = options.Constant ? 4.0 : second < 10 ? 4.0 : second < 30 ? 12.6 : 7.0;
                variable.StatusCode = options.Quality switch
                {
                    "Bad" => StatusCodes.BadSensorFailure,
                    "Uncertain" => StatusCodes.UncertainSensorNotAccurate,
                    "Cycle" => second is >= 40 and < 50 ? StatusCodes.BadSensorFailure
                        : second is >= 50 and < 60 ? StatusCodes.UncertainSensorNotAccurate : StatusCodes.Good,
                    _ => StatusCodes.Good
                };
                if (options.Overflow)
                {
                    variable.StatusCode = new StatusCode(variable.StatusCode.Code | 0x480);
                }
                variable.Timestamp = options.Timestamp switch
                {
                    "Missing" => DateTime.MinValue,
                    "Future" => DateTime.UtcNow.AddMinutes(1),
                    "Frozen" => startedAt,
                    _ => DateTime.UtcNow
                };
                variable.ClearChangeMasks(SystemContext, false);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
