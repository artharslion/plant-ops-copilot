using Npgsql;
using System.Text.RegularExpressions;

namespace PlantOps.OpcUa.Spike;

public static class CapacityLimits
{
    public const int Rounds = 120;
    public const int Points = 100;
    public const long SchemaBytes = 64L * 1024 * 1024;
    public const long WalBytes = 128L * 1024 * 1024;
    public const long HostGrowthBytes = 256L * 1024 * 1024;
    public const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    public static void ValidateConnection(string connection, string schema)
    {
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        if (parsed.Database != "plantops_tests" || parsed.SearchPath != schema
            || !Regex.IsMatch(schema, "^p2_capacity_[a-z0-9_]{1,50}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Capacity experiments require plantops_tests and one dedicated p2_capacity_* search path.");
        }
    }

    public static void Check(long rows, long schemaBytes, long walGenerated, long walGrowth,
        long hostConsumed, long hostFree)
    {
        if (rows < 0 || rows > Rounds * Points || schemaBytes < 0 || schemaBytes > SchemaBytes
            || walGenerated < 0 || walGenerated > WalBytes || walGrowth > WalBytes
            || hostConsumed > HostGrowthBytes || hostFree < MinimumFreeBytes)
        {
            throw new InvalidOperationException("Small-scale capacity experiment exceeded its fixed budget; keep all evidence and stop writing.");
        }
    }
}
