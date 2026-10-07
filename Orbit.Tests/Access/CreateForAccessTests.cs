using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>
/// The requestee (spec §6.2.2, §6.5): who may create a task for someone else (tasks.create_for), the rights the person it is for
/// gets on it, and the lists that follow from that.
/// </summary>
public class CreateForAccessTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Finance = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();

    private static Actor Creator(PermissionScope scope, Guid? department, Guid? userId = null) =>
        TestActors.With(new Dictionary<string, PermissionScope> { [Permission.TasksCreateFor] = scope }, department, userId: userId);

    /// <summary>OBO-001: the catalogue entry - a task permission with every scope, not reserved and not in the Admin menu.</summary>
    [Fact]
    public void Create_tasks_for_others_is_a_task_permission_with_every_scope()
    {
        var p = PermissionCatalog.ByKey[Permission.TasksCreateFor];
        Assert.Equal(PermissionCatalog.TasksGroup, p.Group);
        Assert.Equal(new[] { PermissionScope.Own, PermissionScope.Department, PermissionScope.All }, p.AllowedScopes);
        Assert.False(p.SystemAdministratorOnly);
        Assert.DoesNotContain(Permission.TasksCreateFor, PermissionCatalog.AdminPermissions);

        // A grant at All badges the role company-wide (accepted); a grant at Department ties it to a department (rule 3).
        Assert.True(Creator(PermissionScope.All, null).Role.ReachesEverywhere);
        Assert.False(Creator(PermissionScope.Department, Finance).Role.ReachesEverywhere);
        Assert.True(RoleRules.RequiresDepartment(new Dictionary<string, PermissionScope> { [Permission.TasksCreateFor] = PermissionScope.Department }));
        Assert.False(RoleRules.RequiresDepartment(new Dictionary<string, PermissionScope> { [Permission.TasksCreateFor] = PermissionScope.All }));
    }

    /// <summary>OBO-002: the shipped roles hold it at Own - they name nobody else - and the built-in role reaches anyone.</summary>
    [Fact]
    public void Shipped_roles_create_tasks_only_for_themselves()
    {
        Assert.Equal(PermissionScope.Own, DefaultRoles.MemberGrants[Permission.TasksCreateFor]);
        Assert.Equal(PermissionScope.Own, DefaultRoles.DepartmentAdminGrants[Permission.TasksCreateFor]);

        foreach (var shipped in new[] { TestActors.Member(Finance), TestActors.DepartmentAdmin(Finance) })
        {
            Assert.False(AccessPolicy.CanCreateTasksForOthers(shipped));
            Assert.True(AccessPolicy.CanCreateTaskFor(shipped, shipped.UserId!.Value, Finance));
            Assert.False(AccessPolicy.CanCreateTaskFor(shipped, Guid.NewGuid(), Finance));
            Assert.False(shipped.Role.ReachesEverywhere);
        }

        var admin = TestActors.SystemAdmin();
        Assert.True(AccessPolicy.CanCreateTasksForOthers(admin));
        Assert.True(AccessPolicy.CanCreateTaskFor(admin, Guid.NewGuid(), It));
        Assert.True(AccessPolicy.CanCreateTaskFor(admin, Guid.NewGuid(), null));
    }

    /// <summary>
    /// OBO-003: whom a task may be created for - yourself at Own, your own department's people at Department, anyone at All - by
    /// the person's home department, and independently of requests.submit.
    /// </summary>
    [Fact]
    public void Tasks_are_created_for_yourself_your_department_or_anyone()
    {
        var colleague = Guid.NewGuid();
        var outsider = Guid.NewGuid();

        var lead = Creator(PermissionScope.Department, Finance);
        Assert.True(AccessPolicy.CanCreateTasksForOthers(lead));
        Assert.True(AccessPolicy.CanCreateTaskFor(lead, lead.UserId!.Value, Finance));
        Assert.True(AccessPolicy.CanCreateTaskFor(lead, colleague, Finance));
        Assert.False(AccessPolicy.CanCreateTaskFor(lead, outsider, It));
        Assert.False(AccessPolicy.CanCreateTaskFor(lead, outsider, null)); // someone with no department isn't in the lead's

        var helpdesk = Creator(PermissionScope.All, It);
        Assert.True(AccessPolicy.CanCreateTaskFor(helpdesk, outsider, Finance));
        Assert.True(AccessPolicy.CanCreateTaskFor(helpdesk, outsider, null));

        var own = Creator(PermissionScope.Own, Finance);
        Assert.False(AccessPolicy.CanCreateTasksForOthers(own));
        Assert.True(AccessPolicy.CanCreateTaskFor(own, own.UserId!.Value, Finance));
        Assert.False(AccessPolicy.CanCreateTaskFor(own, colleague, Finance));

        var nobody = TestActors.Nobody(Finance);
        Assert.False(AccessPolicy.CanCreateTaskFor(nobody, nobody.UserId!.Value, Finance));

        // The two "for whom" permissions don't leak into each other: request flows follow requests.submit, tasks tasks.create_for.
        var requester = TestActors.Grants(Finance, (Permission.RequestsSubmit, PermissionScope.Department), (Permission.TasksCreateFor, PermissionScope.Own));
        Assert.True(AccessPolicy.CanRequestFor(requester, colleague, Finance));
        Assert.False(AccessPolicy.CanCreateTaskFor(requester, colleague, Finance));
        var creator = TestActors.Grants(Finance, (Permission.RequestsSubmit, PermissionScope.Own), (Permission.TasksCreateFor, PermissionScope.Department));
        Assert.False(AccessPolicy.CanRequestFor(creator, colleague, Finance));
        Assert.True(AccessPolicy.CanCreateTaskFor(creator, colleague, Finance));
    }

    /// <summary>
    /// OBO-004: the requestee has the creator's rights - the task counts as their own for viewing, editing and planning, wherever
    /// it is filed - and nothing more: logging time stays the assignee's, taking stays a department matter.
    /// </summary>
    [Fact]
    public void The_requestee_has_the_creators_rights()
    {
        var member = TestActors.With(DefaultRoles.MemberGrants, Finance, DefaultRoles.Member, Me);
        var forMe = new TaskItem { DepartmentId = It, CreatedById = Guid.NewGuid(), RequesteeId = Me, Assignments = Assigned.To() };
        var forSomeoneElse = new TaskItem { DepartmentId = It, CreatedById = Guid.NewGuid(), RequesteeId = Guid.NewGuid(), Assignments = Assigned.To() };

        Assert.True(AccessPolicy.CanViewTask(member, forMe));
        Assert.True(AccessPolicy.CanEditTask(member, forMe));
        Assert.True(AccessPolicy.CanChangeStatus(member, forMe));
        Assert.True(AccessPolicy.CanPlanTask(member, forMe));
        Assert.False(AccessPolicy.CanViewTask(member, forSomeoneElse));
        Assert.False(AccessPolicy.CanEditTask(member, forSomeoneElse));

        Assert.False(AccessPolicy.CanLogTimeFor(member, forMe, Me)); // time at Own is on tasks assigned to you
        Assert.False(AccessPolicy.CanTakeTask(member, forMe)); // another department's task

        // An Own-only viewer sees it too; a role without tasks.view doesn't.
        var ownViewer = TestActors.With(new Dictionary<string, PermissionScope> { [Permission.TasksView] = PermissionScope.Own }, Finance, userId: Me);
        Assert.True(AccessPolicy.CanViewTask(ownViewer, forMe));
        Assert.False(AccessPolicy.CanViewTask(TestActors.With(new Dictionary<string, PermissionScope>(), Finance, userId: Me), forMe));
    }

    private static readonly List<TaskItem> Tasks =
    [
        new() { Title = "fin-other", DepartmentId = Finance, CreatedById = Guid.NewGuid(), Assignments = Assigned.To() },
        new() { Title = "fin-for-me", DepartmentId = Finance, CreatedById = Guid.NewGuid(), RequesteeId = Me, Assignments = Assigned.To() },
        new() { Title = "it-for-me", DepartmentId = It, CreatedById = Guid.NewGuid(), RequesteeId = Me, Assignments = Assigned.To() },
        new() { Title = "it-created", DepartmentId = It, CreatedById = Me, RequesteeId = Guid.NewGuid(), Assignments = Assigned.To() },
        new() { Title = "it-assigned", DepartmentId = It, CreatedById = Guid.NewGuid(), Assignments = Assigned.To(Me) },
        new() { Title = "it-other", DepartmentId = It, CreatedById = Guid.NewGuid(), RequesteeId = Guid.NewGuid(), Assignments = Assigned.To() }
    ];

    private static Actor Viewer(PermissionScope scope, Guid? department) =>
        TestActors.With(new Dictionary<string, PermissionScope> { [Permission.TasksView] = scope }, department, userId: Me);

    private static string[] Titles(IEnumerable<TaskItem> tasks) => tasks.Select(t => t.Title).OrderBy(t => t).ToArray();

    /// <summary>
    /// OBO-005: Own lists include the tasks created for you; the department lists stay department-only (decision 52); the
    /// Requestee filter's scope is exactly what the actor may open.
    /// </summary>
    [Fact]
    public void Lists_include_the_requestees_tasks_as_far_as_they_may_open_them()
    {
        Assert.Equal(new[] { "fin-for-me", "it-assigned", "it-created", "it-for-me" },
            Titles(Scoping.Tasks(Tasks.AsQueryable(), Viewer(PermissionScope.Own, Finance))));
        Assert.Equal(new[] { "fin-for-me", "fin-other" }, Titles(Scoping.Tasks(Tasks.AsQueryable(), Viewer(PermissionScope.Department, Finance))));
        Assert.Equal(new[] { "fin-for-me", "fin-other", "it-assigned", "it-created", "it-for-me" },
            Titles(Scoping.TasksIncludingOwn(Tasks.AsQueryable(), Viewer(PermissionScope.Department, Finance))));

        foreach (var viewer in new[]
                 {
                     Viewer(PermissionScope.All, Finance), Viewer(PermissionScope.Department, Finance), Viewer(PermissionScope.Department, null),
                     Viewer(PermissionScope.Own, Finance), Viewer(PermissionScope.None, Finance)
                 })
        {
            Assert.Equal(Titles(Tasks.Where(t => AccessPolicy.CanViewTask(viewer, t))), Titles(Scoping.TasksIncludingOwn(Tasks.AsQueryable(), viewer)));
        }
    }

    private static readonly List<ApplicationUser> People =
    [
        new() { Id = Me, DisplayName = "me", DepartmentId = Finance },
        new() { Id = Guid.NewGuid(), DisplayName = "fin-colleague", DepartmentId = Finance },
        new() { Id = Guid.NewGuid(), DisplayName = "fin-left", DepartmentId = Finance, IsActive = false },
        new() { Id = Guid.NewGuid(), DisplayName = "it-person", DepartmentId = It },
        new() { Id = Guid.NewGuid(), DisplayName = "no-department" },
        new() { Id = Guid.NewGuid(), DisplayName = "Claude", IsSystemAccount = true }
    ];

    private static string[] Names(IEnumerable<ApplicationUser> people) => people.Select(u => u.DisplayName).OrderBy(n => n).ToArray();

    /// <summary>
    /// OBO-006: the people a "for whom" permission offers - active people, never the Claude user - are exactly those
    /// <see cref="AccessPolicy.CanCreateTaskFor"/> allows, and the same helper gives request flows their people by requests.submit.
    /// </summary>
    [Fact]
    public void The_people_offered_are_the_people_in_reach()
    {
        Assert.Equal(new[] { "fin-colleague", "it-person", "me", "no-department" },
            Names(Scoping.People(People.AsQueryable(), Creator(PermissionScope.All, Finance, Me), Permission.TasksCreateFor)));
        Assert.Equal(new[] { "fin-colleague", "me" },
            Names(Scoping.People(People.AsQueryable(), Creator(PermissionScope.Department, Finance, Me), Permission.TasksCreateFor)));
        Assert.Equal(new[] { "me" }, Names(Scoping.People(People.AsQueryable(), Creator(PermissionScope.Own, Finance, Me), Permission.TasksCreateFor)));
        Assert.Empty(Scoping.People(People.AsQueryable(), Creator(PermissionScope.None, Finance, Me), Permission.TasksCreateFor));

        foreach (var actor in new[]
                 {
                     Creator(PermissionScope.All, Finance, Me), Creator(PermissionScope.Department, Finance, Me), Creator(PermissionScope.Department, null, Me),
                     Creator(PermissionScope.Own, Finance, Me), Creator(PermissionScope.None, Finance, Me)
                 })
        {
            var allowed = People.Where(u => u.IsActive && !u.IsSystemAccount && AccessPolicy.CanCreateTaskFor(actor, u.Id, u.DepartmentId));
            Assert.Equal(Names(allowed), Names(Scoping.People(People.AsQueryable(), actor, Permission.TasksCreateFor)));
        }

        // Request flows (§6.20) use the same helper with their own permission.
        var lead = TestActors.With(new Dictionary<string, PermissionScope> { [Permission.RequestsSubmit] = PermissionScope.Department }, Finance, userId: Me);
        Assert.Equal(new[] { "fin-colleague", "me" }, Names(Scoping.People(People.AsQueryable(), lead, Permission.RequestsSubmit)));
        Assert.Equal(new[] { "me" }, Names(Scoping.People(People.AsQueryable(), TestActors.With(DefaultRoles.MemberGrants, Finance, userId: Me), Permission.RequestsSubmit)));
    }
}
