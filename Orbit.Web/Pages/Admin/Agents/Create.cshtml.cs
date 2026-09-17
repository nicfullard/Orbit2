using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Application;
using Orbit.Application.Services;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Agents;

public class CreateModel(AgentService agents, IOptions<AppOptions> app) : OrbitPageModel
{
    [BindProperty, Required, StringLength(200)] public string Name { get; set; } = string.Empty;

    public AgentSetupModel? Setup { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                Setup = AgentSetupModel.For(await agents.CreateAsync(Name, ct), app.Value.BaseUrl);
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        return Page();
    }
}
