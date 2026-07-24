using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleRadius.Models;

namespace SimpleRadius.Pages.Account;

/// <summary>Signs the operator out locally and at the identity provider.</summary>
public class LogoutModel : PageModel
{
    private readonly AuthenticationSettings _authentication;

    public LogoutModel(AuthenticationSettings authentication)
    {
        _authentication = authentication;
    }

    public IActionResult OnGet() => RedirectToPage("/Index");

    public IActionResult OnPost()
    {
        if (!_authentication.IsOidcEnabled)
        {
            return RedirectToPage("/Index");
        }

        return SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme);
    }
}
