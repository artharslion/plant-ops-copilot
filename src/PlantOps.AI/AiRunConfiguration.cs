using Microsoft.Extensions.Options;
using PlantOps.Application.AI;

namespace PlantOps.AI;

// One immutable server profile and one mutable selection per execution scope.
internal sealed class AiRunConfiguration(AiOptions defaults) : IAiRunConfiguration, IOptions<AiOptions>
{
    public AiOptions Value { get; } = defaults.Clone();

    public string DefaultProvider => defaults.Provider;

    public string RealModel => defaults.Model;

    public bool RealConfigured
    {
        get
        {
            var real = defaults.Clone();
            real.Provider = "OpenAiCompatible";
            try
            {
                real.ValidateEndpoint();
                return true;
            }
            catch (LlmException) { return false; }
        }
    }

    public void Select(string provider, string model)
    {
        if (provider is not ("Fake" or "OpenAiCompatible") || provider == "Fake" && model != "fake"
            || provider == "OpenAiCompatible" && model != defaults.Model)
        {
            throw new LlmException(LlmFailure.InvalidConfiguration);
        }
        Value.Provider = provider;
        Value.Model = model;
    }
}
