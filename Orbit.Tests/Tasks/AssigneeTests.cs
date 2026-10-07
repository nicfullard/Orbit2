using Microsoft.EntityFrameworkCore;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data;
using Orbit.Data.Entities;
using Orbit.Tests.Access;
using Orbit.Tests.Scheduling;

namespace Orbit.Tests.Tasks;

/// <summary>Several assignees on a task and on a recurring definition (spec §6.2.3, §13 item 66).</summary>
public class AssigneeTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Finance = Guid.NewGuid();

    private static TaskItem Task(Guid department, params Guid?[] assignees) =>
        new() { DepartmentId = department, CreatedById = Guid.NewGuid(), Assignments = Assigned.To(assignees) };

    private static Actor OwnOnly(Guid department) => TestActors.Grants(department,
        (Permission.TasksView, PermissionScope.Own), (Permission.TasksEdit, PermissionScope.Own), (Permission.TasksPlan, PermissionScope.Own));

    /// <summary>ASGN-001: a task is each assignee's own - to see, edit, plan and close - and nobody else's for being a colleague of theirs.</summary>
    [Fact]
    public void Each_assignee_owns_the_task()
    {
        var first = OwnOnly(It);
        var second = OwnOnly(It);
        var bystander = OwnOnly(It);
        var task = Task(It, first.UserId, second.UserId);

        foreach (var assignee in new[] { first, second })
        {
            Assert.True(AccessPolicy.CanViewTask(assignee, task));
            Assert.True(AccessPolicy.CanEditTask(assignee, task));
            Assert.True(AccessPolicy.CanPlanTask(assignee, task));
            Assert.True(AccessPolicy.CanChangeStatus(assignee, task));
        }
        Assert.False(AccessPolicy.CanViewTask(bystander, task));
        Assert.False(AccessPolicy.CanEditTask(bystander, task));

        // Independent of department, as Own always is: an assignee from elsewhere owns it too.
        var visiting = OwnOnly(Finance);
        Assert.True(AccessPolicy.CanEditTask(visiting, Task(It, first.UserId, visiting.UserId)));
    }

    /// <summary>ASGN-002: Take is for a task nobody is assigned to; joining one that has an assignee, or several, is an edit.</summary>
    [Fact]
    public void Take_is_only_for_a_task_with_no_assignee()
    {
        var member = TestActors.Member(It);
        Assert.True(AccessPolicy.CanTakeTask(member, Task(It)));
        Assert.False(AccessPolicy.CanTakeTask(member, Task(It, Guid.NewGuid())));
        Assert.False(AccessPolicy.CanTakeTask(member, Task(It, Guid.NewGuid(), Guid.NewGuid())));
        Assert.False(AccessPolicy.CanTakeTask(member, Task(It, member.UserId, Guid.NewGuid()))); // already theirs
    }

    /// <summary>
    /// ASGN-003: Own lists include a task for each of its assignees, and a recurring definition likewise; the lists and
    /// <see cref="AccessPolicy.CanViewTask"/> agree for every viewer.
    /// </summary>
    [Fact]
    public void Own_lists_include_a_shared_task_for_each_assignee()
    {
        var me = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var tasks = new List<TaskItem>
        {
            new() { Title = "shared", DepartmentId = It, CreatedById = Guid.NewGuid(), Assignments = Assigned.To(colleague, me) },
            new() { Title = "theirs", DepartmentId = It, CreatedById = Guid.NewGuid(), Assignments = Assigned.To(colleague) },
            new() { Title = "elsewhere-shared", DepartmentId = Finance, CreatedById = Guid.NewGuid(), Assignments = Assigned.To(me, colleague) },
            new() { Title = "nobody", DepartmentId = It, CreatedById = Guid.NewGuid(), Assignments = Assigned.To() }
        };
        Actor Viewer(PermissionScope scope, Guid user) =>
            TestActors.With(new Dictionary<string, PermissionScope> { [Permission.TasksView] = scope }, It, userId: user);
        string[] Titles(IEnumerable<TaskItem> list) => list.Select(t => t.Title).OrderBy(t => t).ToArray();

        Assert.Equal(["elsewhere-shared", "shared"], Titles(Scoping.Tasks(tasks.AsQueryable(), Viewer(PermissionScope.Own, me))));
        Assert.Equal(["elsewhere-shared", "shared", "theirs"], Titles(Scoping.Tasks(tasks.AsQueryable(), Viewer(PermissionScope.Own, colleague))));
        Assert.Equal(["elsewhere-shared", "nobody", "shared", "theirs"],
            Titles(Scoping.TasksIncludingOwn(tasks.AsQueryable(), Viewer(PermissionScope.Department, me))));

        foreach (var scope in new[] { PermissionScope.Own, PermissionScope.Department, PermissionScope.All })
        {
            var viewer = Viewer(scope, me);
            Assert.Equal(Titles(tasks.Where(t => AccessPolicy.CanViewTask(viewer, t))), Titles(Scoping.TasksIncludingOwn(tasks.AsQueryable(), viewer)));
        }

        var definitions = new List<RecurringTaskDefinition>
        {
            new() { Title = "shared", DepartmentId = It, CreatedById = Guid.NewGuid(), Assignments = Assigned.ToDefinition(colleague, me) },
            new() { Title = "theirs", DepartmentId = It, CreatedById = Guid.NewGuid(), Assignments = Assigned.ToDefinition(colleague) }
        };
        var ownViewer = Viewer(PermissionScope.Own, me);
        Assert.Equal(["shared"], Scoping.RecurringDefinitions(definitions.AsQueryable(), ownViewer).Select(d => d.Title).ToArray());
        Assert.True(AccessPolicy.CanViewRecurring(ownViewer, definitions[0]));
        Assert.False(AccessPolicy.CanViewRecurring(ownViewer, definitions[1]));
    }

    /// <summary>
    /// ASGN-004: at Own, time is each assignee's own on the tasks they are on - never a colleague's, nor a non-assignee's - and
    /// each assignee is asked about their own time before setting the task Done.
    /// </summary>
    [Fact]
    public void Time_at_own_and_the_done_question_are_each_assignees()
    {
        var first = TestActors.Member(It);
        var second = TestActors.Member(It);
        var colleague = TestActors.Member(It);
        var task = Task(It, first.UserId, second.UserId);
        task.Status = TaskItemStatus.InProgress;

        Assert.True(AccessPolicy.CanLogTimeFor(first, task, first.UserId!.Value));
        Assert.True(AccessPolicy.CanLogTimeFor(second, task, second.UserId!.Value));
        Assert.False(AccessPolicy.CanLogTimeFor(first, task, second.UserId!.Value)); // not for the other assignee
        Assert.False(AccessPolicy.CanLogTimeFor(colleague, task, colleague.UserId!.Value));

        Assert.True(TimeRules.AskBeforeDoneWithoutTime(first, task, hasLoggedTime: false, clockRunning: false));
        Assert.True(TimeRules.AskBeforeDoneWithoutTime(second, task, hasLoggedTime: false, clockRunning: false));
        Assert.False(TimeRules.AskBeforeDoneWithoutTime(second, task, hasLoggedTime: true, clockRunning: false)); // their own time, not the first's
        Assert.False(TimeRules.AskBeforeDoneWithoutTime(colleague, task, hasLoggedTime: false, clockRunning: false));
    }

    /// <summary>
    /// ASGN-005: an assignee is an active person - never the Claude user - in the task's department, or one whose role sees tasks
    /// everywhere; the refusal names the person. The picker's query offers exactly the people the rule lets through.
    /// </summary>
    [Fact]
    public void Who_may_be_assigned()
    {
        var inDepartment = Assigned.Person(Guid.NewGuid(), "Ann", It);
        var elsewhere = Assigned.Person(Guid.NewGuid(), "Ben", Finance);
        var admin = Assigned.Person(Guid.NewGuid(), "Cal", Finance);
        var homeless = Assigned.Person(Guid.NewGuid(), "Dee");
        var deactivated = Assigned.Person(Guid.NewGuid(), "Eve", It, active: false);
        var claude = Assigned.Person(Guid.NewGuid(), "Claude", It);
        claude.IsSystemAccount = true;
        Guid[] seesEverywhere = [admin.Id];
        ApplicationUser[] everyone = [inDepartment, elsewhere, admin, homeless, deactivated, claude];

        Assert.Null(AssigneeRules.Refusal(inDepartment, It, seesEveryDepartment: false));
        Assert.Null(AssigneeRules.Refusal(admin, It, seesEveryDepartment: true));
        Assert.Equal("Ben can't be assigned: a task can't be assigned to a user outside its own department.", AssigneeRules.Refusal(elsewhere, It, false));
        Assert.Contains("outside its own department", AssigneeRules.Refusal(homeless, It, false));
        Assert.Equal("Eve can't be assigned: an assignee must be an active user.", AssigneeRules.Refusal(deactivated, It, false));
        Assert.Contains("must be an active user", AssigneeRules.Refusal(claude, It, true));

        var offered = UserDirectoryService.Assignable(everyone.AsQueryable(), It, seesEverywhere).Select(u => u.Id).ToHashSet();
        Assert.Equal(everyone.Where(u => AssigneeRules.Refusal(u, It, seesEverywhere.Contains(u.Id)) is null).Select(u => u.Id).ToHashSet(), offered);
        Assert.Equal(new HashSet<Guid> { inDepartment.Id, admin.Id }, offered);
        // With no department to go by, only the people who can be assigned anywhere.
        Assert.Equal([admin.Id], UserDirectoryService.Assignable(everyone.AsQueryable(), null, seesEverywhere).Select(u => u.Id).ToArray());
    }

    /// <summary>
    /// ASGN-006: a change of assignees is the people added and removed, each once. Only the people added must qualify, so a
    /// colleague kept on the task doesn't block an edit by having since been deactivated or moved; when the task changes
    /// department, everyone staying on it must qualify there too - not the people leaving.
    /// </summary>
    [Fact]
    public void A_change_checks_the_people_added_and_a_move_checks_everyone_staying()
    {
        var kept = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        var added = Guid.NewGuid();
        Guid[] current = [kept, dropped];

        var (adds, removes) = AssigneeRules.Diff(current, [added, kept, added]);
        Assert.Equal([added], adds);
        Assert.Equal([dropped], removes);

        Assert.Equal([added], AssigneeRules.ToCheck(current, adds, removes, departmentChanging: false));
        Assert.Equal([kept, added], AssigneeRules.ToCheck(current, adds, removes, departmentChanging: true));

        // The same set, in any order, is no change, and checks nobody unless the task is moving.
        var (noAdds, noRemoves) = AssigneeRules.Diff(current, [dropped, kept]);
        Assert.Empty(noAdds);
        Assert.Empty(noRemoves);
        Assert.Empty(AssigneeRules.ToCheck(current, noAdds, noRemoves, departmentChanging: false));
        Assert.Equal(current, AssigneeRules.ToCheck(current, noAdds, noRemoves, departmentChanging: true));

        // Clearing everyone checks nobody, moving or not.
        var (_, all) = AssigneeRules.Diff(current, []);
        Assert.Equal(current, all);
        Assert.Empty(AssigneeRules.ToCheck(current, [], all, departmentChanging: true));
    }

    /// <summary>
    /// ASGN-007: a task names its assignees in name order, and reads as unassigned only when it has none. One whose assignees
    /// were never loaded refuses to answer rather than say "nobody" - which would read as "not yours" in every access check.
    /// </summary>
    [Fact]
    public void A_task_names_its_assignees_and_refuses_when_they_were_not_loaded()
    {
        var zoe = Assigned.Person(Guid.NewGuid(), "Zoe");
        var adam = Assigned.Person(Guid.NewGuid(), "Adam");
        var shared = new TaskItem { Assignments = Assigned.ToPeople(zoe, adam) };
        Assert.False(shared.IsUnassigned);
        Assert.True(shared.IsAssignedTo(zoe.Id) && shared.IsAssignedTo(adam.Id));
        Assert.False(shared.IsAssignedTo(Guid.NewGuid()));
        Assert.Equal([adam.Id, zoe.Id], shared.Assignees.Select(u => u.Id));
        Assert.Equal("Adam, Zoe", shared.AssigneeNames);
        Assert.Equal(2, shared.AssigneeIds.Count);

        var nobody = new TaskItem { Assignments = Assigned.To() };
        Assert.True(nobody.IsUnassigned);
        Assert.Null(nobody.AssigneeNames);

        var notLoaded = new TaskItem();
        Assert.Throws<InvalidOperationException>(() => notLoaded.IsUnassigned);
        Assert.Throws<InvalidOperationException>(() => notLoaded.IsAssignedTo(zoe.Id));
        Assert.Throws<InvalidOperationException>(() => AccessPolicy.CanViewTask(OwnOnly(It), notLoaded));
        Assert.Throws<InvalidOperationException>(() => AccessPolicy.CanTakeTask(TestActors.Member(It), notLoaded));

        var definition = new RecurringTaskDefinition { Assignments = [new RecurringTaskAssignment { UserId = zoe.Id, User = zoe }, new RecurringTaskAssignment { UserId = adam.Id, User = adam }] };
        Assert.Equal("Adam, Zoe", definition.AssigneeNames);
        Assert.Throws<InvalidOperationException>(() => new RecurringTaskDefinition().IsAssignedTo(zoe.Id));
    }

    /// <summary>
    /// ASGN-011: the day plan has a card per assignee, by name, then the unassigned tasks. A shared task is on each of its
    /// assignees' cards with its whole estimate - there is no rule for splitting one - and counted as shared there.
    /// </summary>
    [Fact]
    public void The_day_plan_puts_a_shared_task_on_each_assignees_card()
    {
        var zoe = Assigned.Person(Guid.NewGuid(), "Zoe");
        var adam = Assigned.Person(Guid.NewGuid(), "Adam");
        var shared = new TaskItem { Title = "shared", EstimateMinutes = 240, Assignments = Assigned.ToPeople(zoe, adam) };
        var zoes = new TaskItem { Title = "zoe's", EstimateMinutes = 60, Status = TaskItemStatus.Done, Assignments = Assigned.ToPeople(zoe) };
        var nobodys = new TaskItem { Title = "nobody's", Assignments = Assigned.To() };

        var groups = DayPlanRules.GroupByAssignee([shared, nobodys, zoes]);

        Assert.Equal(["Adam", "Zoe", "Unassigned"], groups.Select(g => g.Name));
        Assert.Equal(new Guid?[] { adam.Id, zoe.Id, null }, groups.Select(g => g.AssigneeId));
        Assert.Equal(["shared"], groups[0].Tasks.Select(t => t.Title));
        Assert.Equal(["shared", "zoe's"], groups[1].Tasks.Select(t => t.Title)); // in the order given
        Assert.Equal(["nobody's"], groups[2].Tasks.Select(t => t.Title));

        Assert.Equal(240, groups[0].OpenEstimatedMinutes);
        Assert.Equal(300, groups[1].EstimatedMinutes);
        Assert.Equal(240, groups[1].OpenEstimatedMinutes);
        Assert.Equal(1, groups[1].Done);
        Assert.Equal(1, groups[0].Shared);
        Assert.Equal(1, groups[1].Shared);
        Assert.Equal(0, groups[2].Shared);
        Assert.Equal(1, groups[2].OpenUnestimated);

        Assert.Empty(DayPlanRules.GroupByAssignee([]));
    }

    /// <summary>
    /// ASGN-012: a task's assignees, and a recurring definition's, come with it however it was loaded - through a time entry, a
    /// comment, a project - because the model includes them itself. Without that, the many access checks made on a task that
    /// never asked for its assignees would have nothing to go on.
    /// </summary>
    [Fact]
    public void The_model_loads_assignees_with_every_task_and_definition()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql("Host=unused;Database=unused").Options;
        using var db = new ApplicationDbContext(options);

        bool Eager(Type entity, string navigation) => db.Model.FindEntityType(entity)!.FindNavigation(navigation)!.IsEagerLoaded;
        Assert.True(Eager(typeof(TaskItem), nameof(TaskItem.Assignments)));
        Assert.True(Eager(typeof(TaskAssignment), nameof(TaskAssignment.User)));
        Assert.True(Eager(typeof(RecurringTaskDefinition), nameof(RecurringTaskDefinition.Assignments)));
        Assert.True(Eager(typeof(RecurringTaskAssignment), nameof(RecurringTaskAssignment.User)));

        // Reached through another entity's Include, a task still brings them along.
        Assert.Contains("\"TaskAssignments\"", db.TimeEntries.Include(e => e.Task).ToQueryString());
        Assert.Contains("\"TaskAssignments\"", db.Comments.Include(c => c.Task).ToQueryString());
        Assert.Contains("\"TaskAssignments\"", db.Tasks.Include(t => t.ParentTask).ToQueryString());
        Assert.Contains("\"RecurringTaskAssignments\"", db.Tasks.Include(t => t.RecurringTaskDefinition).ToQueryString());

        // The members that read them aren't columns.
        var task = db.Model.FindEntityType(typeof(TaskItem))!;
        Assert.Null(task.FindProperty(nameof(TaskItem.AssigneeIds)));
        Assert.Null(task.FindNavigation(nameof(TaskItem.Assignees)));
    }

    /// <summary>ASGN-013: a critical path analysis names every assignee of a task in its one assignee field, in name order.</summary>
    [Fact]
    public void The_critical_path_names_every_assignee()
    {
        var shared = Fixture.Task("Shared", "2026-10-05", "2026-10-06");
        shared.Assignments = Assigned.ToPeople(Assigned.Person(Guid.NewGuid(), "Zoe"), Assigned.Person(Guid.NewGuid(), "Adam"));
        var open = Fixture.Task("Open", "2026-10-06", "2026-10-07");

        var result = Fixture.Run([shared, open], [Fixture.Link(shared, open)]);

        Assert.Equal("Adam, Zoe", result.Row(shared).Assignee);
        Assert.Null(result.Row(open).Assignee);
    }

    /// <summary>ASGN-014: each person added is emailed, except whoever made the change; a change by the system leaves nobody out.</summary>
    [Fact]
    public void The_people_added_are_told_except_whoever_added_them()
    {
        var me = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Equal([first, second], AssigneeRules.ToNotify([first, me, second, first], me));
        Assert.Empty(AssigneeRules.ToNotify([me], me));
        Assert.Equal([me, first], AssigneeRules.ToNotify([me, first], null));
    }
}
