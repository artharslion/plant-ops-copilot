using PlantOps.Application.Assets;

namespace PlantOps.Application.AI;

public sealed record AiSettingsDto(string Provider, string RealModel, bool RealConfigured, long Version);

public sealed record AiSettingsInput(string Provider, long Version);

public sealed record AiModelSelection(string Provider, string Model);

public interface IAiRunConfiguration
{
    string DefaultProvider
    {
        get;
    }

    string RealModel
    {
        get;
    }

    bool RealConfigured
    {
        get;
    }

    void Select(string provider, string model);
}

public interface IAiSettingsService
{
    Task<AiSettingsDto> GetAsync(Actor actor, CancellationToken ct = default);

    Task<AiSettingsDto> SaveAsync(Actor actor, AiSettingsInput input, CancellationToken ct = default);

    Task<AiModelSelection> SelectionAsync(CancellationToken ct = default);
}
