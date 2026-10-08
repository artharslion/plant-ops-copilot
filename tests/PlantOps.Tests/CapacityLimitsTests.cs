using PlantOps.OpcUa.Spike;
using Xunit;

namespace PlantOps.Tests;

public sealed class CapacityLimitsTests
{
    [Theory]
    [InlineData("plantops", "p2_capacity_trial", "p2_capacity_trial")]
    [InlineData("plantops_tests", "public", "public")]
    [InlineData("plantops_tests", "p2_capacity_trial,public", "p2_capacity_trial")]
    [InlineData("plantops_tests", "p2_capacity_other", "p2_capacity_trial")]
    [InlineData("plantops_tests", "p2_capacity_trial", "p2_capacity_trial;drop")]
    public void RejectsBusinessDatabaseFallbackAndUnsafeSchema(string database, string path, string schema) =>
        Assert.Throws<ArgumentException>(() => CapacityLimits.ValidateConnection($"Database={database};Search Path={path}", schema));

    [Theory]
    [InlineData(12001, 0, 0, 0, 0, 2147483648)]
    [InlineData(0, 67108865, 0, 0, 0, 2147483648)]
    [InlineData(0, 0, 134217729, 0, 0, 2147483648)]
    [InlineData(0, 0, 0, 134217729, 0, 2147483648)]
    [InlineData(0, 0, 0, 0, 268435457, 2147483648)]
    [InlineData(0, 0, 0, 0, 0, 2147483647)]
    public void StopsAtEachIndependentBudget(long rows, long schema, long generated, long wal, long used, long free) =>
        Assert.Throws<InvalidOperationException>(() => CapacityLimits.Check(rows, schema, generated, wal, used, free));

    [Fact]
    public void AcceptsExactLimitsAndDedicatedSchema()
    {
        CapacityLimits.ValidateConnection("Database=plantops_tests;Search Path=p2_capacity_trial", "p2_capacity_trial");
        CapacityLimits.Check(12000, CapacityLimits.SchemaBytes, CapacityLimits.WalBytes, CapacityLimits.WalBytes,
            CapacityLimits.HostGrowthBytes, CapacityLimits.MinimumFreeBytes);
    }
}
