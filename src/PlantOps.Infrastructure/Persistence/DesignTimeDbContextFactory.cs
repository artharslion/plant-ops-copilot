using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PlantOps.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlantOpsDbContext>
{
    public PlantOpsDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__PlantOps")
            ?? "Host=localhost;Database=plantops";
        return new PlantOpsDbContext(new DbContextOptionsBuilder<PlantOpsDbContext>()
            .UseNpgsql(connection).Options);
    }
}
