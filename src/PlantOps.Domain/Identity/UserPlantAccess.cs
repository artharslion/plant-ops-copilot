namespace PlantOps.Domain.Identity;

public sealed class UserPlantAccess
{
    public string UserId { get; set; } = "";

    public Guid PlantId
    {
        get; set;
    }

    public DateTimeOffset GrantedAt
    {
        get; set;
    }

    public string? GrantedByUserId
    {
        get; set;
    }
}
