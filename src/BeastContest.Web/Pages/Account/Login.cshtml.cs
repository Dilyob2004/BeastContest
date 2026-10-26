using BeastContest.Web.Data;
using BeastContest.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
namespace BeastContest.Web.Pages.Account;

[Microsoft.AspNetCore.Authorization.AllowAnonymous]
[EnableRateLimiting("account")]
public sealed class LoginModel(Translations translations, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) : AccountPage(translations)
{
    [BindProperty] public string Login {get;set;} = "";
    [BindProperty] public string Password {get;set;} = "";
    public IActionResult OnGet() => Page();
    public async Task<IActionResult> OnPostAsync() {
        if(!ModelState.IsValid || Password.Length > 128 || Login.Length > 40) {Errors=["auth.invalidCredentials"];return Page();}
        var user=await users.FindByNameAsync(Login);
        if(user is null || user.Disabled) { Errors=["auth.invalidCredentials"];return Page(); }
        var result=await signIn.PasswordSignInAsync(user,Password,isPersistent:false,lockoutOnFailure:true);
        Password="";
        if(!result.Succeeded) { Errors=[result.IsLockedOut ? "auth.locked" : "auth.invalidCredentials"]; return Page(); }
        var returnUrl=Request.Query["ReturnUrl"].ToString();
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}


