using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>Who may log requests, act on them, manage them, configure a department's request flows and write actions (spec §6.5, §6.20).</summary>
public class RequestAccessTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Finance = Guid.NewGuid();

    private static Request Request(Guid department, Guid requester, RequestStatus status = RequestStatus.InProgress) =>
        new() { DepartmentId = department, RequesterId = requester, Status = status };

    /// <summary>REQ-007: requests.submit at any scope lets anyone log a request with any department, whatever their tasks.create.</summary>
    [Fact]
    public void Logging_requests_reaches_every_department_without_creating_tasks_there()
    {
        var member = TestActors.Member(Finance);
        Assert.True(AccessPolicy.CanSubmitRequests(member));
        Assert.False(AccessPolicy.CanCreateTaskIn(member, It)); // the flow files the task, not tasks.create
        Assert.True(AccessPolicy.CanSubmitRequests(TestActors.DepartmentAdmin(It)));
        Assert.True(AccessPolicy.CanSubmitRequests(TestActors.SystemAdmin()));
        Assert.False(AccessPolicy.CanSubmitRequests(TestActors.Nobody(Finance)));
        Assert.True(AccessPolicy.CanSubmitRequests(TestActors.Grants(null, (Permission.RequestsSubmit, PermissionScope.Own))));

        // Only people log requests: the system (background jobs) has no one to file them for.
        Assert.False(AccessPolicy.CanSubmitRequests(Actor.System));

        // Holding it doesn't make a role company-wide: it has no All scope.
        Assert.DoesNotContain(PermissionScope.All, PermissionCatalog.ByKey[Permission.RequestsSubmit].AllowedScopes);
        Assert.False(member.Role.ReachesEverywhere);

        // At Department, Your requests also follows the department's requests; at Own only your own.
        Assert.False(AccessPolicy.CanSeeDepartmentsRequests(member));
        Assert.True(AccessPolicy.CanSeeDepartmentsRequests(TestActors.Grants(Finance, (Permission.RequestsSubmit, PermissionScope.Department))));
        Assert.True(RoleRules.RequiresDepartment(new Dictionary<string, PermissionScope> { [Permission.RequestsSubmit] = PermissionScope.Department }));
    }

    /// <summary>REQ-007: requests.configure at Department covers the actor's own department's flows only; at All, every department's.</summary>
    [Fact]
    public void Configuring_follows_the_department_scope()
    {
        var itAdmin = TestActors.DepartmentAdmin(It);
        Assert.True(AccessPolicy.CanConfigureRequestsIn(itAdmin, It));
        Assert.False(AccessPolicy.CanConfigureRequestsIn(itAdmin, Finance));
        Assert.False(AccessPolicy.CanConfigureRequestsIn(TestActors.Member(It), It));
        Assert.True(AccessPolicy.CanConfigureRequestsIn(TestActors.SystemAdmin(), Finance));
        Assert.True(AccessPolicy.CanConfigureRequestsIn(TestActors.Grants(null, (Permission.RequestsConfigure, PermissionScope.All)), Finance));

        var categories = new[]
        {
            new RequestCategory { Title = "IT", DepartmentId = It },
            new RequestCategory { Title = "Finance", DepartmentId = Finance }
        }.AsQueryable();
        Assert.Equal(new[] { "IT" }, Scoping.RequestCategories(categories, itAdmin).Select(c => c.Title));
        Assert.Equal(2, Scoping.RequestCategories(categories, TestActors.SystemAdmin()).Count());
        Assert.Empty(Scoping.RequestCategories(categories, TestActors.Member(It)));
    }

    /// <summary>
    /// REQ-020: a request is open to its requester, to anyone it was addressed to, and to requests.manage reaching its department (Department
    /// or All); acting on a step takes its assignee or a manager; an approval only its approver; cancelling the requester (while in progress)
    /// or a manager; retrying and skipping a manager; writing actions actions.create, which is All-only.
    /// </summary>
    [Fact]
    public void Requests_are_seen_by_their_people_and_managed_by_their_department()
    {
        var requester = TestActors.Member(Finance);
        var colleague = TestActors.Member(Finance);
        var itAdmin = TestActors.DepartmentAdmin(It);
        var financeAdmin = TestActors.DepartmentAdmin(Finance);
        var request = Request(It, requester.UserId!.Value);

        Assert.True(AccessPolicy.CanViewRequest(requester, request, false));
        Assert.False(AccessPolicy.CanViewRequest(colleague, request, false));
        Assert.True(AccessPolicy.CanViewRequest(colleague, request, true)); // addressed to them: a step or an approval
        Assert.True(AccessPolicy.CanViewRequest(itAdmin, request, false));
        Assert.False(AccessPolicy.CanViewRequest(financeAdmin, request, false)); // filed with IT, not Finance
        Assert.True(AccessPolicy.CanViewRequest(TestActors.SystemAdmin(), request, false));
        Assert.False(AccessPolicy.CanViewRequest(Actor.System, request, true));

        var step = new RequestStep { AssignedToId = colleague.UserId };
        Assert.True(AccessPolicy.CanActOnStep(colleague, request, step));
        Assert.False(AccessPolicy.CanActOnStep(requester, request, step));
        Assert.True(AccessPolicy.CanActOnStep(itAdmin, request, step));
        Assert.False(AccessPolicy.CanActOnStep(financeAdmin, request, step));

        var approval = new RequestApproval { ApproverId = colleague.UserId!.Value };
        Assert.True(AccessPolicy.CanDecide(colleague, approval));
        Assert.False(AccessPolicy.CanDecide(itAdmin, approval));
        Assert.False(AccessPolicy.CanDecide(TestActors.SystemAdmin(), approval));

        Assert.True(AccessPolicy.CanCancelRequest(requester, request));
        Assert.True(AccessPolicy.CanCancelRequest(itAdmin, request));
        Assert.False(AccessPolicy.CanCancelRequest(colleague, request));
        Assert.False(AccessPolicy.CanCancelRequest(requester, Request(It, requester.UserId.Value, RequestStatus.Completed)));
        Assert.True(AccessPolicy.CanRetryOrSkipStep(itAdmin, request));
        Assert.False(AccessPolicy.CanRetryOrSkipStep(requester, request));

        Assert.True(AccessPolicy.CanManageRequestsIn(TestActors.Grants(null, (Permission.RequestsManage, PermissionScope.All)), Finance));
        Assert.True(AccessPolicy.CanCreateActions(TestActors.Grants(null, (Permission.ActionsCreate, PermissionScope.All))));
        Assert.False(AccessPolicy.CanCreateActions(itAdmin));
        Assert.True(PermissionCatalog.ByKey[Permission.ActionsCreate].AllOnly);
        Assert.False(PermissionCatalog.ByKey[Permission.ActionsCreate].SystemAdministratorOnly);

        var requests = new[] { Request(It, requester.UserId.Value), Request(Finance, requester.UserId.Value) }.AsQueryable();
        Assert.Equal(new[] { It }, Scoping.Requests(requests, itAdmin).Select(r => r.DepartmentId));
        Assert.Equal(2, Scoping.Requests(requests, TestActors.SystemAdmin()).Count());
        Assert.Empty(Scoping.Requests(requests, requester));
    }
}
