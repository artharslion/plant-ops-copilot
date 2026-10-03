using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Assets;
using PlantOps.Domain.Identity;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class AssetManagementTests
{
    private static readonly Guid North = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid South = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Utilities = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ProcessHall = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Pump = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid Fan = Guid.Parse("30000000-0000-0000-0000-000000000002");

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Enabled) throw new InvalidOperationException("Injected failure after database statements, before commit.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Session : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }
        public FailAfterSave Failure { get; } = new();
        public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "a!";
        public string Name { get; } = "test-" + Guid.NewGuid().ToString("N");
        public string UserId { get; private set; } = "";
        public string Token { get; private set; } = "";
        public Session()
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["ConnectionStrings:PlantOps"] = TestDatabase.Connection, ["Demo:Password"] = Password,
                    ["Logging:LogLevel:Default"] = "Warning" }));
                builder.ConfigureServices(services => services.AddDbContext<PlantOpsDbContext>(options => options.AddInterceptors(Failure)));
            });
            Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        }
        public async Task Initialize(string role = "Admin", bool both = false)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            await DevelopmentIdentityInitializer.InitializeAsync(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<IConfiguration>(), "Development");
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PlantOpsUser>>();
            var user = new PlantOpsUser { UserName = Name, DisplayName = "Integration test" };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            UserId = user.Id;
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            db.UserPlantAccess.Add(new UserPlantAccess { UserId = user.Id, PlantId = North, GrantedAt = DateTimeOffset.UtcNow });
            if (both) db.UserPlantAccess.Add(new UserPlantAccess { UserId = user.Id, PlantId = South, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        public async Task RefreshToken()
        {
            var data = await Client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/auth/csrf");
            Token = data!["token"];
            Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", Token);
        }
        public async Task Login()
        {
            await RefreshToken();
            Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsJsonAsync("/api/v1/auth/login", new { UserName = Name, Password })).StatusCode);
            await RefreshToken();
        }
        public async Task<DirectoryRecord> Get(string kind, Guid id) =>
            (await Client.GetFromJsonAsync<DirectoryRecord>($"/api/v1/{kind}/{id}"))!;
        public async Task<DirectoryRecord> Create(string kind, Guid plantId, Guid? parent = null)
        {
            var input = new DirectoryInput("TEST-" + Guid.NewGuid().ToString("N"), "Test resource", ParentLocationId: parent, LocationId: Utilities);
            var response = await Client.PostAsJsonAsync($"/api/v1/plants/{plantId}/{kind}", input);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<DirectoryRecord>())!;
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }
    private static DirectoryInput Edit(DirectoryRecord x, string? name = null, Guid? parent = null, Guid? location = null, bool? archive = null) =>
        new(x.Code, name ?? x.Name, x.Version, archive ?? x.IsArchived, x.Description, parent ?? x.ParentLocationId, x.SortOrder,
            x.DisplayTimeZone, location ?? x.LocationId, x.AssetType, x.LifecycleStatus, x.Criticality, x.Manufacturer, x.Model, x.SerialNumber, x.InstalledAt);

    [DatabaseFact]
    public async Task AnonymousBusinessRequestsReturn401AndLoginRequiresAntiforgery()
    {
        await using var s = new Session();
        await s.Initialize();
        foreach (var path in new[] { "/api/v1/plants", $"/api/v1/assets/{Pump}", "/api/v1/auth/me" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{Pump}", new DirectoryInput("P-101", "No access"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/v1/auth/login", new { UserName = s.Name, s.Password })).StatusCode);
        await s.Login();
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        var pump = await s.Get("assets", Pump);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{Pump}", Edit(pump))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        await s.RefreshToken();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [DatabaseFact]
    public async Task ReaderRolesCanBrowseButCannotManageEvenWithPlantAccess()
    {
        foreach (var role in new[] { "Viewer", "Operator", "MaintenanceEngineer" })
        {
            await using var s = new Session();
            await s.Initialize(role); await s.Login();
            var pump = await s.Get("assets", Pump);
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{Pump}", Edit(pump))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Client.PostAsJsonAsync("/api/v1/plants", new DirectoryInput("TEST", "Denied"))).StatusCode);
            foreach (var kind in new[] { "locations", "assets" })
                Assert.Equal(HttpStatusCode.Forbidden, (await s.Client.PostAsJsonAsync($"/api/v1/plants/{North}/{kind}", new DirectoryInput("TEST", "Denied", LocationId: Utilities))).StatusCode);
        }
    }

    [DatabaseFact]
    public async Task AdminStillNeedsScopeForListsIdsAndWrites()
    {
        await using var s = new Session();
        await s.Initialize(); await s.Login();
        var plants = await s.Client.GetFromJsonAsync<DirectoryPage>("/api/v1/plants");
        Assert.Single(plants!.Items); Assert.Equal(North, plants.Items[0].Id);
        foreach (var path in new[] { $"plants/{South}", $"plants/{South}/locations", $"locations/{ProcessHall}", $"assets/{Fan}", $"assets?plantId={South}", $"assets?plantId={North}&locationId={ProcessHall}" })
            Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync("/api/v1/" + path)).StatusCode);
        foreach (var (kind, id) in new[] { ("plants", South), ("locations", ProcessHall), ("assets", Fan) })
            Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PatchAsJsonAsync($"/api/v1/{kind}/{id}", new DirectoryInput("TEST", "Denied"))).StatusCode);
        foreach (var kind in new[] { "locations", "assets" })
            Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync($"/api/v1/plants/{South}/{kind}", new DirectoryInput("TEST", "Denied", LocationId: ProcessHall))).StatusCode);
    }

    [DatabaseFact]
    public async Task LocationSelfReferenceCycleAndCrossPlantParentAreRejected()
    {
        await using var s = new Session(); await s.Initialize(both: true); await s.Login();
        var root = await s.Create("locations", North);
        var child = await s.Create("locations", North, root.Id);
        foreach (var parent in new[] { root.Id, child.Id, ProcessHall })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PatchAsJsonAsync($"/api/v1/locations/{root.Id}", Edit(root, parent: parent))).StatusCode);
        var other = await s.Create("locations", North);
        var replies = await Task.WhenAll(
            s.Client.PatchAsJsonAsync($"/api/v1/locations/{root.Id}", Edit(root, parent: other.Id)),
            s.Client.PatchAsJsonAsync($"/api/v1/locations/{other.Id}", Edit(other, parent: root.Id)));
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.BadRequest);
    }

    [DatabaseFact]
    public async Task AssetMovesOnlyWithinPlantAndConcurrentEditReturns409()
    {
        await using var s = new Session(); await s.Initialize(both: true); await s.Login();
        var asset = await s.Create("assets", North);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{asset.Id}", Edit(asset, location: ProcessHall))).StatusCode);
        var location = await s.Create("locations", North);
        var replies = await Task.WhenAll(
            s.Client.PatchAsJsonAsync($"/api/v1/assets/{asset.Id}", Edit(asset, "First", location: location.Id)),
            s.Client.PatchAsJsonAsync($"/api/v1/assets/{asset.Id}", Edit(asset, "Second", location: location.Id)));
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.Conflict);
        var current = await s.Get("assets", asset.Id);
        Assert.Equal(asset.Version + 1, current.Version); Assert.Equal(location.Id, current.LocationId);
    }

    [DatabaseFact]
    public async Task AuditAndBusinessChangeRollbackTogetherAfterSqlExecution()
    {
        await using var s = new Session(); await s.Initialize(); await s.Login();
        var asset = await s.Create("assets", North);
        s.Failure.Enabled = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{asset.Id}", Edit(asset, "Must roll back"))).StatusCode);
        s.Failure.Enabled = false;
        var current = await s.Get("assets", asset.Id);
        Assert.Equal(asset.Name, current.Name); Assert.Equal(asset.Version, current.Version);
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Single(await db.AuditLogs.Where(x => x.ResourceId == asset.Id).ToListAsync());
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{asset.Id}", Edit(current, "Committed"))).StatusCode);
        var audit = await db.AuditLogs.SingleAsync(x => x.ResourceId == asset.Id && x.Action == "Update");
        Assert.Equal(s.UserId, audit.ActorUserId); Assert.Equal(North, audit.PlantId);
        Assert.Contains("Committed", audit.After); Assert.Contains(asset.Name, audit.Before!); Assert.NotEmpty(audit.CorrelationId);
    }

    [DatabaseFact]
    public async Task ArchivePreservesResourcesAndAuditHistoryAndBlocksActiveChildren()
    {
        await using var s = new Session(); await s.Initialize(); await s.Login();
        var create = await s.Client.PostAsJsonAsync("/api/v1/plants", new DirectoryInput("TEST-" + Guid.NewGuid().ToString("N"), "Archive test"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var plant = (await create.Content.ReadFromJsonAsync<DirectoryRecord>())!;
        var location = await s.Create("locations", plant.Id);
        var createAsset = await s.Client.PostAsJsonAsync($"/api/v1/plants/{plant.Id}/assets", new DirectoryInput("A", "Archive asset", LocationId: location.Id));
        Assert.Equal(HttpStatusCode.Created, createAsset.StatusCode);
        var asset = (await createAsset.Content.ReadFromJsonAsync<DirectoryRecord>())!;
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PatchAsJsonAsync($"/api/v1/plants/{plant.Id}", Edit(plant, archive: true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PatchAsJsonAsync($"/api/v1/locations/{location.Id}", Edit(location, archive: true))).StatusCode);
        foreach (var (kind, record) in new[] { ("assets", asset), ("locations", location), ("plants", plant) })
        {
            Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync($"/api/v1/{kind}/{record.Id}", Edit(record, archive: true))).StatusCode);
            Assert.True((await s.Get(kind, record.Id)).IsArchived);
        }
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Equal(6, await db.AuditLogs.CountAsync(x => x.PlantId == plant.Id));
        Assert.True(await db.Assets.AnyAsync(x => x.Id == asset.Id));
    }

    [DatabaseFact]
    public async Task RepeatedSeedPreservesAccountRevocationsAndPasswordAndAssetEdits()
    {
        await using var s = new Session(); await s.Initialize(); await s.Login();
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PlantOpsUser>>();
        var demo = (await users.FindByNameAsync("viewer"))!;
        var hash = demo.PasswordHash;
        var enabled = demo.IsEnabled;
        var grants = await db.UserPlantAccess.Where(x => x.UserId == demo.Id).ToListAsync();
        var originalRoles = await users.GetRolesAsync(demo);
        db.UserPlantAccess.RemoveRange(grants); await db.SaveChangesAsync();
        await users.RemoveFromRolesAsync(demo, originalRoles);
        demo.IsEnabled = false; await users.UpdateAsync(demo);
        try
        {
            await DevelopmentIdentityInitializer.InitializeAsync(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<IConfiguration>(), "Development");
            Assert.False(demo.IsEnabled); Assert.Equal(hash, demo.PasswordHash);
            Assert.Empty(await db.UserPlantAccess.Where(x => x.UserId == demo.Id).ToListAsync());
            Assert.Empty(await users.GetRolesAsync(demo));
            Assert.Equal(2, await db.Plants.CountAsync(x => x.Id == North || x.Id == South));
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally
        {
            demo.IsEnabled = enabled; await users.UpdateAsync(demo);
            db.UserPlantAccess.AddRange(grants); await db.SaveChangesAsync();
            await users.AddToRolesAsync(demo, originalRoles);
        }
    }

    [DatabaseFact]
    public async Task ScopeAndRoleRevocationsApplyToExistingCookie()
    {
        await using var s = new Session(); await s.Initialize(); await s.Login();
        var asset = await s.Create("assets", North);
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var access = await db.UserPlantAccess.SingleAsync(x => x.UserId == s.UserId && x.PlantId == North);
        db.UserPlantAccess.Remove(access); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/v1/assets/{asset.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{asset.Id}", Edit(asset))).StatusCode);
        db.UserPlantAccess.Add(access); await db.SaveChangesAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PlantOpsUser>>();
        var user = (await users.FindByIdAsync(s.UserId))!;
        await users.RemoveFromRoleAsync(user, "Admin"); await users.AddToRoleAsync(user, "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{asset.Id}", Edit(asset))).StatusCode);
        user.IsEnabled = false; await users.UpdateAsync(user);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [DatabaseFact]
    public async Task DuplicateCodeFailsWithoutAuditAndListsHaveBoundedCursorPages()
    {
        await using var s = new Session(); await s.Initialize(); await s.Login();
        var asset = await s.Create("assets", North);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/v1/plants/{North}/assets", Edit(asset))).StatusCode);
        var page = (await s.Client.GetFromJsonAsync<DirectoryPage>($"/api/v1/assets?plantId={North}&limit=1"))!;
        Assert.Single(page.Items); Assert.NotNull(page.NextCursor);
        var next = (await s.Client.GetFromJsonAsync<DirectoryPage>($"/api/v1/assets?plantId={North}&limit=1&cursor={Uri.EscapeDataString(page.NextCursor!)}"))!;
        Assert.Single(next.Items); Assert.NotEqual(page.Items[0].Id, next.Items[0].Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync($"/api/v1/assets?plantId={North}&limit=201")).StatusCode);
    }

    [DatabaseFact]
    public async Task PlantAndLocationUpdatesRequireVersionAndErrorsIncludeCorrelation()
    {
        await using var s = new Session(); await s.Initialize(); await s.Login();
        var create = await s.Client.PostAsJsonAsync("/api/v1/plants", new DirectoryInput("TEST-" + Guid.NewGuid().ToString("N"), "Version plant"));
        var plant = (await create.Content.ReadFromJsonAsync<DirectoryRecord>())!;
        var location = await s.Create("locations", plant.Id);
        foreach (var (kind, record) in new[] { ("plants", plant), ("locations", location) })
        {
            Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync($"/api/v1/{kind}/{record.Id}", Edit(record, "Updated"))).StatusCode);
            var conflict = await s.Client.PatchAsJsonAsync($"/api/v1/{kind}/{record.Id}", Edit(record, "Stale"));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Contains("correlationId", await conflict.Content.ReadAsStringAsync());
        }
        await s.Client.PostAsync("/api/v1/auth/logout", null);
        var denied = await s.Client.GetAsync("/api/v1/plants");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Contains("correlationId", await denied.Content.ReadAsStringAsync());
    }

    [DatabaseFact]
    public async Task ForgedAndOtherUsersAntiforgeryTokensAreRejected()
    {
        await using var first = new Session(); await first.Initialize(); await first.Login();
        await using var second = new Session(); await second.Initialize(); await second.Login();
        var asset = await first.Get("assets", Pump);
        foreach (var token in new[] { "forged", second.Token })
        {
            first.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            first.Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
            Assert.Equal(HttpStatusCode.BadRequest, (await first.Client.PatchAsJsonAsync($"/api/v1/assets/{Pump}", Edit(asset))).StatusCode);
        }
        Assert.Equal(asset.Version, (await first.Get("assets", Pump)).Version);
    }

    [DatabaseFact]
    public async Task HealthProbesIgnoreSignedInCookieWhenIdentityDatabaseIsOffline()
    {
        await using var s = new Session(); await s.Initialize(); await s.Login();
        s.Factory.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:PlantOps"] =
            "Host=127.0.0.1;Port=59999;Database=plantops_tests;Username=plantops;Timeout=1;Command Timeout=1";
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await s.Client.GetAsync("/health/ready")).StatusCode);
    }
}

