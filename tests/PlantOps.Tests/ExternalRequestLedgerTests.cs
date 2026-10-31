using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class ExternalRequestLedgerTests
{
    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public void CapRetainsEveryReservationAndRejectsExcess(int limit)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"kind\":\"authorization\",\"limit\":" + limit + "}\n");
            for (var i = 0; i < limit; i++)
            {
                ExternalRequestLedger.ReserveAt(path, "test", [1, 2]);
            }
            var before = File.ReadAllText(path);
            Assert.Equal(LlmFailure.BudgetExceeded, Assert.Throws<LlmException>(() => ExternalRequestLedger.ReserveAt(path, "test", [3])).Failure);
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(limit + 1, File.ReadAllLines(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"kind\":\"authorization\",\"limit\":101}\n")]
    [InlineData("{\"kind\":\"authorization\",\"limit\":50}\n{\"sequence\":2}\n")]
    [InlineData("{\"kind\":\"authorization\",\"limit\":50}")]
    public void CorruptOrIncompleteLedgerFailsClosed(string content)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, content);
            Assert.Throws<LlmException>(() => ExternalRequestLedger.ReserveAt(path, "test", [1]));
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ConcurrentReservationsCannotExceedCap()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"kind\":\"authorization\",\"limit\":5}\n");
            await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => Task.Run(() =>
            {
                try
                {
                    ExternalRequestLedger.ReserveAt(path, "test", [1]);
                }
                catch (LlmException) { }
            })));
            Assert.InRange(File.ReadAllLines(path).Length, 2, 6);
        }
        finally { File.Delete(path); }
    }
}
