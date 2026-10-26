using System.Text.Json;

namespace PlantOps.Application.AI;

public sealed record LlmLimits(int RunTimeoutSeconds = 180, int MaxModelRounds = 4, int MaxToolCalls = 8,
    int MaxInputTokens = 16000, int MaxOutputTokens = 2000);

// One instance per investigation/probe. Calls within a run are sequential, never shared across actors.
public sealed class LlmBudget : IDisposable
{
    private readonly LlmLimits limits;
    private readonly CancellationTokenSource deadline;
    private bool exceeded;
    private (int Input, int Output)? pendingReservation;

    public LlmBudget(LlmLimits? limits = null, TimeProvider? clock = null)
    {
        this.limits = limits ?? new();
        if (this.limits.RunTimeoutSeconds is < 1 or > 600 || this.limits.MaxModelRounds is < 1 or > 4
            || this.limits.MaxToolCalls is < 1 or > 8 || this.limits.MaxInputTokens is < 1 or > 32000
            || this.limits.MaxOutputTokens is < 1 or > 6000)
        {
            throw new LlmException(LlmFailure.InvalidConfiguration);
        }
        deadline = new(TimeSpan.FromSeconds(this.limits.RunTimeoutSeconds), clock ?? TimeProvider.System);
    }

    public CancellationToken Deadline => deadline.Token;

    public LlmLimits Limits => limits;

    public long ActualInputTokens
    {
        get; private set;
    }

    public long ActualOutputTokens
    {
        get; private set;
    }

    public int ReportedUsageAttempts
    {
        get; private set;
    }

    public int UnknownUsageAttempts => Attempts - ReportedUsageAttempts;

    public long UnknownInputReservation
    {
        get; private set;
    }

    public long UnknownOutputReservation
    {
        get; private set;
    }

    public string PromptVersion { get; private set; } = "unknown";

    public List<JsonElement> Requests { get; } = [];

    public Func<CancellationToken, Task>? CheckpointAsync
    {
        get; set;
    }

    public async Task RecordRequestAsync(string promptVersion, JsonElement snapshot, CancellationToken cancellationToken)
    {
        PromptVersion = promptVersion;
        Requests.Add(snapshot.Clone());
        if (CheckpointAsync != null)
        {
            await CheckpointAsync(cancellationToken);
        }
    }

    public int ModelRounds
    {
        get; private set;
    }

    public int Attempts
    {
        get; private set;
    }

    public int ToolCalls
    {
        get; private set;
    }

    public int ReservedInputTokens
    {
        get; private set;
    }

    public int ReservedOutputTokens
    {
        get; private set;
    }

    public int RemainingOutputTokens => limits.MaxOutputTokens - ReservedOutputTokens;

    public int RemainingInputTokens => limits.MaxInputTokens - ReservedInputTokens;

    public void BeginRound()
    {
        CheckDeadline();
        if (ModelRounds >= limits.MaxModelRounds)
        {
            throw new LlmException(LlmFailure.BudgetExceeded);
        }
        ModelRounds++;
    }

    public void ReserveAttempt(int inputReservation, int outputTokens)
    {
        CheckDeadline();
        if (inputReservation < 1 || outputTokens < 1
            || inputReservation > limits.MaxInputTokens - ReservedInputTokens
            || outputTokens > RemainingOutputTokens)
        {
            throw new LlmException(LlmFailure.BudgetExceeded);
        }
        ReservedInputTokens += inputReservation;
        ReservedOutputTokens += outputTokens;
        UnknownInputReservation += inputReservation;
        UnknownOutputReservation += outputTokens;
        pendingReservation = (inputReservation, outputTokens);
        Attempts++;
    }

    // A relay may add its own prompt. Reconcile reported usage against the whole run,
    // while preserving conservative reservations for attempts with unknown usage.
    public void ReconcileUsage(LlmUsage? usage, int inputReservation, int outputReservation)
    {
        if (pendingReservation != (inputReservation, outputReservation))
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        if (usage is null)
        {
            pendingReservation = null;
            CheckDeadline();
            return;
        }
        if (usage.InputTokens < 0 || usage.OutputTokens < 0)
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        pendingReservation = null;
        ActualInputTokens += usage.InputTokens;
        ActualOutputTokens += usage.OutputTokens;
        ReportedUsageAttempts++;
        UnknownInputReservation -= inputReservation;
        UnknownOutputReservation -= outputReservation;
        ReservedInputTokens = (int)Math.Min(int.MaxValue, (long)ReservedInputTokens - inputReservation + usage.InputTokens);
        ReservedOutputTokens = (int)Math.Min(int.MaxValue, (long)ReservedOutputTokens - outputReservation + usage.OutputTokens);
        if (ReservedInputTokens > limits.MaxInputTokens || ReservedOutputTokens > limits.MaxOutputTokens
            || usage.OutputTokens > outputReservation)
        {
            exceeded = true;
            throw new LlmException(LlmFailure.BudgetExceeded, usage);
        }
        CheckDeadline();
    }

    public void RecordToolCalls(int count)
    {
        CheckDeadline();
        if (count < 0 || count > limits.MaxToolCalls - ToolCalls)
        {
            throw new LlmException(LlmFailure.BudgetExceeded);
        }
        ToolCalls += count;
    }

    public void CheckDeadline()
    {
        if (exceeded)
        {
            throw new LlmException(LlmFailure.BudgetExceeded);
        }
        if (deadline.IsCancellationRequested)
        {
            throw new LlmException(LlmFailure.Timeout);
        }
    }

    public void Dispose() => deadline.Dispose();
}
