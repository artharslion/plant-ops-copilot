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
        Assert.Equal(3, budget.ReservedOutputTokens);
        budget.ReserveAttempt(30, 10);
        Assert.Equal(100, budget.ReservedInputTokens);
    }

    [Fact]
    public void ReportedUsageReleasesOnlyTheSuccessfulAttemptReservation()
    {
        using var budget = new LlmBudget();
        budget.ReserveAttempt(1000, 100); // Failed attempt: usage unknown.
        budget.ReserveAttempt(12274, 900);
        budget.ReconcileUsage(new(8036, 15), 12274, 900);
        Assert.Equal(9036, budget.ReservedInputTokens);
        Assert.Equal(115, budget.ReservedOutputTokens);
        budget.ReserveAttempt(6964, 900);
        Assert.Equal(16000, budget.ReservedInputTokens);
        Assert.Equal(3, budget.Attempts);
    }

    [Fact]
    public void UnknownUsageKeepsReservationAndCannotBeReconciledTwice()
    {
        using var budget = new LlmBudget();
        budget.ReserveAttempt(100, 50);
        budget.ReconcileUsage(null, 100, 50);
        Assert.Equal(100, budget.ReservedInputTokens);
        Assert.Equal(50, budget.ReservedOutputTokens);
        Assert.Equal(LlmFailure.InvalidResponse, Assert.Throws<LlmException>(() => budget.ReconcileUsage(new(1, 1), 100, 50)).Failure);
        Assert.Equal(100, budget.ReservedInputTokens);
    }

    [Fact]
    public void DuplicateOrMismatchedReconciliationCannotReleaseBudget()
    {
        using var budget = new LlmBudget();
        budget.ReserveAttempt(100, 50);
        Assert.Equal(LlmFailure.InvalidResponse, Assert.Throws<LlmException>(() => budget.ReconcileUsage(new(1, 1), 99, 50)).Failure);
        Assert.Equal(100, budget.ReservedInputTokens);
        budget.ReconcileUsage(new(20, 5), 100, 50);
        Assert.Equal(LlmFailure.InvalidResponse, Assert.Throws<LlmException>(() => budget.ReconcileUsage(new(1, 1), 100, 50)).Failure);
        Assert.Equal(20, budget.ReservedInputTokens);
        Assert.Equal(5, budget.ReservedOutputTokens);
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
