using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using PlantOps.Application.AI;

namespace PlantOps.AI;

public sealed class OpenAiCompatibleGateway(HttpClient client, IOptions<AiOptions> settings) : ILlmGateway
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
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ReserveAttempt(inputReservation, request.MaxOutputTokens);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Deadline);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            try
            {
                using var outgoing = new HttpRequestMessage(HttpMethod.Post, endpoint);
                outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                outgoing.Content = new ByteArrayContent(body);
                outgoing.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new LlmException(Classify(response.StatusCode));
                }
                var result = options.StreamResponses
                    ? await SseCompletionReader.ReadAsync(response.Content, options.MaxResponseBytes, request, timeout.Token)
                    : ChatCompletionsProtocol.Parse(await ReadBoundedAsync(response.Content, options.MaxResponseBytes, timeout.Token), request);
                cancellationToken.ThrowIfCancellationRequested();
                budget.CheckDeadline();
                budget.ReconcileUsage(result.Usage, inputReservation, request.MaxOutputTokens);
                budget.RecordToolCalls(result.ToolCalls.Count);
                return result;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                throw new LlmException(LlmFailure.Timeout);
            }
            catch (HttpRequestException)
            {
                if (attempt >= options.MaxRetries)
                {
                    throw new LlmException(LlmFailure.Unavailable);
                }
            }
            catch (IOException)
            {
                if (attempt >= options.MaxRetries)
                {
                    throw new LlmException(LlmFailure.Unavailable);
                }
            }
            catch (LlmException ex) when (attempt < options.MaxRetries && ex.Failure is LlmFailure.Unavailable or LlmFailure.RateLimited)
            {
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
