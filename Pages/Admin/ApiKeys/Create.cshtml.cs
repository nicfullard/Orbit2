using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.ApiKeys;

public class CreateModel(ApiKeyService apiKeys, DepartmentService departments) : OrbitPageModel
{
    [BindProperty, Required, StringLength(200)] public string Name { get; set; } = "Claude Project";
    [BindProperty] public OrbitRole Role { get; set; } = OrbitRole.Member;
    [BindProperty] public Guid? DepartmentId { get; set; }

    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public CreatedApiKey? Created { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        DepartmentItems = await BuildDepartmentsAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                Created = await apiKeys.CreateAsync(new ApiKeyInput { Name = Name, Role = Role, DepartmentId = DepartmentId }, ct);
                return Page();
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        DepartmentItems = await BuildDepartmentsAsync(ct);
        return Page();
    }

    private async Task<IReadOnlyList<SelectListItem>> BuildDepartmentsAsync(CancellationToken ct)
    {
        var items = new List<SelectListItem> { new("(none - System Admin key)", string.Empty, DepartmentId is null) };
        items.AddRange((await departments.ListAsync(false, ct)).Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == DepartmentId)));
        return items;
    }
}
