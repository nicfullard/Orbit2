using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>Attachments (spec §6.18): who may attach and remove files, and how uploaded names are tamed.</summary>
public class AttachmentTests
{
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Marketing = Guid.NewGuid();

    private static TaskItem Task(Guid department, Guid? assignee = null) =>
        new() { DepartmentId = department, AssigneeId = assignee, CreatedById = Guid.NewGuid() };

    private static Project Project(Guid department, Guid? owner = null) =>
        new() { DepartmentId = department, OwnerId = owner ?? Guid.NewGuid() };

    private static Attachment UploadedBy(Guid? userId) => new() { UploadedById = userId, FileName = "notes.txt" };

    [Fact]
    public void Anyone_in_the_department_may_attach_to_a_task_they_can_see()
    {
        var member = TestActors.Member(It);
        Assert.True(AccessPolicy.CanAttachToTask(member, Task(It, assignee: Guid.NewGuid())));
        Assert.False(AccessPolicy.CanAttachToTask(member, Task(Marketing)));
        Assert.True(AccessPolicy.CanAttachToTask(TestActors.SystemAdmin(), Task(Marketing)));
    }

    [Fact]
    public void Only_the_projects_own_department_may_attach_to_it()
    {
        Assert.True(AccessPolicy.CanAttachToProject(TestActors.Member(It), Project(It)));
        Assert.True(AccessPolicy.CanAttachToProject(TestActors.DepartmentAdmin(It), Project(It)));
        Assert.False(AccessPolicy.CanAttachToProject(TestActors.Member(Marketing), Project(It)));
        Assert.False(AccessPolicy.CanAttachToProject(TestActors.DepartmentAdmin(Marketing), Project(It)));
        Assert.True(AccessPolicy.CanAttachToProject(TestActors.SystemAdmin(), Project(It)));
    }

    [Fact]
    public void The_uploader_or_an_editor_of_the_parent_may_delete()
    {
        var uploader = TestActors.Member(It);
        var colleague = TestActors.Member(It);
        var admin = TestActors.DepartmentAdmin(It);
        var file = UploadedBy(uploader.UserId);

        var othersTask = Task(It, assignee: Guid.NewGuid());
        Assert.True(AccessPolicy.CanDeleteAttachment(uploader, file, othersTask));
        Assert.False(AccessPolicy.CanDeleteAttachment(colleague, file, othersTask));
        Assert.True(AccessPolicy.CanDeleteAttachment(admin, file, othersTask));
        Assert.True(AccessPolicy.CanDeleteAttachment(colleague, file, Task(It, assignee: colleague.UserId)));

        var othersProject = Project(It);
        Assert.True(AccessPolicy.CanDeleteAttachment(uploader, file, othersProject));
        Assert.False(AccessPolicy.CanDeleteAttachment(colleague, file, othersProject));
        Assert.True(AccessPolicy.CanDeleteAttachment(admin, file, othersProject));
        Assert.True(AccessPolicy.CanDeleteAttachment(colleague, file, Project(It, owner: colleague.UserId)));
    }

    [Fact]
    public void A_file_without_an_uploader_is_not_everyones_to_delete()
    {
        // UploadedById null (the uploader's account was removed) must not match an actor with no user id either.
        var job = new Actor(null, "Job", RoleRef.None, DefaultRoles.MemberGrants, It, ActorType.System, Guid.Empty);
        Assert.False(AccessPolicy.CanDeleteAttachment(job, UploadedBy(null), Task(It, assignee: Guid.NewGuid())));
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("  Q3 plan (final).xlsx  ", "Q3 plan (final).xlsx")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\me\photo.png", "photo.png")]
    [InlineData("we<i>rd|name?.txt", "weirdname.txt")]
    [InlineData("", "attachment")]
    [InlineData("   ", "attachment")]
    [InlineData("...", "attachment")]
    [InlineData(null, "attachment")]
    public void Uploaded_names_are_reduced_to_plain_file_names(string? uploaded, string expected)
    {
        Assert.Equal(expected, AttachmentService.CleanFileName(uploaded));
    }

    [Fact]
    public void Long_names_are_cut_but_keep_their_extension()
    {
        var name = new string('a', 300) + ".docx";
        var clean = AttachmentService.CleanFileName(name);
        Assert.Equal(255, clean.Length);
        Assert.EndsWith(".docx", clean);
    }
}
