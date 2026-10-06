using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace PlantOps.OpcUa.Spike;

internal sealed class SpikeTelemetry() : TelemetryContextBase(NullLoggerFactory.Instance);

internal static class Program
{
    internal const string Endpoint = "opc.tcp://localhost:48480/plantops";
    internal const string NamespaceUri = "urn:plant-ops:simulator";

    public static async Task Main(string[] args)
    {
        var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/p2/spike");
        Directory.CreateDirectory(root);
        var telemetry = new SpikeTelemetry();
        var serverApplication = await ConfigureAsync("Server", root, telemetry);
        var options = args.Length > 2
            ? JsonSerializer.Deserialize<SimulatorOptions>(await File.ReadAllTextAsync(args[2])) ?? new()
            : new SimulatorOptions();
        using var server = new SimulatorServer(options);
        await serverApplication.StartAsync(server);
        if (args.FirstOrDefault() == "server")
        {
            Console.WriteLine("Isolated simulator listening on port 48480; terminate this process to stop.");
            await Task.Delay(Timeout.Infinite);
            return;
        }

        var clientApplication = await ConfigureAsync("Client", root, telemetry);
        var serverCertificate = await serverApplication.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.FindAsync(false);
        var clientCertificate = await clientApplication.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.FindAsync(false);
        // Explicit trust of only these two generated experiment identities; never an acceptance callback.
        using (var trusted = serverApplication.ApplicationConfiguration.SecurityConfiguration.TrustedPeerCertificates.OpenStore(telemetry))
        {
            await trusted.AddAsync(clientCertificate);
        }
        await serverApplication.ApplicationConfiguration.CertificateValidator.UpdateAsync(serverApplication.ApplicationConfiguration.SecurityConfiguration);
        var rejected = false;
        try
        {
            using var denied = await ConnectAsync(clientApplication.ApplicationConfiguration, telemetry);
        }
        catch (ServiceResultException exception) when (exception.StatusCode == StatusCodes.BadCertificateUntrusted)
        {
            rejected = true;
        }
        if (!rejected)
        {
            throw new InvalidOperationException("Fresh experiment must reject the untrusted server. Use a NEW output directory.");
        }
        using (var trusted = clientApplication.ApplicationConfiguration.SecurityConfiguration.TrustedPeerCertificates.OpenStore(telemetry))
        {
            await trusted.AddAsync(serverCertificate);
        }
        await clientApplication.ApplicationConfiguration.CertificateValidator.UpdateAsync(clientApplication.ApplicationConfiguration.SecurityConfiguration);
        using var session = await ConnectAsync(clientApplication.ApplicationConfiguration, telemetry);
        var namespaceIndex = session.NamespaceUris.GetIndex(NamespaceUri);
        if (namespaceIndex < 0)
        {
            throw new InvalidOperationException("Simulator Namespace URI missing.");
        }
        var nodeId = new NodeId("North.Utilities.P101.VibrationRms", (ushort)namespaceIndex);
        var browse = await session.BrowseAsync(null, null, 50, new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = ObjectIds.ObjectsFolder,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                IncludeSubtypes = true,
                NodeClassMask = (uint)NodeClass.Variable,
                ResultMask = (uint)BrowseResultMask.All
            }
        }, CancellationToken.None);
        var browseCount = browse.Results[0].References.Count;
        if (browse.Results[0].ContinuationPoint is { Length: > 0 } continuation)
        {
            await session.BrowseNextAsync(null, true, new ByteStringCollection { continuation }, CancellationToken.None);
        }
        if (browseCount is < 1 or > 50)
        {
            throw new InvalidOperationException("Bounded Browse failed.");
        }
        var first = await session.ReadValueAsync(nodeId);
        var notifications = new List<DataValue>();
        var subscription = new Subscription(session.DefaultSubscription)
        {
            PublishingInterval = 1000,
            KeepAliveCount = 3,
            LifetimeCount = 30,
            PublishingEnabled = true
        };
        var item = new MonitoredItem(subscription.DefaultItem)
        {
            StartNodeId = nodeId,
            AttributeId = Attributes.Value,
            SamplingInterval = 1000,
            QueueSize = 10,
            DiscardOldest = true,
            Filter = new DataChangeFilter { Trigger = DataChangeTrigger.StatusValueTimestamp }
        };
        item.Notification += (_, _) =>
        {
            lock (notifications)
            {
                notifications.AddRange(item.DequeueValues());
            }
        };
        subscription.AddItem(item);
        session.AddSubscription(subscription);
        await subscription.CreateAsync();
        await Task.Delay(TimeSpan.FromSeconds(8));
        DataValue[] values;
        lock (notifications)
        {
            values = notifications.ToArray();
        }
        if (values.Length < 5 || values.Select(x => x.SourceTimestamp).Distinct().Count() < 5)
        {
            throw new InvalidOperationException("StatusValueTimestamp did not deliver periodic constant-value observations.");
        }
        var report = new
        {
            CheckedAt = DateTimeOffset.UtcNow,
            SdkVersion = "1.5.378.182",
            UntrustedRejected = rejected,
            Policy = session.ConfiguredEndpoint.Description.SecurityPolicyUri,
            Mode = session.ConfiguredEndpoint.Description.SecurityMode.ToString(),
            ServerThumbprint = serverCertificate.Thumbprint,
            ClientThumbprint = clientCertificate.Thumbprint,
            NamespaceUri,
            NamespaceIndex = namespaceIndex,
            BrowseCount = browseCount,
            FirstQuality = first.StatusCode.ToString(),
            Notifications = values.Length,
            DistinctSourceTimes = values.Select(x => x.SourceTimestamp).Distinct().Count(),
            ConstantValue = values.All(x => Equals(x.Value, values[0].Value)),
            RevisedPublishingMs = subscription.CurrentPublishingInterval,
            RevisedSamplingMs = item.Status.SamplingInterval,
            RevisedQueueSize = item.Status.QueueSize,
            Scope = "8 second isolated protocol spike; no database or application collection"
        };
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        await subscription.DeleteAsync(true);
        await session.CloseAsync();
        await server.StopAsync();
        Console.WriteLine("Secure read-only subscription spike passed; public certificate fingerprints and results saved.");
    }

    private static async Task<ApplicationInstance> ConfigureAsync(string role, string root, ITelemetryContext telemetry)
    {
        var application = new ApplicationInstance(telemetry) { ApplicationName = "PlantOpsSpike" + role };
        var builder = application.Build("urn:localhost:plantops:spike:" + role.ToLowerInvariant(), "urn:plantops:spike");
        var certificates = new CertificateIdentifierCollection
        {
            new CertificateIdentifier
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(root, role, "own"),
                SubjectName = "CN=PlantOpsSpike" + role + ", DC=localhost",
                CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType
            }
        };
        IApplicationConfigurationBuilderSecurityOptions security;
        if (role == "Server")
        {
            security = builder.AsServer([Endpoint])
                .AddPolicy(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256)
                .AddUserTokenPolicy(UserTokenType.Anonymous)
                .AddSecurityConfiguration(certificates, Path.Combine(root, role));
        }
        else
        {
            security = builder.AsClient().AddSecurityConfiguration(certificates, Path.Combine(root, role));
        }
        await security.SetAutoAcceptUntrustedCertificates(false).SetRejectSHA1SignedCertificates(true).CreateAsync();
        if (!await application.CheckApplicationInstanceCertificatesAsync(false))
        {
            throw new InvalidOperationException("Application certificate unavailable.");
        }
        return application;
    }

    private static async Task<ISession> ConnectAsync(ApplicationConfiguration configuration, ITelemetryContext telemetry)
    {
        var description = await CoreClientUtils.SelectEndpointAsync(configuration, Endpoint, true, telemetry)
            ?? throw new InvalidOperationException("No secure endpoint.");
        if (description.SecurityPolicyUri != SecurityPolicies.Basic256Sha256 || description.SecurityMode != MessageSecurityMode.SignAndEncrypt)
        {
            throw new InvalidOperationException("Unexpected security endpoint.");
        }
        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(configuration));
        return await new DefaultSessionFactory(telemetry).CreateAsync(configuration, endpoint, false, true,
            "PlantOpsReadOnlySpike", 30000, new UserIdentity(new AnonymousIdentityToken()), null);
    }
}
