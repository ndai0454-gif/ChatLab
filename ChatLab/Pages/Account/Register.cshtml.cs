using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ChatLab.Pages.Account;

public sealed class RegisterModel(UserManager<IdentityUser> userManager) : PageModel
{
    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = new IdentityUser
        {
            UserName = Input.UserName.Trim(),
            LockoutEnabled = true
        };
        var result = await userManager.CreateAsync(user, Input.Password);
        if (result.Succeeded)
        {
            return RedirectToPage("/Account/Login");
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }

        return Page();
    }

    public sealed class RegisterInput
    {
        [Required, StringLength(32, MinimumLength = 3)]
        [RegularExpression(@"^[a-zA-Z0-9._-]+$", ErrorMessage = "Use letters, numbers, dots, underscores, or hyphens.")]
        public string UserName { get; set; } = "";

        [Required, StringLength(100, MinimumLength = 12), DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [Required, Compare(nameof(Password)), DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = "";
    }
}
