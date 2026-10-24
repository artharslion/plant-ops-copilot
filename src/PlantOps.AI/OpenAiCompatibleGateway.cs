using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using PlantOps.Application.AI;

namespace PlantOps.AI;

public sealed class OpenAiCompatibleGateway(HttpClient client, IOptions<AiOptions> settings,
    ILogger<OpenAiCompatibleGateway>? logger = null) : ILlmGateway
{
    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
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
                    // Unknown usage keeps its reservation. If that leaves no room
                    // for a retry, preserve the original service/transport failure.
                    logger?.LogWarning("AI retry suppressed: stage=attempt-reservation; failure={Failure}; attempt={Attempt}",
                        retryFailure.Value, attempt + 1);
                    throw new LlmException(retryFailure.Value);
                }
                logger?.LogWarning("AI budget rejected: stage=attempt-reservation; inputReservation={InputReservation}; outputAllowance={OutputAllowance}; reservedInput={ReservedInput}; reservedOutput={ReservedOutput}; rounds={Rounds}; tools={Tools}",
                    inputReservation, request.MaxOutputTokens, budget.ReservedInputTokens, budget.ReservedOutputTokens, budget.ModelRounds, budget.ToolCalls);
                throw;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Deadline);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            var receivedHeaders = false;
            var elapsed = Stopwatch.StartNew();
            try
            {
                using var outgoing = new HttpRequestMessage(HttpMethod.Post, endpoint);
                outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                outgoing.Content = new ByteArrayContent(body);
                outgoing.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                logger?.LogInformation("AI request dispatched: round={Round}; attempt={Attempt}; stream={Stream}", budget.ModelRounds, attempt + 1, options.StreamResponses);
                using var response = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                receivedHeaders = true;
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
                var result = options.StreamResponses
                    ? await SseCompletionReader.ReadAsync(response.Content, options.MaxResponseBytes, request, timeout.Token, logger,
                        () => timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds)))
                    : ChatCompletionsProtocol.Parse(await ReadBoundedAsync(response.Content, options.MaxResponseBytes, timeout.Token), request);
                cancellationToken.ThrowIfCancellationRequested();
                budget.CheckDeadline();
                try
                {
                    budget.ReconcileUsage(result.Usage, inputReservation, request.MaxOutputTokens);
                }
                catch (LlmException exception) when (exception.Failure == LlmFailure.BudgetExceeded)
                {
                    logger?.LogWarning("AI budget rejected: stage=usage-reconciliation; outputAllowance={OutputAllowance}; reportedInput={ReportedInput}; reportedOutput={ReportedOutput}; rounds={Rounds}; tools={Tools}",
                        request.MaxOutputTokens, result.Usage?.InputTokens, result.Usage?.OutputTokens, budget.ModelRounds, budget.ToolCalls);
                    throw;
                }
                budget.RecordToolCalls(result.ToolCalls.Count);
                return result;
            }
            catch (OperationCanceledException)
            {
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
                // Do not repeat invalid schemas, auth failures, refusals or truncated responses.
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
