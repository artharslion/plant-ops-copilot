using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using PlantOps.Application.Telemetry;

namespace PlantOps.Integrations.OpcUa;

// Explicit finite local demo only; not registered as a general production source.
public sealed class OpcUaDemoSource(string endpointUrl, string certificateRoot,
    Func<SamplingRevision, CancellationToken, Task> opened,
    Func<DateTimeOffset, CancellationToken, Task> closed) : IMeasurementSource
{
    private sealed class DemoTelemetry() : TelemetryContextBase(NullLoggerFactory.Instance);

    public string Kind => "OpcUa";

    public static void ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "opc.tcp"
            || uri.Host is not ("localhost" or "127.0.0.1") || uri.Port < 1
            || uri.AbsolutePath is not ("" or "/") || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("The finite OPC UA demo requires an explicit loopback endpoint.");
        }
    }

    public async IAsyncEnumerable<NormalizedSample> ReadAsync(IReadOnlyList<SourceConfiguration> configurations,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateEndpoint(endpointUrl);
        if (configurations.Count != 1 || configurations[0].SourceKind != Kind)
        {
            throw new ArgumentException("The finite demo permits exactly one OPC UA point.");
        }
        var configuration = configurations[0];
        var mapping = JsonSerializer.Deserialize<OpcUaMappingInput>(configuration.SourceParameters)
            ?? throw new ArgumentException("Missing explicit node mapping.");
        if (mapping.NamespaceUri != "urn:industrial-sim:runtime" || mapping.IdentifierType != "String"
            || mapping.Identifier != "plantops-p101/vibration" || mapping.SamplingMode != "Periodic"
            || mapping.SamplingIntervalMs != 1000 || mapping.QueueSize != 10)
        {
            throw new ArgumentException("Only the approved P-101 1 Hz vibration demo node is supported.");
        }
        var telemetry = new DemoTelemetry();
        var application = new ApplicationInstance(telemetry) { ApplicationName = "PlantOpsLocalDemo" };
        await application.Build("urn:localhost:plantops:local-demo", "urn:plantops:local-demo").AsClient()
            .AddSecurityConfiguration(new CertificateIdentifierCollection
            {
                new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(certificateRoot, "own"),
                    SubjectName = "CN=PlantOpsLocalDemo, DC=localhost",
                    CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType
                }
            }, certificateRoot).SetAutoAcceptUntrustedCertificates(false).CreateAsync();
        var appConfiguration = application.ApplicationConfiguration;
        appConfiguration.TransportQuotas.OperationTimeout = 3000;
        var description = await CoreClientUtils.SelectEndpointAsync(appConfiguration, endpointUrl, false, telemetry, cancellationToken)
            ?? throw new InvalidOperationException("Local demo endpoint unavailable.");
        ValidateEndpoint(description.EndpointUrl);
        var requested = new Uri(endpointUrl);
        var advertised = new Uri(description.EndpointUrl);
        if (advertised.Port != requested.Port || description.Server.ApplicationUri != "urn:industrial-sim:server"
            || description.SecurityMode != MessageSecurityMode.None || description.SecurityPolicyUri != SecurityPolicies.None)
        {
            throw new InvalidOperationException("Unexpected local simulator identity or endpoint policy.");
        }
        using var session = await new DefaultSessionFactory(telemetry).CreateAsync(appConfiguration,
            new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(appConfiguration)), false, true,
            "PlantOpsFiniteReadOnlyDemo", 10000, new UserIdentity(new AnonymousIdentityToken()), null, cancellationToken);
        var validation = await OpcUaMetadataReader.ValidateAsync(session, configuration.SourceKey, mapping,
            configuration.RawUnit, configuration.CanonicalUnit, configuration.Scale, configuration.Offset, cancellationToken);
        if (!validation.Accepted)
        {
            throw new InvalidOperationException("Demo node rejected: " + validation.Error);
        }
        var channel = Channel.CreateBounded<NormalizedSample>(new BoundedChannelOptions(100)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = 1000 };
        var item = new MonitoredItem(subscription.DefaultItem)
        {
            StartNodeId = OpcUaNodeIdentity.Resolve(mapping, session.NamespaceUris),
            AttributeId = Attributes.Value,
            SamplingInterval = 1000,
            QueueSize = 10,
            DiscardOldest = true,
            Filter = new DataChangeFilter { Trigger = DataChangeTrigger.StatusValueTimestamp }
        };
        item.Notification += (_, _) =>
        {
            try
            {
                foreach (var value in item.DequeueValues())
                {
                    var sample = OpcUaNormalizer.Normalize(configuration, value, DateTimeOffset.UtcNow, Guid.NewGuid());
                    if (!channel.Writer.TryWrite(sample))
                    {
                        channel.Writer.TryComplete(new InvalidOperationException("Demo notification queue overflow; possible loss."));
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
            }
        };
        session.KeepAlive += (_, args) =>
        {
            if (ServiceResult.IsBad(args.Status))
            {
                channel.Writer.TryComplete(new InvalidOperationException("Demo session disconnected; possible loss."));
            }
        };
        session.AddSubscription(subscription);
        subscription.AddItem(item);
        var effectiveFrom = DateTimeOffset.UtcNow;
        var activated = false;
        try
        {
            await subscription.CreateAsync(cancellationToken);
            if (ServiceResult.IsBad(item.Status.Error) || item.Status.SamplingInterval is < 1000 or > 2000
                || subscription.CurrentPublishingInterval is < 1000 or > 2000 || item.Status.QueueSize is < 1 or > 10)
            {
                throw new InvalidOperationException("Server revision exceeds the finite demo budget.");
            }
            await opened(new(configuration.PointId, configuration.MappingId, configuration.MappingVersion,
                effectiveFrom, "Periodic", item.Status.SamplingInterval, subscription.CurrentPublishingInterval,
                item.Status.QueueSize), cancellationToken);
            activated = true;
            await foreach (var sample in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return sample;
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (subscription.Created)
                {
                    await subscription.DeleteAsync(true, cleanup.Token);
                }
                await session.CloseAsync(cleanup.Token);
            }
            finally
            {
                if (activated)
                {
                    await closed(DateTimeOffset.UtcNow, cleanup.Token);
                }
                subscription.Dispose();
            }
        }
    }
}
