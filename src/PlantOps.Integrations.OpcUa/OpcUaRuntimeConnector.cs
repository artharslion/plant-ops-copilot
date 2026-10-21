using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using PlantOps.Application.Telemetry;

namespace PlantOps.Integrations.OpcUa;

public sealed class OpcUaRuntimeConnector(Func<string, OpcUaApprovedRuntimeEndpoint> resolveEndpoint,
    bool development, bool enabled) : IOpcUaConnector
{
    private sealed class RuntimeTelemetry() : TelemetryContextBase(NullLoggerFactory.Instance);

    public async Task<IOpcUaConnection> ConnectAsync(IReadOnlyList<SourceConfiguration> configurations, CancellationToken ct)
    {
        OpcUaApprovedRuntimeEndpoint approved;
        OpcUaMappingInput[] mappings;
        try
        {
            var reference = System.Text.Json.JsonSerializer.Deserialize<OpcUaConnectionInput>(configurations[0].ConnectionParameters)
                ?? throw new SourceConfigurationException("ConnectionConfigurationRejected");
            OpcUaConfigurationPolicy.Validate(reference);
            approved = resolveEndpoint(reference.EndpointReference);
            OpcUaRuntimePolicy.ValidateEndpoint(approved, development, enabled);
            mappings = OpcUaRuntimePolicy.Validate(configurations, approved);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
        {
            throw new SourceConfigurationException("RuntimePolicyRejected");
        }
        var telemetry = new RuntimeTelemetry();
        var application = new ApplicationInstance(telemetry) { ApplicationName = "PlantOpsControlledLocal" };
        await application.Build("urn:localhost:plantops:controlled-local", "urn:plantops:controlled-local").AsClient()
            .AddSecurityConfiguration(new CertificateIdentifierCollection
            {
                new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(approved.CertificateRoot, "own"),
                    SubjectName = "CN=PlantOpsControlledLocal, DC=localhost",
                    CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType
                }
            }, approved.CertificateRoot).SetAutoAcceptUntrustedCertificates(false).CreateAsync();
        var settings = application.ApplicationConfiguration;
        settings.TransportQuotas.OperationTimeout = 3000;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ISession? session = null;
        Subscription? subscription = null;
        try
        {
            var description = await CoreClientUtils.SelectEndpointAsync(settings, approved.Url, false, telemetry, timeout.Token)
                ?? throw new IOException("Endpoint unavailable.");
            // Discovery must not redirect the Worker or silently select a different security policy.
            if (new Uri(description.EndpointUrl) != new Uri(approved.Url) || description.Server.ApplicationUri != approved.ApplicationUri
                || description.SecurityMode != MessageSecurityMode.None || description.SecurityPolicyUri != SecurityPolicies.None)
            {
                throw new SourceConfigurationException("ServerIdentityOrPolicyRejected");
            }
            session = await new DefaultSessionFactory(telemetry).CreateAsync(settings,
                new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(settings)), false, true,
                "PlantOpsControlledReadOnly", 10000, new UserIdentity(new AnonymousIdentityToken()), null, timeout.Token);
            session.KeepAliveInterval = 2000;
            subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingInterval = 1000,
                KeepAliveCount = 2,
                LifetimeCount = 10
            };
            var connection = new Connection(session, subscription);
            for (var index = 0; index < configurations.Count; index++)
            {
                var configuration = configurations[index];
                var mapping = mappings[index];
                var validation = await OpcUaMetadataReader.ValidateAsync(session, configuration.SourceKey, mapping,
                    configuration.RawUnit, configuration.CanonicalUnit, configuration.Scale, configuration.Offset, timeout.Token);
                if (!validation.Accepted)
                {
                    throw new SourceConfigurationException("NodeMetadataRejected");
                }
                var item = new MonitoredItem(subscription.DefaultItem)
                {
                    StartNodeId = OpcUaNodeIdentity.Resolve(mapping, session.NamespaceUris),
                    AttributeId = Attributes.Value,
                    SamplingInterval = mapping.SamplingIntervalMs,
                    QueueSize = (uint)mapping.QueueSize,
                    DiscardOldest = true,
                    Filter = new DataChangeFilter { Trigger = DataChangeTrigger.StatusValueTimestamp }
                };
                item.Notification += (_, _) => connection.Receive(item, configuration);
                subscription.AddItem(item);
            }
            session.AddSubscription(subscription);
            var at = DateTimeOffset.UtcNow;
            await subscription.CreateAsync(timeout.Token);
            if (subscription.CurrentPublishingInterval is < 1000 or > 2000 || subscription.MonitoredItems.Any(item =>
                ServiceResult.IsBad(item.Status.Error) || item.Status.SamplingInterval is < 1000 or > 2000 || item.Status.QueueSize is < 1 or > 10))
            {
                throw new SourceConfigurationException("ServerSamplingRevisionRejected");
            }
            connection.Revisions = subscription.MonitoredItems.Select((item, index) =>
                new SamplingRevision(configurations[index].PointId, configurations[index].MappingId, configurations[index].MappingVersion,
                    at, "Periodic", item.Status.SamplingInterval, subscription.CurrentPublishingInterval, item.Status.QueueSize)).ToArray();
            return connection;
        }
        catch (ServiceResultException exception) when (IsSecurityFailure(exception.StatusCode))
        {
            await CleanupAsync(session, subscription);
            throw new SourceConfigurationException("SecurityRejected");
        }
        catch
        {
            await CleanupAsync(session, subscription);
            throw;
        }
    }

    private static bool IsSecurityFailure(uint code)
    {
        var symbol = new StatusCode(code).SymbolicId ?? "";
        return symbol.Contains("Certificate", StringComparison.Ordinal) || symbol.Contains("Security", StringComparison.Ordinal)
            || symbol.Contains("Identity", StringComparison.Ordinal) || code == StatusCodes.BadUserAccessDenied;
    }

    private static async Task CleanupAsync(ISession? session, Subscription? subscription)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if (session != null)
            {
                await session.CloseAsync(cleanup.Token);
            }
        }
        catch (Exception exception) when (exception is ServiceResultException or OperationCanceledException or IOException) { }
        finally
        {
            subscription?.Dispose();
            session?.Dispose();
        }
    }

    private sealed class Connection : IOpcUaConnection
    {
        private readonly ISession session;
        private readonly Subscription subscription;
        private readonly Channel<NormalizedSample> channel = Channel.CreateBounded<NormalizedSample>(new BoundedChannelOptions(100)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        private long lastKeepAliveTicks = DateTimeOffset.UtcNow.UtcTicks;

        public Connection(ISession session, Subscription subscription)
        {
            this.session = session;
            this.subscription = subscription;
            session.KeepAlive += (_, args) =>
            {
                if (ServiceResult.IsBad(args.Status))
                {
                    channel.Writer.TryComplete(new IOException("Session keep-alive failed."));
                }
                else
                {
                    Interlocked.Exchange(ref lastKeepAliveTicks, DateTimeOffset.UtcNow.UtcTicks);
                }
            };
        }

        public IReadOnlyList<SamplingRevision> Revisions { get; set; } = [];

        public DateTimeOffset? LastKeepAliveAt => new DateTimeOffset(Interlocked.Read(ref lastKeepAliveTicks), TimeSpan.Zero);

        public void Receive(MonitoredItem item, SourceConfiguration configuration)
        {
            try
            {
                foreach (var value in item.DequeueValues())
                {
                    if (!channel.Writer.TryWrite(OpcUaNormalizer.Normalize(configuration, value, DateTimeOffset.UtcNow, Guid.NewGuid())))
                    {
                        channel.Writer.TryComplete(new IOException("Notification buffer overflow; possible loss."));
                        break;
                    }
                }
            }
            catch (Exception exception) { channel.Writer.TryComplete(exception); }
        }

        public async IAsyncEnumerable<NormalizedSample> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var sample in channel.Reader.ReadAllAsync(ct))
            {
                yield return sample;
            }
        }

        public async ValueTask DisposeAsync()
        {
            channel.Writer.TryComplete();
            await CleanupAsync(session, subscription);
        }
    }
}
