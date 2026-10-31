using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using PlantOps.Application.AI;

namespace PlantOps.AI;

public sealed class OpenAiCompatibleGateway(HttpClient client, IOptions<AiOptions> settings,
    ILogger<OpenAiCompatibleGateway>? logger = null) : ILlmGateway
{
    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
    {
        using var logScope = logger?.BeginScope(new Dictionary<string, object?>
        {
            ["RunId"] = budget.RunId,
            ["LeaseId"] = budget.LeaseId
        });
        try
        {
            return await CompleteCoreAsync(request, budget, cancellationToken);
        }
        catch (Exception exception)
        {
            budget.RecordDiagnostic("model-call", "Failed", new
            {
                failureCode = exception is LlmException llm ? llm.Failure.ToString()
                    : exception is OperationCanceledException ? "Cancelled" : "Unexpected",
                validationRule = (exception as LlmException)?.ValidationRule,
                exceptionType = exception.GetType().Name
            });
            throw;
        }
    }

    private async Task<LlmCompletion> CompleteCoreAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = settings.Value;
        var endpoint = options.ValidateEndpoint();
        var body = ChatCompletionsProtocol.Serialize(request, options.Model, options.ReasoningEffort, options.StreamResponses);
        // UTF-8 bytes plus framing estimates visible input. Reported relay overhead is reconciled below.
        var inputReservation = checked(body.Length + 256);
        budget.BeginRound();
        LlmFailure? retryFailure = null;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                budget.ReserveAttempt(inputReservation, request.MaxOutputTokens);
            }
            catch (LlmException exception) when (exception.Failure == LlmFailure.BudgetExceeded)
            {
                if (retryFailure.HasValue)
                {
                    budget.RecordDiagnostic("attempt-reservation", "RetrySuppressed", new
                    {
                        failureCode = retryFailure.Value.ToString()
                    });
                    // Unknown usage keeps its reservation. If that leaves no room
                    // for a retry, preserve the original service/transport failure.
                    logger?.LogWarning("AI retry suppressed: stage=attempt-reservation; failure={Failure}; attempt={Attempt}",
                        retryFailure.Value, attempt + 1);
                    throw new LlmException(retryFailure.Value);
                }
                logger?.LogWarning("AI budget rejected: stage=attempt-reservation; inputReservation={InputReservation}; outputAllowance={OutputAllowance}; reservedInput={ReservedInput}; reservedOutput={ReservedOutput}; rounds={Rounds}; tools={Tools}",
                    inputReservation, request.MaxOutputTokens, budget.ReservedInputTokens, budget.ReservedOutputTokens, budget.ModelRounds, budget.ToolCalls);
                budget.RecordDiagnostic("attempt-reservation", "Rejected", new
                {
                    inputReservation,
                    outputAllowance = request.MaxOutputTokens
                });
                throw;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Deadline);
            if (budget.CheckpointAsync != null)
            {
                await budget.CheckpointAsync(cancellationToken);
            }
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            var receivedHeaders = false;
            var elapsed = Stopwatch.StartNew();
            var stage = "dispatch";
            try
            {
                using var outgoing = new HttpRequestMessage(HttpMethod.Post, endpoint);
                outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                outgoing.Content = new ByteArrayContent(body);
                outgoing.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                ExternalRequestLedger.Reserve(options.Model, body);
                logger?.LogInformation("AI request dispatched: round={Round}; attempt={Attempt}; stream={Stream}", budget.ModelRounds, attempt + 1, options.StreamResponses);
                budget.RecordDiagnostic("dispatch", "Started", new
                {
                    requestSha256 = Convert.ToHexStringLower(SHA256.HashData(body)),
                    stream = options.StreamResponses,
                    inputReservation,
                    outputAllowance = request.MaxOutputTokens
                });
                stage = "response-headers";
                using var response = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                receivedHeaders = true;
                budget.RecordDiagnostic("response-headers", "Received", new
                {
                    httpStatus = (int)response.StatusCode,
                    elapsedMs = elapsed.ElapsedMilliseconds
                });
                if (!response.IsSuccessStatusCode)
                {
                    logger?.LogWarning("AI transport failed: stage=http-status; status={Status}; attempt={Attempt}; cloudflare={Cloudflare}; via={Via}",
                        (int)response.StatusCode, attempt + 1, response.Headers.Contains("cf-ray"), response.Headers.Contains("Via"));
                    throw new LlmException(Classify(response.StatusCode));
                }
                logger?.LogInformation("AI transport: stage=response-headers; stream={Stream}; elapsedMs={ElapsedMs}; attempt={Attempt}",
                    options.StreamResponses, elapsed.ElapsedMilliseconds, attempt + 1);
                if (options.StreamResponses)
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                }
                LlmCompletion result;
                stage = "response-protocol";
                try
                {
                    result = options.StreamResponses
                        ? await SseCompletionReader.ReadAsync(response.Content, options.MaxResponseBytes, request, timeout.Token, logger,
                            () => timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds)), budget)
                        : ChatCompletionsProtocol.Parse(await ReadBoundedAsync(response.Content, options.MaxResponseBytes, timeout.Token), request);
                }
                catch (LlmException ex) when (ex.Usage is not null)
                {
                    budget.ReconcileUsage(ex.Usage, inputReservation, request.MaxOutputTokens);
                    throw;
                }
                try
                {
                    await budget.RecordResponseAsync(result, Convert.ToHexStringLower(SHA256.HashData(body)), cancellationToken);
                    ExternalRequestLedger.RecordCompletion(body, result);
                    stage = "usage-reconciliation";
                    budget.ReconcileUsage(result.Usage, inputReservation, request.MaxOutputTokens);
                }
                catch (LlmException exception) when (exception.Failure == LlmFailure.BudgetExceeded)
                {
                    logger?.LogWarning("AI budget rejected: stage=usage-reconciliation; outputAllowance={OutputAllowance}; reportedInput={ReportedInput}; reportedOutput={ReportedOutput}; rounds={Rounds}; tools={Tools}",
                        request.MaxOutputTokens, result.Usage?.InputTokens, result.Usage?.OutputTokens, budget.ModelRounds, budget.ToolCalls);
                    throw;
                }
                cancellationToken.ThrowIfCancellationRequested();
                budget.RecordToolCalls(result.ToolCalls.Count);
                budget.RecordDiagnostic("model-call", "Completed", new
                {
                    elapsedMs = elapsed.ElapsedMilliseconds
                });
                return result;
            }
            catch (OperationCanceledException)
            {
                budget.RecordDiagnostic(stage, "CancelledOrTimedOut", new
                {
                    receivedHeaders,
                    runDeadline = budget.Deadline.IsCancellationRequested,
                    cancelled = cancellationToken.IsCancellationRequested,
                    elapsedMs = elapsed.ElapsedMilliseconds
                });
                logger?.LogWarning("AI transport ended: stage=cancel-or-timeout; headers={Headers}; cancelled={Cancelled}; runDeadline={RunDeadline}; elapsedMs={ElapsedMs}; attempt={Attempt}",
                    receivedHeaders, cancellationToken.IsCancellationRequested, budget.Deadline.IsCancellationRequested, elapsed.ElapsedMilliseconds, attempt + 1);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                throw new LlmException(LlmFailure.Timeout);
            }
            catch (HttpRequestException ex)
            {
                budget.RecordDiagnostic(stage, "TransportFailed", new
                {
                    error = ex.HttpRequestError.ToString(),
                    elapsedMs = elapsed.ElapsedMilliseconds
                });
                retryFailure = LlmFailure.Unavailable;
                logger?.LogWarning("AI transport failed: stage=http-transport; error={Error}; socket={Socket}; headers={Headers}; attempt={Attempt}",
                    ex.HttpRequestError, ex.InnerException is System.Net.Sockets.SocketException socket ? socket.SocketErrorCode.ToString() : "none",
                    receivedHeaders, attempt + 1);
                if (attempt >= options.MaxRetries)
                {
                    throw new LlmException(LlmFailure.Unavailable);
                }
            }
            catch (IOException)
            {
                budget.RecordDiagnostic(stage, "IoFailed", new
                {
                    elapsedMs = elapsed.ElapsedMilliseconds
                });
                retryFailure = LlmFailure.Unavailable;
                logger?.LogWarning("AI transport failed: stage=io; headers={Headers}; attempt={Attempt}", receivedHeaders, attempt + 1);
                if (attempt >= options.MaxRetries)
                {
                    throw new LlmException(LlmFailure.Unavailable);
                }
            }
            catch (LlmException ex) when (attempt < options.MaxRetries && ex.Failure is LlmFailure.Unavailable or LlmFailure.RateLimited)
            {
                retryFailure = ex.Failure;
                budget.RecordDiagnostic(stage, "RetryScheduled", new
                {
                    failureCode = ex.Failure.ToString(),
                    elapsedMs = elapsed.ElapsedMilliseconds
                });
                // Do not repeat invalid schemas, auth failures, refusals or truncated responses.
            }
            catch (LlmException ex)
            {
                budget.RecordDiagnostic(stage, "Rejected", new
                {
                    failureCode = ex.Failure.ToString(),
                    ex.ValidationRule,
                    elapsedMs = elapsed.ElapsedMilliseconds
                });
                throw;
            }
            using var backoff = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Deadline);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), backoff.Token);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                throw new LlmException(LlmFailure.Timeout);
            }
        }
    }

    private static LlmFailure Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LlmFailure.Unauthorized,
        HttpStatusCode.TooManyRequests => LlmFailure.RateLimited,
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => LlmFailure.Unavailable,
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => LlmFailure.UnsupportedRequest,
        _ => LlmFailure.InvalidResponse
    };

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
            {
                break;
            }
            if (read > maximumBytes - result.Length)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
