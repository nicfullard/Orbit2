using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Pages.Admin.Users;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.ApiKeys;

public class CreateModel(ApiKeyService apiKeys, DepartmentService departments, RoleService roles) : OrbitPageModel
{
    [BindProperty, Required, StringLength(200)] public string Name { get; set; } = "Claude Project";
    [BindProperty, Required] public Guid RoleId { get; set; }
    [BindProperty] public Guid? DepartmentId { get; set; }

    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public IReadOnlyList<RolePickerItem> RoleItems { get; private set; } = [];
    public CreatedApiKey? Created { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadLookupsAsync(ct);
        RoleId = UserForm.DefaultRole(RoleItems);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                Created = await apiKeys.CreateAsync(new ApiKeyInput { Name = Name, RoleId = RoleId, DepartmentId = DepartmentId }, ct);
                return Page();
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        await LoadLookupsAsync(ct);
        return Page();
    }

    private async Task LoadLookupsAsync(CancellationToken ct)
    {
        var items = new List<SelectListItem> { new("(none)", string.Empty, DepartmentId is null) };
        items.AddRange((await departments.ListAsync(false, ct)).Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == DepartmentId)));
        DepartmentItems = items;
        RoleItems = await roles.ListForPickerAsync(ct);
    }
}
