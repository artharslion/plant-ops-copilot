using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using PlantOps.Infrastructure.Identity;

namespace PlantOps.Api;

public static class AuthEndpoints
{
    public sealed record LoginInput(string UserName, string Password);
    public static void MapAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapGet("/csrf", (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(ctx).RequestToken });
        }).AllowAnonymous();
        auth.MapPost("/login", async (LoginInput input, UserManager<PlantOpsUser> users, SignInManager<PlantOpsUser> signIn) =>
        {
            if (string.IsNullOrWhiteSpace(input.UserName) || input.UserName.Length > 256 || string.IsNullOrEmpty(input.Password) || input.Password.Length > 1024)
                return Results.Problem(statusCode: 400, title: "Invalid login input.");
            var user = await users.FindByNameAsync(input.UserName);
            if (user == null || !user.IsEnabled) return Results.Problem(statusCode: 401, title: "Login failed.");
            var result = await signIn.PasswordSignInAsync(user, input.Password, false, lockoutOnFailure: true);
            return result.Succeeded ? Results.NoContent() : Results.Problem(statusCode: 401, title: "Login failed.");
        }).AllowAnonymous();
        auth.MapPost("/logout", async (SignInManager<PlantOpsUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();
        auth.MapGet("/me", async (HttpContext ctx, UserManager<PlantOpsUser> users) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var user = await users.GetUserAsync(ctx.User);
            return user is { IsEnabled: true }
                ? Results.Ok(new { user.Id, user.UserName, user.DisplayName, roles = await users.GetRolesAsync(user) })
                : Results.Problem(statusCode: 401, title: "Session unavailable.");
        }).RequireAuthorization();
    }
}
