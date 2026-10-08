using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Requests;

/// <summary>The requests filed with a department (spec §6.20), for whoever manages them (requests.manage): a filter and a list.</summary>
public class DepartmentModel(RequestService requests, IActorProvider actors) : OrbitPageModel
{
    public IReadOnlyList<Request> Items { get; private set; } = [];
    public IReadOnlyList<Department> Departments { get; private set; } = [];
    public RequestFilter Filter { get; private set; } = new();

    public async Task OnGetAsync(Guid? dept, RequestStatus? status, string? q, CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(actor.Has(Permission.RequestsManage), "You don't have permission to manage requests.");
        Departments = await requests.ManagedDepartmentsAsync(ct);
        Filter = new RequestFilter { DepartmentId = dept, Status = status, Query = q, Take = 200 };
        Items = await requests.DepartmentRequestsAsync(Filter, ct);
    }
}
