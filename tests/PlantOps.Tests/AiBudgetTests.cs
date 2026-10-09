using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class AiBudgetTests
{
    [Fact]
    public void ReservationsAreCumulativeIncludingRetries()
    {
        using var budget = new LlmBudget(new(MaxInputTokens: 20, MaxOutputTokens: 10));
        budget.BeginRound();
        budget.ReserveAttempt(10, 5);
        budget.ReserveAttempt(10, 5);
        Assert.Equal(1, budget.ModelRounds);
        Assert.Equal(2, budget.Attempts);
        Assert.Equal(20, budget.ReservedInputTokens);
        Assert.Equal(0, budget.RemainingOutputTokens);
        Assert.Equal(LlmFailure.BudgetExceeded, Assert.Throws<LlmException>(() => budget.ReserveAttempt(1, 1)).Failure);
        Assert.Equal(2, budget.Attempts);
    }

    [Theory]
    [InlineData(11, 1)]
    [InlineData(1, 11)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void RejectsAttemptsBeforeIncreasingCounters(int input, int output)
    {
        using var budget = new LlmBudget(new(MaxInputTokens: 10, MaxOutputTokens: 10));
        Assert.Equal(LlmFailure.BudgetExceeded, Assert.Throws<LlmException>(() => budget.ReserveAttempt(input, output)).Failure);
        Assert.Equal(0, budget.Attempts);
    }

    [Fact]
    public void LimitsModelRoundsAndTools()
    {
        using var budget = new LlmBudget(new(MaxModelRounds: 2, MaxToolCalls: 2));
        budget.BeginRound();
        budget.BeginRound();
        Assert.Equal(LlmFailure.BudgetExceeded, Assert.Throws<LlmException>(budget.BeginRound).Failure);
        budget.RecordToolCalls(2);
        Assert.Equal(LlmFailure.BudgetExceeded, Assert.Throws<LlmException>(() => budget.RecordToolCalls(1)).Failure);
        Assert.Equal(2, budget.ToolCalls);
    }

    [Theory]
    [InlineData(61, 4, 8, 16000, 2000)]
    [InlineData(60, 5, 8, 16000, 2000)]
    [InlineData(60, 4, 9, 16000, 2000)]
    [InlineData(60, 4, 8, 16001, 2000)]
    [InlineData(60, 4, 8, 16000, 2001)]
    public void RejectsLimitsBeyondDemoCeilings(int seconds, int rounds, int tools, int input, int output)
    {
        Assert.Equal(LlmFailure.InvalidConfiguration,
            Assert.Throws<LlmException>(() => new LlmBudget(new(seconds, rounds, tools, input, output))).Failure);
    }

    [Fact]
    public async Task DeadlineStopsFurtherRounds()
    {
        using var budget = new LlmBudget(new(RunTimeoutSeconds: 1));
        await Task.Delay(1200);
        Assert.True(budget.Deadline.IsCancellationRequested);
        Assert.Equal(LlmFailure.Timeout, Assert.Throws<LlmException>(budget.BeginRound).Failure);
    }

    [Fact]
    public void RelayPromptOverheadIsReconciledAgainstTotalBudget()
    {
        using var budget = new LlmBudget(new(MaxInputTokens: 100, MaxOutputTokens: 20));
        budget.ReserveAttempt(10, 10);
        budget.ReconcileUsage(new(70, 3), 10, 10);
        Assert.Equal(70, budget.ReservedInputTokens);
        Assert.Equal(10, budget.ReservedOutputTokens);
        budget.ReserveAttempt(30, 10);
        Assert.Equal(100, budget.ReservedInputTokens);
    }

    [Theory]
    [InlineData(101, 3)]
    [InlineData(20, 11)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ReportedExcessExhaustsRunWithoutOverflowOrFurtherCalls(int input, int output)
    {
        using var budget = new LlmBudget(new(MaxInputTokens: 100, MaxOutputTokens: 20));
        budget.ReserveAttempt(10, 10);
        var exception = Assert.Throws<LlmException>(() => budget.ReconcileUsage(new(input, output), 10, 10));
        Assert.Equal(LlmFailure.BudgetExceeded, exception.Failure);
        Assert.Equal(new LlmUsage(input, output), exception.Usage);
        Assert.Equal(LlmFailure.BudgetExceeded, Assert.Throws<LlmException>(budget.BeginRound).Failure);
        Assert.Equal(1, budget.Attempts);
    }
}
