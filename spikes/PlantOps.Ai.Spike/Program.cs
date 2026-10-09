using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;

var fake = args.Contains("--fake");
var outputIndex = Array.IndexOf(args, "--output");
if (outputIndex < 0 || outputIndex + 1 >= args.Length)
{
    Console.Error.WriteLine("Specify --output <new-report-path>; --fake is optional.");
    return 2;
}
var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var services = new ServiceCollection().AddLogging().AddPlantOpsAi(configuration);
await using var provider = services.BuildServiceProvider();
using var cancelled = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancelled.Cancel();
};
try
{
    var options = provider.GetRequiredService<IOptions<AiOptions>>().Value;
    if (!fake)
    {
        // Real checks must never silently become Fake when configuration is absent.
        if (options.Provider != "OpenAiCompatible")
        {
            throw new LlmException(LlmFailure.NotConfigured);
        }
        options.ValidateEndpoint();
    }
    var gateway = fake ? ProviderProbe.CreateFake() : provider.GetRequiredService<ILlmGateway>();
    var result = await ProviderProbe.RunAsync(gateway, options, fake, cancelled.Token);
    var path = Path.GetFullPath(args[outputIndex + 1]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        await JsonSerializer.SerializeAsync(file, result, new JsonSerializerOptions { WriteIndented = true }, cancelled.Token);
    }
    Console.WriteLine(fake ? $"Fake compatibility checks: {result.ChecksPassed}. No real provider was verified."
        : $"Real provider compatibility checks: {result.ChecksPassed}.");
    foreach (var check in result.Checks)
    {
        Console.WriteLine($"{check.Capability}: {check.Status} {check.Failure}");
    }
    return result.ChecksPassed ? 0 : 1;
}
catch (LlmException ex)
{
    Console.Error.WriteLine($"AI probe did not complete: {ex.Failure}. No credentials or response bodies are printed.");
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("AI probe cancelled.");
    return 2;
}
catch
{
    Console.Error.WriteLine("AI probe did not complete. Check local configuration and the new report path. No exception details are printed.");
    return 2;
}
