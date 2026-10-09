using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ChatLab.Pages.Account;

public sealed class LoginModel(SignInManager<IdentityUser> signInManager) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(
            Input.UserName.Trim(),
            Input.Password,
            Input.RememberMe,
            lockoutOnFailure: true);

        if (result.Succeeded)
        {
            return RedirectToPage("/Index");
        }

        ModelState.AddModelError(string.Empty,
            result.IsLockedOut
                ? "Too many failed attempts. Try again in 15 minutes."
                : "Invalid username or password.");
        return Page();
    }

    public sealed class LoginInput
    {
        [Required, StringLength(32)]
        public string UserName { get; set; } = "";

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = "";

        public bool RememberMe { get; set; }
    }
}
