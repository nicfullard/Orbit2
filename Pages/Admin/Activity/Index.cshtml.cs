using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Admin.Activity;

public class IndexModel(AuditService audit, DepartmentService departments) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? EntityType { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? DepartmentId { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public PagedResult<AuditLog> Result { get; private set; } = null!;
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public static readonly string[] EntityTypes = [AuditEntity.Task, AuditEntity.Project, AuditEntity.Sprint, AuditEntity.RecurringTaskDefinition, AuditEntity.Department, AuditEntity.User, AuditEntity.ApiKey];

    public async Task OnGetAsync(CancellationToken ct)
    {
        To ??= DateTime.UtcNow;
        From ??= To.Value.AddDays(-7);
        Result = await audit.ListAsync(new AuditFilter
        {
            From = AuditService.AsUtc(From),
            To = AuditService.AsUtc(To),
            EntityType = EntityType,
            DepartmentId = DepartmentId,
            Page = PageNumber,
            PageSize = 100
        }, ct);
        DepartmentItems = (await departments.ListAsync(true, ct))
            .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == DepartmentId)).ToList();
    }
}
