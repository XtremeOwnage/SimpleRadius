using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SimpleRadius.Pages.Account;

/// <summary>
/// Shown when the provider authenticated someone who lacks the required role. Anonymous, otherwise the
/// browser bounces between here and the provider.
/// </summary>
[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public void OnGet()
    {
    }
}
