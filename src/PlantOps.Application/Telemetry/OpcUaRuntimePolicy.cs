using System.Text.Json;

namespace PlantOps.Application.Telemetry;

public sealed record OpcUaApprovedRuntimeEndpoint(string Url, string ApplicationUri, string NamespaceUri,
    string[] Nodes, string CertificateRoot);

public static class OpcUaRuntimePolicy
{
    public static void ValidateEndpoint(OpcUaApprovedRuntimeEndpoint endpoint, bool development, bool enabled)
    {
        if (!development || !enabled || !Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != "opc.tcp" || uri.Host is not ("localhost" or "127.0.0.1") || uri.Port < 1
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(endpoint.ApplicationUri) || !Uri.TryCreate(endpoint.ApplicationUri, UriKind.Absolute, out _)
            || !Uri.TryCreate(endpoint.NamespaceUri, UriKind.Absolute, out _) || endpoint.Nodes.Length is < 1 or > 10
            || endpoint.Nodes.Distinct(StringComparer.Ordinal).Count() != endpoint.Nodes.Length
            || string.IsNullOrWhiteSpace(endpoint.CertificateRoot))
        {
            throw new SourceConfigurationException("RuntimePolicyRejected");
        }
    }

    public static OpcUaMappingInput[] Validate(IReadOnlyList<SourceConfiguration> configurations, OpcUaApprovedRuntimeEndpoint endpoint)
    {
        if (configurations.Count is < 1 or > 10 || configurations.Select(x => (x.DataSourceId, x.SourceVersion, x.ConnectionParameters)).Distinct().Count() != 1
            || configurations.Select(x => x.PointId).Distinct().Count() != configurations.Count)
        {
            throw new SourceConfigurationException("MappingBudgetRejected");
        }
        var connection = JsonSerializer.Deserialize<OpcUaConnectionInput>(configurations[0].ConnectionParameters)
            ?? throw new SourceConfigurationException("ConnectionConfigurationRejected");
        OpcUaConfigurationPolicy.Validate(connection);
        if (connection.PublishingIntervalMs != 1000)
        {
            throw new SourceConfigurationException("SamplingBudgetRejected");
        }
        return configurations.Select(configuration =>
        {
            var mapping = JsonSerializer.Deserialize<OpcUaMappingInput>(configuration.SourceParameters)
                ?? throw new SourceConfigurationException("MappingConfigurationRejected");
            OpcUaConfigurationPolicy.Validate(mapping);
            if (configuration.SourceKind != "OpcUa" || mapping.NamespaceUri != endpoint.NamespaceUri
                || !endpoint.Nodes.Contains(mapping.IdentifierType + ":" + mapping.Identifier, StringComparer.Ordinal)
                || configuration.SourceKey != OpcUaConfigurationPolicy.Key(mapping)
                || mapping.SamplingMode != "Periodic" || mapping.SamplingIntervalMs != 1000 || mapping.QueueSize is < 1 or > 10
                || !OpcUaConfigurationPolicy.ValidConversion(configuration.RawUnit, configuration.CanonicalUnit, configuration.Scale, configuration.Offset))
            {
                throw new SourceConfigurationException("MappingScopeRejected");
            }
            return mapping;
        }).ToArray();
    }
}
