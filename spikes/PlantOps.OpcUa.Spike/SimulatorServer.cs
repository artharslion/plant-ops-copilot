using Opc.Ua;
using Opc.Ua.Server;

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
                variable.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, variable.NodeId));
                AddPredefinedNode(SystemContext, variable);
                variables.Add(variable);
            }
            timer = new Timer(_ => UpdateValues(), null, 1000, 1000);
        }
    }

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
