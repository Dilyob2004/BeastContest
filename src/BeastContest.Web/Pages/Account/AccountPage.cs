using BeastContest.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace BeastContest.Web.Pages.Account;
public abstract class AccountPage(Translations translations) : PageModel
{
    public string Lang => Request.Query["lang"].ToString() is "ru" or "en" ? Request.Query["lang"].ToString()
        : Request.Cookies["beast.language"] == "en" ? "en" : "ru";
    public string T(string key) => translations.Get(Lang,key);
    public string[] Errors { get; set; } = [];
}
