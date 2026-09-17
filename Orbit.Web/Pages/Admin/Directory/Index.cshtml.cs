using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Orbit.Agents;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Directory;

public class IndexModel(LdapSettingsService settings, DirectoryAuthService directory, AgentConnectionRegistry registry) : OrbitPageModel
{
    [BindProperty] public LdapForm Form { get; set; } = new();
    /// <summary>Only used by "Test connection": an email to look up, proving the search base and filter.</summary>
    [BindProperty] public string? SampleUsername { get; set; }

    public bool HasBindPassword { get; private set; }
    public int AgentsAbleToSignIn { get; private set; }
    public DirectoryTestOutcome? Test { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var view = await LoadAsync(ct);
        Form = LdapForm.From(view.Settings);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await settings.UpdateAsync(Form.ToInput(), ct);
                Success(Form.Enabled ? "Directory sign-in settings saved." : "Settings saved. Directory sign-in is switched off.");
                return RedirectToPage();
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>Tests what is in the form - nothing is saved - so settings can be proven before they go live.</summary>
    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                Test = await directory.TestAsync(Form.ToInput(), SampleUsername, ct);
            }
            catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        await LoadAsync(ct);
        return Page();
    }

    private async Task<LdapSettingsView> LoadAsync(CancellationToken ct)
    {
        var view = await settings.GetAsync(ct);
        HasBindPassword = view.HasBindPassword;
        AgentsAbleToSignIn = registry.WithCapability(AgentCapabilities.LdapAuthenticate).Count;
        return view;
    }
}

public sealed class LdapForm
{
    // Checkboxes post nothing when unticked, so these must default to false for "unticked" to bind as false.
    public bool Enabled { get; set; }
    public bool UseSsl { get; set; }
    public bool ValidateCertificate { get; set; }

    [StringLength(255)] public string? Server { get; set; }
    [Range(1, 65535)] public int Port { get; set; } = 636;
    [StringLength(500)] public string? BindDn { get; set; }
    /// <summary>Write-only: never sent back to the browser. Blank keeps the stored password.</summary>
    [DataType(DataType.Password)] public string? BindPassword { get; set; }
    [StringLength(500)] public string? SearchBase { get; set; }
    [StringLength(500)] public string? UserFilter { get; set; }

    public LdapSettingsInput ToInput() => new()
    {
        Enabled = Enabled, Server = Server, Port = Port, UseSsl = UseSsl, ValidateCertificate = ValidateCertificate,
        BindDn = BindDn, NewBindPassword = BindPassword, SearchBase = SearchBase, UserFilter = UserFilter
    };

    public static LdapForm From(LdapSettings s) => new()
    {
        Enabled = s.Enabled, Server = s.Server, Port = s.Port, UseSsl = s.UseSsl, ValidateCertificate = s.ValidateCertificate,
        BindDn = s.BindDn, SearchBase = s.SearchBase, UserFilter = s.UserFilter
    };
}
