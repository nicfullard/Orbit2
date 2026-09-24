using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>The list scoping helper (spec §6.5): Own / Department / All / None filters, run over in-memory lists.</summary>
public class ScopingTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Marketing = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();

    private static readonly List<TaskItem> Tasks =
    [
        new() { Title = "it-mine", DepartmentId = It, AssigneeId = Me },
        new() { Title = "it-created", DepartmentId = It, CreatedById = Me, AssigneeId = Guid.NewGuid() },
        new() { Title = "it-other", DepartmentId = It, AssigneeId = Guid.NewGuid(), CreatedById = Guid.NewGuid() },
        new() { Title = "mk-mine", DepartmentId = Marketing, AssigneeId = Me },
        new() { Title = "mk-other", DepartmentId = Marketing, CreatedById = Guid.NewGuid() }
    ];

    private static Actor Viewer(PermissionScope scope) =>
        TestActors.With(new Dictionary<string, PermissionScope> { [Permission.TasksView] = scope, [Permission.ProjectsView] = scope, [Permission.AuditView] = scope }, It, userId: Me);

    private static string[] Titles(IEnumerable<TaskItem> tasks) => tasks.Select(t => t.Title).OrderBy(t => t).ToArray();

    [Fact]
    public void Tasks_all_department_own_none()
    {
        Assert.Equal(new[] { "it-created", "it-mine", "it-other", "mk-mine", "mk-other" }, Titles(Scoping.Tasks(Tasks.AsQueryable(), Viewer(PermissionScope.All))));
        Assert.Equal(new[] { "it-created", "it-mine", "it-other" }, Titles(Scoping.Tasks(Tasks.AsQueryable(), Viewer(PermissionScope.Department))));
        Assert.Equal(new[] { "it-created", "it-mine", "mk-mine" }, Titles(Scoping.Tasks(Tasks.AsQueryable(), Viewer(PermissionScope.Own))));
        Assert.Empty(Scoping.Tasks(Tasks.AsQueryable(), Viewer(PermissionScope.None)));
    }

    [Fact]
    public void A_department_less_viewer_at_department_scope_sees_nothing()
    {
        var homeless = TestActors.With(new Dictionary<string, PermissionScope> { [Permission.TasksView] = PermissionScope.Department }, null, userId: Me);
        Assert.Empty(Scoping.Tasks(Tasks.AsQueryable(), homeless));
    }

    [Fact]
    public void Projects_include_shared_ones_at_department_scope_unless_asked_not_to()
    {
        var own = new Project { Name = "it", DepartmentId = It, OwnerId = Guid.NewGuid() };
        var mine = new Project { Name = "mk-mine", DepartmentId = Marketing, OwnerId = Me };
        var shared = new Project { Name = "mk-shared", DepartmentId = Marketing, OwnerId = Guid.NewGuid(), Tasks = [new TaskItem { DepartmentId = It }] };
        var other = new Project { Name = "mk-other", DepartmentId = Marketing, OwnerId = Guid.NewGuid() };
        var projects = new List<Project> { own, mine, shared, other };
        string[] Names(IEnumerable<Project> p) => p.Select(x => x.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "it", "mk-mine", "mk-other", "mk-shared" }, Names(Scoping.Projects(projects.AsQueryable(), Viewer(PermissionScope.All))));
        Assert.Equal(new[] { "it", "mk-shared" }, Names(Scoping.Projects(projects.AsQueryable(), Viewer(PermissionScope.Department))));
        Assert.Equal(new[] { "it" }, Names(Scoping.Projects(projects.AsQueryable(), Viewer(PermissionScope.Department), includeShared: false)));
        Assert.Equal(new[] { "mk-mine" }, Names(Scoping.Projects(projects.AsQueryable(), Viewer(PermissionScope.Own))));
        Assert.Empty(Scoping.Projects(projects.AsQueryable(), Viewer(PermissionScope.None)));
    }

    [Fact]
    public void Audit_entries_all_department_or_nothing()
    {
        var logs = new List<AuditLog>
        {
            new() { Summary = "it", DepartmentId = It },
            new() { Summary = "mk", DepartmentId = Marketing },
            new() { Summary = "org", DepartmentId = null }
        };
        Assert.Equal(3, Scoping.Audit(logs.AsQueryable(), Viewer(PermissionScope.All)).Count());
        Assert.Equal(new[] { "it" }, Scoping.Audit(logs.AsQueryable(), Viewer(PermissionScope.Department)).Select(a => a.Summary).ToArray());
        Assert.Empty(Scoping.Audit(logs.AsQueryable(), Viewer(PermissionScope.Own)));
    }

    [Fact]
    public void Recurring_definitions_follow_the_task_rule()
    {
        var defs = new List<RecurringTaskDefinition>
        {
            new() { Title = "it-mine", DepartmentId = It, AssigneeId = Me },
            new() { Title = "it-other", DepartmentId = It, CreatedById = Guid.NewGuid() },
            new() { Title = "mk-created", DepartmentId = Marketing, CreatedById = Me }
        };
        Assert.Equal(2, Scoping.RecurringDefinitions(defs.AsQueryable(), Viewer(PermissionScope.Department)).Count());
        Assert.Equal(new[] { "it-mine", "mk-created" }, Scoping.RecurringDefinitions(defs.AsQueryable(), Viewer(PermissionScope.Own)).Select(d => d.Title).OrderBy(t => t).ToArray());
    }
}
