using Xunit;

namespace PlantOps.Tests;

public sealed class SmallBackupGuardTests
{
    [Fact]
    public void RemapPreservesCopyPayload()
    {
        const string sql = "CREATE SCHEMA p2_backup_source;\nCOPY p2_backup_source.\"Documents\" FROM stdin;\np2_backup_source\tmanual\n\\.\nALTER TABLE p2_backup_source.\"Documents\" OWNER TO plantops;\n";
        var result = SmallBackupExperiment.Remap(sql, "p2_backup_source", "p2_backup_target");
        Assert.Contains("CREATE SCHEMA p2_backup_target;", result);
        Assert.Contains("\np2_backup_source\tmanual\n", result);
        Assert.Contains("ALTER TABLE p2_backup_target.", result);
    }

    [Theory]
    [InlineData("public", "p2_backup_target")]
    [InlineData("p2_backup_source", "public")]
    [InlineData("p2_backup_source", "p2_backup_source")]
    [InlineData("p2_backup_source", "p2_backup_a;DROP SCHEMA public")]
    public void RejectUnsafeSchemas(string source, string target) =>
        Assert.Throws<InvalidOperationException>(() => SmallBackupExperiment.Remap("", source, target));

    [Theory]
    [InlineData(181, 0, 0, 3000000000)]
    [InlineData(0, 268435457, 0, 3000000000)]
    [InlineData(0, 0, 134217729, 3000000000)]
    [InlineData(0, 0, 0, 2147483647)]
    public void StopsAtObservedLimits(double seconds, long growth, long wal, long free) =>
        Assert.Throws<InvalidOperationException>(() => SmallBackupExperiment.Check(seconds, growth, wal, free));

    [Fact]
    public void AllowsBoundedObservation() => SmallBackupExperiment.Check(180, 268435456, 134217728, 2147483648);

    [Theory]
    [InlineData(67108865, 0, 0, 0)]
    [InlineData(0, 33554433, 0, 0)]
    [InlineData(0, 0, 4, 0)]
    [InlineData(0, 0, 0, 1048577)]
    public void RejectOversizePayload(long schema, long dump, int files, long bytes) =>
        Assert.Throws<InvalidOperationException>(() => SmallBackupExperiment.CheckPayload(schema, dump, files, bytes));

    [Fact]
    public void RejectIncompleteCopy() => Assert.Throws<InvalidOperationException>(() =>
        SmallBackupExperiment.Remap("COPY p2_backup_source.t FROM stdin;\nvalue", "p2_backup_source", "p2_backup_target"));
}

public sealed class SmallBackupFactAttribute : FactAttribute
{
    public SmallBackupFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PLANTOPS_SMALL_BACKUP_RUN") == null)
        {
            Skip = "Opt-in bounded backup experiment; run deploy/Run-SmallJointBackup.ps1 with a fresh run.";
        }
    }
}

[Collection("Database")]
public sealed class SmallBackupRestoreTests
{
    [SmallBackupFact]
    public async Task FrozenDatabaseAndFilesRestoreWithAclAndProtocolFidelity()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This opt-in harness measures Windows host drives and file ACLs.");
        }
        await SmallBackupExperiment.RunAsync();
    }
}
