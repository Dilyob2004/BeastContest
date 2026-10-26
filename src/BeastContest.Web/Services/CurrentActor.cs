using System.Security.Claims;
using BeastContest.Web.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace BeastContest.Web.Services;

public sealed record Actor(string Id, string Name, bool IsAdmin)
{
    public static readonly Actor Guest = new("", "", false);
    public bool IsAuthenticated => Id.Length > 0;
}
public interface ICurrentActor { Task<Actor> GetAsync(); Task<Actor> RequireAdminAsync(); }

public sealed class CurrentActor(AuthenticationStateProvider stateProvider, IServiceScopeFactory scopes) : ICurrentActor
{
    public async Task<Actor> GetAsync()
    {
        var principal = (await stateProvider.GetAuthenticationStateAsync()).User;
        if (principal.Identity?.IsAuthenticated != true) return Actor.Guest;
        using var scope = scopes.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await manager.GetUserAsync(principal);
        if (user is null || user.Disabled || user.SecurityStamp != principal.FindFirstValue("AspNet.Identity.SecurityStamp")) return Actor.Guest;
        return new(user.Id, user.DisplayName, await manager.IsInRoleAsync(user, "Admin"));
    }
    public async Task<Actor> RequireAdminAsync()
    {
        var actor = await GetAsync();
        if (!actor.IsAdmin) throw new UnauthorizedAccessException();
        return actor;
    }
}
