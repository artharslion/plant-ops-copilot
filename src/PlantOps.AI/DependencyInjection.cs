using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Net;
using PlantOps.Application.AI;

namespace PlantOps.AI;

public static class DependencyInjection
{
    public static IServiceCollection AddPlantOpsAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection("AI"));
        services.AddScoped<AiRunConfiguration>(provider => new(provider.GetRequiredService<IOptionsSnapshot<AiOptions>>().Value));
        services.AddScoped<IAiRunConfiguration>(provider => provider.GetRequiredService<AiRunConfiguration>());
        services.AddScoped<IOptions<AiOptions>>(provider => provider.GetRequiredService<AiRunConfiguration>());
        services.AddTransient<IInvestigationEngine, InvestigationEngine>();
        services.AddSingleton(provider => new AiConnectionFactory(
            (host, ct) => Dns.GetHostAddressesAsync(host, ct),
            (duration, ct) => Task.Delay(duration, ct),
            provider.GetRequiredService<ILogger<AiConnectionFactory>>()));
        services.AddHttpClient<OpenAiCompatibleGateway>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                ConnectCallback = provider.GetRequiredService<AiConnectionFactory>().ConnectAsync
            })
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
