using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Domain.Identity;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Identity;

public static class DevelopmentIdentityInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, string environment)
    {
        if (environment != "Development") throw new InvalidOperationException("Demo accounts require Development.");
        var password = configuration["Demo:Password"];
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Set Demo:Password in local configuration before initialization.");
        var users = services.GetRequiredService<UserManager<PlantOpsUser>>();
        foreach (var validator in users.PasswordValidators)
            if (!(await validator.ValidateAsync(users, new PlantOpsUser(), password)).Succeeded)
                throw new InvalidOperationException("Demo:Password must satisfy Identity password policy (12+ characters, upper/lowercase, number and symbol).");
        var db = services.GetRequiredService<PlantOpsDbContext>();
        await DevelopmentDatabaseInitializer.InitializeAsync(db, environment);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7300103)");
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Viewer", "Operator", "MaintenanceEngineer", "Admin" })
            if (!await roles.RoleExistsAsync(role)) Check(await roles.CreateAsync(new IdentityRole(role)));
        var north = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var south = Guid.Parse("10000000-0000-0000-0000-000000000002");
        foreach (var (name, role, plants) in new[] {
            ("viewer", "Viewer", new[] { north }), ("operator", "Operator", new[] { north }),
            ("engineer", "MaintenanceEngineer", new[] { south }), ("admin", "Admin", new[] { north, south }) })
        {
            // Existing accounts retain passwords, roles, enablement and explicit grants/revocations.
            if (await users.FindByNameAsync(name) != null) continue;
            var user = new PlantOpsUser { UserName = name, DisplayName = $"Demo {role}" };
            Check(await users.CreateAsync(user, password));
            Check(await users.AddToRoleAsync(user, role));
            foreach (var plantId in plants)
                db.UserPlantAccess.Add(new UserPlantAccess { UserId = user.Id, PlantId = plantId, GrantedAt = DateTimeOffset.UtcNow });
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await transaction.DisposeAsync();
        await DevelopmentScaleInitializer.InitializeAsync(services, configuration);
    }
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Demo identity initialization failed; existing data preserved.");
    }
}
