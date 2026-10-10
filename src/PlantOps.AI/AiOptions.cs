using PlantOps.Application.AI;

namespace PlantOps.AI;

public sealed class AiOptions
{
    public string Provider { get; set; } = "Fake";

    public string BaseUrl { get; set; } = "https://zzone.cc.cd/v1";

    public string Model { get; set; } = "gpt-6.1-sol";

    public string ApiKey { get; set; } = string.Empty;

    public string? ReasoningEffort { get; set; } = "low";

    public int RequestTimeoutSeconds { get; set; } = 30;

    public int RunTimeoutSeconds { get; set; } = 60;

    public int MaxModelRounds { get; set; } = 4;

    public int MaxToolCalls { get; set; } = 8;

    public int MaxInputTokens { get; set; } = 16000;

    public int MaxOutputTokens { get; set; } = 2000;

    public int MaxRetries { get; set; } = 1;

    public int MaxResponseBytes { get; set; } = 131072;

    public LlmLimits Limits => new(RunTimeoutSeconds, MaxModelRounds, MaxToolCalls, MaxInputTokens, MaxOutputTokens);

    public Uri ValidateEndpoint()
    {
        if (Provider != "OpenAiCompatible" || !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Model) || Model.Length > 200 || Model.Any(char.IsControl)
            || (!string.IsNullOrEmpty(ReasoningEffort) && ReasoningEffort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            || RequestTimeoutSeconds is < 1 or > 60 || MaxRetries is < 0 or > 1
            || MaxResponseBytes is < 1024 or > 262144)
        {
            throw new LlmException(LlmFailure.InvalidConfiguration);
        }
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new LlmException(LlmFailure.NotConfigured);
        }
        if (ApiKey.Length > 4096 || ApiKey.Any(char.IsWhiteSpace) || ApiKey.Any(char.IsControl))
        {
            throw new LlmException(LlmFailure.InvalidConfiguration);
        }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
    }
}
