using Microsoft.AspNetCore.Identity;

namespace PlantOps.Infrastructure.Identity;

public sealed class PlantOpsUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
}
