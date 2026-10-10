using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantOps.Application.AI;

namespace PlantOps.AI;

public static class DependencyInjection
{
    public static IServiceCollection AddPlantOpsAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection("AI"));
        services.AddTransient<IInvestigationEngine, InvestigationEngine>();
        services.AddHttpClient<OpenAiCompatibleGateway>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddTransient<FakeLlmGateway>(_ => new FakeLlmGateway());
        services.AddTransient<ILlmGateway>(provider => provider.GetRequiredService<IOptions<AiOptions>>().Value.Provider switch
        {
            "Fake" => provider.GetRequiredService<FakeLlmGateway>(),
            "OpenAiCompatible" => provider.GetRequiredService<OpenAiCompatibleGateway>(),
            _ => throw new LlmException(LlmFailure.InvalidConfiguration)
        });
        return services;
    }
}
