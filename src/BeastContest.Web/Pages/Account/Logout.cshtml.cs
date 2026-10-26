using BeastContest.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace BeastContest.Web.Pages.Account;
public sealed class LogoutModel(SignInManager<ApplicationUser> signIn) : PageModel
{
    public IActionResult OnGet()=>Redirect("/");
    public async Task<IActionResult> OnPostAsync(){await signIn.SignOutAsync();return Redirect("/");}
}
