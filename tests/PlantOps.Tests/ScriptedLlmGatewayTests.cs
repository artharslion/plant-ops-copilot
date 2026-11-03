using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class ScriptedLlmGatewayTests
{
    private static readonly LlmRequest Request = new([new(LlmRole.User, "fixture")], MaxOutputTokens: 20);

    [Fact]
    public async Task ResponsesAreExplicitAndExhaustionDoesNotFallBackToDemo()
    {
        var gateway = new ScriptedLlmGateway([new("explicit", [], "fixture", new(10, 5))]);
        using var budget = new LlmBudget();
        Assert.Equal("explicit", (await gateway.CompleteAsync(Request, budget)).Content);
        var error = await Assert.ThrowsAsync<LlmException>(() => gateway.CompleteAsync(Request, budget));
        Assert.Equal(LlmFailure.InvalidResponse, error.Failure);
        Assert.Equal(2, budget.ModelRounds);
    }

    [Fact]
    public async Task RoundLimitStopsBeforeConsumingTheNextResponse()
    {
        var gateway = new ScriptedLlmGateway([new("first", [], "fixture", new(10, 5)), new("second", [], "fixture", new(10, 5))]);
        using var budget = new LlmBudget(new(MaxModelRounds: 1));
        await gateway.CompleteAsync(Request, budget);
        var error = await Assert.ThrowsAsync<LlmException>(() => gateway.CompleteAsync(Request, budget));
        Assert.Equal(LlmFailure.BudgetExceeded, error.Failure);
        using var freshBudget = new LlmBudget();
        Assert.Equal("second", (await gateway.CompleteAsync(Request, freshBudget)).Content);
    }

    [Fact]
    public async Task CancellationDoesNotConsumeAResponseOrReserveBudget()
    {
        var gateway = new ScriptedLlmGateway([new("first", [], "fixture", new(10, 5))]);
        using var budget = new LlmBudget();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.CompleteAsync(Request, budget, cancellation.Token));
        Assert.Equal(0, budget.ModelRounds);
        Assert.Equal("first", (await gateway.CompleteAsync(Request, budget)).Content);
    }
}
