using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>Comments on a project (spec §6.1, §6.5): commenting follows seeing the project, the shared view (§6.2.1) included.</summary>
public class CommentAccessTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Marketing = Guid.NewGuid();

    private static Project Project(Guid department, Guid? owner = null) =>
        new() { DepartmentId = department, OwnerId = owner ?? Guid.NewGuid() };

    /// <summary>CMT-001: commenting on a project follows viewing it - its own department, its owner at Own, the built-in role anywhere.</summary>
    [Fact]
    public void Commenting_on_a_project_follows_viewing_it()
    {
        Assert.True(AccessPolicy.CanCommentOnProject(TestActors.Member(It), Project(It), false));
        Assert.True(AccessPolicy.CanCommentOnProject(TestActors.DepartmentAdmin(It), Project(It), false));
        Assert.False(AccessPolicy.CanCommentOnProject(TestActors.Member(Marketing), Project(It), false));
        Assert.False(AccessPolicy.CanCommentOnProject(TestActors.DepartmentAdmin(Marketing), Project(It), false));
        Assert.True(AccessPolicy.CanCommentOnProject(TestActors.SystemAdmin(), Project(It), false));
        Assert.False(AccessPolicy.CanCommentOnProject(TestActors.Nobody(It), Project(It), false));

        var ownOnly = TestActors.Grants(It, (Permission.ProjectsView, PermissionScope.Own));
        Assert.True(AccessPolicy.CanCommentOnProject(ownOnly, Project(Marketing, ownOnly.UserId), false));
        Assert.False(AccessPolicy.CanCommentOnProject(ownOnly, Project(It), false));
    }

    /// <summary>CMT-002: a department that only shares the project through its tasks may comment, but still not attach, edit or add tasks.</summary>
    [Fact]
    public void A_department_that_only_shares_the_project_may_comment_but_not_attach_or_edit()
    {
        var project = Project(Marketing);
        foreach (var sharer in new[] { TestActors.Member(It), TestActors.DepartmentAdmin(It) })
        {
            Assert.True(AccessPolicy.CanCommentOnProject(sharer, project, hasTasksInActorDepartment: true));
            Assert.False(AccessPolicy.CanAttachToProject(sharer, project));
            Assert.False(AccessPolicy.CanEditProject(sharer, project));
            Assert.False(AccessPolicy.CanAddTaskToProject(sharer, project));
        }

        // projects.view at Own is the projects you own; having tasks on someone else's doesn't widen it.
        var ownOnly = TestActors.Grants(It, (Permission.ProjectsView, PermissionScope.Own));
        Assert.False(AccessPolicy.CanCommentOnProject(ownOnly, project, hasTasksInActorDepartment: true));
    }
}
