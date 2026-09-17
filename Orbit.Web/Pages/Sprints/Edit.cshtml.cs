using Microsoft.AspNetCore.Mvc;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Sprints;

public class EditModel(SprintService sprints, IActorProvider actors) : OrbitPageModel
{
    [BindProperty] public SprintForm Form { get; set; } = new();
    public Sprint Sprint { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageSprints(actor), "Only a System Admin can manage sprints.");
        Sprint = await sprints.GetAsync(id, ct);
        Form = SprintForm.From(Sprint);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        Sprint = await sprints.GetAsync(id, ct);
        if (ModelState.IsValid)
        {
            try
            {
                var s = await sprints.UpdateAsync(id, Form.ToInput(), ct);
                Success($"Sprint \"{s.Name}\" saved.");
                return RedirectToPage("/Sprints/Details", new { id });
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        return Page();
    }
}
