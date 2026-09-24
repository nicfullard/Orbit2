using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Sprints;

public class CreateModel(SprintService sprints, IActorProvider actors) : OrbitPageModel
{
    [BindProperty] public SprintForm Form { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageSprints(actor), "You don't have permission to manage sprints.");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var sprint = await sprints.CreateAsync(Form.ToInput(), ct);
                Success($"Sprint \"{sprint.Name}\" created.");
                return RedirectToPage("/Sprints/Details", new { id = sprint.Id });
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        return Page();
    }
}
