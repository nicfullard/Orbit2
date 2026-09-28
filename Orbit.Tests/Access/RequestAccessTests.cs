using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>Who may log requests and who may configure a department's request flows (spec §6.5, §6.20).</summary>
public class RequestAccessTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Finance = Guid.NewGuid();

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
    }

    /// <summary>REQ-008: whom a request may be logged for - yourself at Own, your department's people at Department, anyone at All.</summary>
    [Fact]
    public void Requests_are_logged_for_yourself_or_your_department()
    {
        var colleague = Guid.NewGuid();
        var outsider = Guid.NewGuid();

        var member = TestActors.Member(Finance);
        Assert.False(AccessPolicy.CanRequestForOthers(member));
        Assert.True(AccessPolicy.CanRequestFor(member, member.UserId!.Value, Finance));
        Assert.False(AccessPolicy.CanRequestFor(member, colleague, Finance));

        var lead = TestActors.Grants(Finance, (Permission.RequestsSubmit, PermissionScope.Department));
        Assert.True(AccessPolicy.CanRequestForOthers(lead));
        Assert.True(AccessPolicy.CanRequestFor(lead, lead.UserId!.Value, Finance));
        Assert.True(AccessPolicy.CanRequestFor(lead, colleague, Finance));
        Assert.False(AccessPolicy.CanRequestFor(lead, outsider, It));
        Assert.False(AccessPolicy.CanRequestFor(lead, outsider, null)); // someone with no department isn't in the lead's

        var admin = TestActors.SystemAdmin();
        Assert.True(AccessPolicy.CanRequestForOthers(admin));
        Assert.True(AccessPolicy.CanRequestFor(admin, outsider, It));
        Assert.True(AccessPolicy.CanRequestFor(admin, outsider, null));

        var nobody = TestActors.Nobody(Finance);
        Assert.False(AccessPolicy.CanRequestFor(nobody, nobody.UserId!.Value, Finance));

        // A Department grant ties the role to a department (§6.5, rule 3).
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
}
