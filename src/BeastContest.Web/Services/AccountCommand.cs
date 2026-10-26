using BeastContest.Web.Data;
using Microsoft.AspNetCore.Identity;
namespace BeastContest.Web.Services;
public static class AccountCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string Option(string key, string fallback="") { var i=Array.IndexOf(args,key); return i>=0 && i+1<args.Length ? args[i+1] : fallback; }
        var reset=args.Contains("--reset-admin");
        var login=Option(reset ? "--reset-admin" : "--create-user"); var role=reset?"Admin":Option("--role","Participant"); var name=Option("--name",login);
        Console.Write(role=="Participant"?"Password (6 characters): ":"Password (10-128 characters): ");
        string password;
        if(Console.IsInputRedirected) password=Console.ReadLine() ?? "";
        else {
            var buffer=new System.Text.StringBuilder();
            while(true) { var key=Console.ReadKey(true); if(key.Key==ConsoleKey.Enter) break;
                if(key.Key==ConsoleKey.Backspace) { if(buffer.Length>0) buffer.Length--; }
                else if(!char.IsControl(key.KeyChar) && buffer.Length<129) buffer.Append(key.KeyChar);
            }
            Console.WriteLine(); password=buffer.ToString();
        }
        var errors=AccountService.Validate(login,name,password,role);
        if(errors.Length>0) { Console.Error.WriteLine("Invalid account: "+string.Join(", ",errors)); return 1; }
        using var scope=services.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users=scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await using var tx=await db.Database.BeginTransactionAsync();
        if(reset) {
            var existing=await users.FindByNameAsync(login);
            if(existing is null || !await users.IsInRoleAsync(existing,"Admin")) {
                Console.Error.WriteLine("Administrator not found. No account was changed."); return 1;
            }
            var token=await users.GeneratePasswordResetTokenAsync(existing);
            var changed=await users.ResetPasswordAsync(existing,token,password);
            if(!changed.Succeeded) { Console.Error.WriteLine("Password reset failed."); return 1; }
            if(!(await users.SetLockoutEndDateAsync(existing,null)).Succeeded ||
               !(await users.ResetAccessFailedCountAsync(existing)).Succeeded) {
                Console.Error.WriteLine("Unable to clear login lockout. Password was not changed."); return 1;
            }
            await tx.CommitAsync(); Console.WriteLine($"Password reset for {login}."); return 0;
        }
        var user=new ApplicationUser {UserName=login,DisplayName=name.Trim()};
        var result=await users.CreateAsync(user,password);
        if(!result.Succeeded) { Console.Error.WriteLine(string.Join("; ",result.Errors.Select(e=>e.Description))); return 1; }
        result=await users.AddToRoleAsync(user,role);
        if(!result.Succeeded) { Console.Error.WriteLine("Unable to assign role."); return 1; }
        await tx.CommitAsync(); Console.WriteLine($"Created {login} ({role})."); return 0;
    }
}
