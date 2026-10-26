using BeastContest.Web.Data;
using BeastContest.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace BeastContest.Web.Services;

public sealed record AccountSummary(string Id, string Login, string DisplayName, string Role, bool Disabled);
public sealed record ParticipantCredentials(string Login,string Password);

public sealed class AccountService(ICurrentActor actor, IServiceScopeFactory scopes)
{
    public static string GenerateParticipantPassword(){
        const string alphabet="ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        string password;
        do{password=new string(Enumerable.Range(0,6).Select(_=>alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());}while(password.Distinct().Count()<3);
        return password;
    }
    public async Task<ParticipantCredentials> CreateParticipantAsync(string login,string displayName){
        await actor.RequireAdminAsync();
        var password=GenerateParticipantPassword();
        await CreateAsync(login,displayName,password,"Participant");
        return new(login,password);
    }
    public async Task DeleteParticipantAsync(string id){
        var current=await actor.RequireAdminAsync();
        using var scope=scopes.CreateScope();
        var users=scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user=await users.FindByIdAsync(id)??throw new DomainException("users.notFound");
        if(current.Id==id||await users.IsInRoleAsync(user,"Admin"))throw new DomainException("users.deleteAdmin");
        var result=await users.DeleteAsync(user);
        if(!result.Succeeded)throw new DomainException("auth.failed");
    }
    private static bool ValidPassword(string password,string role)=>password.Distinct().Count()>=3 && (role=="Participant"?password.Length==6:password.Length is >=10 and <=128);
    public async Task<AccountSummary[]> GetAllAsync()
    {
        await actor.RequireAdminAsync();
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var all = await users.Users.OrderBy(u => u.UserName).ToArrayAsync();
        var result = new List<AccountSummary>();
        foreach(var u in all) result.Add(new(u.Id,u.UserName!,u.DisplayName,await users.IsInRoleAsync(u,"Admin") ? "Admin" : "Participant",u.Disabled));
        return result.ToArray();
    }

    public static string[] Validate(string login, string displayName, string password, string role)
    {
        var errors = new List<string>();
        if (!Regex.IsMatch(login, "^[a-zA-Z0-9_.-]{3,40}$")) errors.Add("auth.loginRules");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80) errors.Add("auth.nameRules");
        if (!ValidPassword(password,role)) errors.Add(role=="Participant"?"users.participantPasswordRules":"auth.passwordRules");
        if (role is not ("Admin" or "Participant")) errors.Add("auth.roleRules");
        return errors.ToArray();
    }

    public async Task CreateAsync(string login, string displayName, string password, string role)
    {
        await actor.RequireAdminAsync();
        var errors = Validate(login,displayName,password,role);
        if(errors.Length > 0) throw new DomainException(errors);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await using var tx = await db.Database.BeginTransactionAsync();
        var user = new ApplicationUser { UserName=login, DisplayName=displayName.Trim() };
        var result = await users.CreateAsync(user,password);
        if(!result.Succeeded) throw new DomainException(result.Errors.Any(e=>e.Code=="DuplicateUserName") ? "auth.duplicate" : "auth.passwordRules");
        var roleResult = await users.AddToRoleAsync(user,role);
        if(!roleResult.Succeeded) throw new DomainException("auth.failed");
        await tx.CommitAsync();
    }

    public async Task SetDisabledAsync(string id, bool disabled)
    {
        var current = await actor.RequireAdminAsync();
        if(current.Id == id) throw new DomainException("auth.cannotDisableSelf");
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(id) ?? throw new DomainException("auth.failed");
        user.Disabled=disabled;
        var result = await users.UpdateSecurityStampAsync(user);
        if(!result.Succeeded) throw new DomainException("auth.failed");
    }

    public async Task ResetPasswordAsync(string id, string password)
    {
        await actor.RequireAdminAsync();
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(id) ?? throw new DomainException("auth.failed");
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var role=await users.IsInRoleAsync(user,"Admin")?"Admin":"Participant";
        if(!ValidPassword(password,role))throw new DomainException(role=="Participant"?"users.participantPasswordRules":"auth.passwordRules");
        var result = await users.ResetPasswordAsync(user,token,password);
        if(!result.Succeeded) throw new DomainException("auth.passwordRules");
        await users.SetLockoutEndDateAsync(user,null);
        await users.ResetAccessFailedCountAsync(user);
    }
}
