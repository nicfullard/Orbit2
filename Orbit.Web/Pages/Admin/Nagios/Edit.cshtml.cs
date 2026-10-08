using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Nagios;

/// <summary>
/// A Nagios instance's settings (spec §6.21): where it is and how to sign in, what raises a task, and where the tasks go.
/// "Test connection" reads Nagios with what is on the form, saved or not, and shows what a check would do.
/// </summary>
public class EditModel(NagiosService nagios, DepartmentService departments) : OrbitPageModel
{
    public NagiosInstance? Item { get; private set; }
    [BindProperty] public NagiosForm Form { get; set; } = new();
    public bool HasPassword { get; private set; }
    public IReadOnlyList<NagiosAgentChoice> Agents { get; private set; } = [];
    public IReadOnlyList<Department> Departments { get; private set; } = [];
    public IReadOnlyList<UserSummary> SelectedAssignees { get; private set; } = [];
    public string? SaveError { get; private set; }
    public NagiosTestResult? Test { get; private set; }

    public async Task OnGetAsync(Guid? id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        Form = Item is null ? NagiosForm.New() : NagiosForm.From(Item);
        SelectedAssignees = await nagios.AssigneesAsync(Form.AssigneeIds, ct);
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, CancellationToken ct)
    {
        try
        {
            var saved = id is Guid existing ? await nagios.UpdateAsync(existing, Form.ToInput(), ct) : await nagios.CreateAsync(Form.ToInput(), ct);
            Success(saved.Enabled ? $"Nagios instance \"{saved.Name}\" saved." : $"Nagios instance \"{saved.Name}\" saved. It is switched off, so nothing is checked.");
            return RedirectToPage("/Admin/Nagios/Details", new { id = saved.Id });
        }
        catch (ValidationException ex) { SaveError = ex.Message; }
        await ReloadAsync(id, ct);
        return Page();
    }

    /// <summary>Tests what is in the form - nothing is saved, nothing is raised - so settings can be proven before they go live.</summary>
    public async Task<IActionResult> OnPostTestAsync(Guid? id, CancellationToken ct)
    {
        try
        {
            Test = await nagios.TestAsync(id, Form.ToInput(), ct);
        }
        catch (ValidationException ex) { SaveError = ex.Message; }
        await ReloadAsync(id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var view = await nagios.GetAsync(id, ct);
        await nagios.DeleteAsync(id, ct);
        Success($"Nagios instance \"{view.Instance.Name}\" deleted. The tasks it raised are kept.");
        return RedirectToPage("/Admin/Nagios/Index");
    }

    /// <summary>The Assignees picker's search: GET ?handler=People&amp;departmentId=...&amp;q=... - the people a task in that department may be assigned to.</summary>
    public async Task<IActionResult> OnGetPeopleAsync(Guid? departmentId, string? q, CancellationToken ct)
    {
        var people = await nagios.SearchAssigneesAsync(departmentId, q, ct);
        return new JsonResult(people.Select(u => new
        {
            id = u.Id, name = u.DisplayName, email = u.Email, department = u.DepartmentName,
            scope = u.CanViewAllTasks ? null : u.DepartmentId
        }));
    }

    private async Task ReloadAsync(Guid? id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        SelectedAssignees = await nagios.AssigneesAsync(Form.AssigneeIds, ct);
    }

    private async Task LoadAsync(Guid? id, CancellationToken ct)
    {
        if (id is Guid existing)
        {
            var view = await nagios.GetAsync(existing, ct);
            Item = view.Instance;
            HasPassword = view.HasPassword;
        }
        Agents = await nagios.AgentsAsync(ct);
        Departments = await departments.ListAsync(ct: ct);
    }
}

public sealed class NagiosForm
{
    // Checkboxes post nothing when unticked, so these must default to false for "unticked" to bind as false. New() ticks the usual ones.
    public bool Enabled { get; set; }
    public bool ValidateCertificate { get; set; }
    public bool RaiseHostDown { get; set; }
    public bool RaiseHostUnreachable { get; set; }
    public bool RaiseServiceCritical { get; set; }
    public bool RaiseServiceWarning { get; set; }
    public bool RaiseServiceUnknown { get; set; }
    public bool SkipScheduledDowntime { get; set; }
    public bool SkipAcknowledged { get; set; }

    public string? Name { get; set; }
    public string? BaseUrl { get; set; }
    public string? Username { get; set; }
    /// <summary>Write-only: never sent back to the browser. Blank keeps the stored password.</summary>
    [DataType(DataType.Password)] public string? Password { get; set; }
    public Guid? AgentId { get; set; }
    public int CheckIntervalMinutes { get; set; } = 5;
    public int HostThresholdMinutes { get; set; } = 15;
    public int ServiceThresholdMinutes { get; set; } = 15;
    public int MaxNewTasksPerCheck { get; set; } = 10;
    public Guid? DepartmentId { get; set; }
    public TaskPriority TaskPriority { get; set; } = TaskPriority.High;
    public List<Guid> AssigneeIds { get; set; } = [];

    public NagiosInstanceInput ToInput() => new()
    {
        Name = Name, BaseUrl = BaseUrl, Username = Username, NewPassword = Password, ValidateCertificate = ValidateCertificate,
        AgentId = AgentId, Enabled = Enabled, CheckIntervalMinutes = CheckIntervalMinutes,
        HostThresholdMinutes = HostThresholdMinutes, ServiceThresholdMinutes = ServiceThresholdMinutes,
        RaiseHostDown = RaiseHostDown, RaiseHostUnreachable = RaiseHostUnreachable,
        RaiseServiceCritical = RaiseServiceCritical, RaiseServiceWarning = RaiseServiceWarning, RaiseServiceUnknown = RaiseServiceUnknown,
        SkipScheduledDowntime = SkipScheduledDowntime, SkipAcknowledged = SkipAcknowledged, MaxNewTasksPerCheck = MaxNewTasksPerCheck,
        DepartmentId = DepartmentId, TaskPriority = TaskPriority, AssigneeIds = AssigneeIds
    };

    /// <summary>A new instance: down hosts and critical services raise tasks; downtime and acknowledged problems don't; switched off until tested.</summary>
    public static NagiosForm New() => new()
    {
        ValidateCertificate = true, RaiseHostDown = true, RaiseServiceCritical = true, SkipScheduledDowntime = true, SkipAcknowledged = true
    };

    public static NagiosForm From(NagiosInstance n) => new()
    {
        Name = n.Name, BaseUrl = n.BaseUrl, Username = n.Username, ValidateCertificate = n.ValidateCertificate,
        AgentId = n.AgentId, Enabled = n.Enabled, CheckIntervalMinutes = n.CheckIntervalMinutes,
        HostThresholdMinutes = n.HostThresholdMinutes, ServiceThresholdMinutes = n.ServiceThresholdMinutes,
        RaiseHostDown = n.RaiseHostDown, RaiseHostUnreachable = n.RaiseHostUnreachable,
        RaiseServiceCritical = n.RaiseServiceCritical, RaiseServiceWarning = n.RaiseServiceWarning, RaiseServiceUnknown = n.RaiseServiceUnknown,
        SkipScheduledDowntime = n.SkipScheduledDowntime, SkipAcknowledged = n.SkipAcknowledged, MaxNewTasksPerCheck = n.MaxNewTasksPerCheck,
        DepartmentId = n.DepartmentId, TaskPriority = n.TaskPriority, AssigneeIds = n.Assignees.Select(a => a.UserId).ToList()
    };
}
