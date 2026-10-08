using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Orbit.Data.Entities;

namespace Orbit.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<TaskAssignment> TaskAssignments => Set<TaskAssignment>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<Sprint> Sprints => Set<Sprint>();
    public DbSet<RecurringTaskDefinition> RecurringTaskDefinitions => Set<RecurringTaskDefinition>();
    public DbSet<RecurringTaskAssignment> RecurringTaskAssignments => Set<RecurringTaskAssignment>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<RunningClock> RunningClocks => Set<RunningClock>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<LdapSettings> LdapSettings => Set<LdapSettings>();
    public DbSet<WorkingCalendar> WorkingCalendars => Set<WorkingCalendar>();
    public DbSet<WorkingCalendarException> WorkingCalendarExceptions => Set<WorkingCalendarException>();
    public DbSet<CriticalPathAnalysis> CriticalPathAnalyses => Set<CriticalPathAnalysis>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AttachmentContent> AttachmentContents => Set<AttachmentContent>();
    public DbSet<NumberCounter> NumberCounters => Set<NumberCounter>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetAssignment> AssetAssignments => Set<AssetAssignment>();
    public DbSet<AssetCheck> AssetChecks => Set<AssetCheck>();
    public DbSet<AssetLocation> AssetLocations => Set<AssetLocation>();
    public DbSet<AssetType> AssetTypes => Set<AssetType>();
    public DbSet<AssetTypeProperty> AssetTypeProperties => Set<AssetTypeProperty>();
    public DbSet<AssetPropertyValue> AssetPropertyValues => Set<AssetPropertyValue>();
    public DbSet<RequestCategory> RequestCategories => Set<RequestCategory>();
    public DbSet<RequestFlow> RequestFlows => Set<RequestFlow>();
    public DbSet<RequestFlowStep> RequestFlowSteps => Set<RequestFlowStep>();
    public DbSet<RequestFlowStepDependency> RequestFlowStepDependencies => Set<RequestFlowStepDependency>();
    public DbSet<RequestFormField> RequestFormFields => Set<RequestFormField>();
    public DbSet<RequestFlowApprovalStage> RequestFlowApprovalStages => Set<RequestFlowApprovalStage>();
    public DbSet<RequestFlowApprover> RequestFlowApprovers => Set<RequestFlowApprover>();
    public DbSet<RequestFlowStepAssignee> RequestFlowStepAssignees => Set<RequestFlowStepAssignee>();
    public DbSet<RequestFlowStepActionInput> RequestFlowStepActionInputs => Set<RequestFlowStepActionInput>();
    public DbSet<RequestAction> RequestActions => Set<RequestAction>();
    public DbSet<RequestActionParameter> RequestActionParameters => Set<RequestActionParameter>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<RequestStep> RequestSteps => Set<RequestStep>();
    public DbSet<RequestFormAnswer> RequestFormAnswers => Set<RequestFormAnswer>();
    public DbSet<RequestApproval> RequestApprovals => Set<RequestApproval>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(b =>
        {
            b.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            b.HasOne(u => u.Department).WithMany(d => d.Users)
                .HasForeignKey(u => u.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(u => u.DepartmentId);
        });

        builder.Entity<ApplicationRole>(b =>
        {
            b.Property(r => r.Description).HasMaxLength(500);
            // Exactly one built-in role (spec §6.5), enforced at the database level as well.
            b.HasIndex(r => r.IsBuiltIn).IsUnique().HasFilter("\"IsBuiltIn\"")
                .HasDatabaseName("IX_AspNetRoles_SingleBuiltIn");
        });

        builder.Entity<RolePermission>(b =>
        {
            // One row per (role, permission); the grants die with their role.
            b.HasKey(p => new { p.RoleId, p.Permission });
            b.Property(p => p.Permission).HasMaxLength(100).IsRequired();
            b.HasOne(p => p.Role).WithMany(r => r.Permissions)
                .HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Department>(b =>
        {
            b.Property(d => d.Name).HasMaxLength(200).IsRequired();
            b.Property(d => d.Description).HasMaxLength(2000);
            b.HasIndex(d => d.Name).IsUnique();
        });

        builder.Entity<Project>(b =>
        {
            b.Property(p => p.Number).HasMaxLength(16).IsRequired();
            b.HasIndex(p => p.Number).IsUnique();
            b.Property(p => p.Name).HasMaxLength(200).IsRequired();
            b.HasOne(p => p.Department).WithMany(d => d.Projects)
                .HasForeignKey(p => p.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(p => p.Owner).WithMany(u => u.OwnedProjects)
                .HasForeignKey(p => p.OwnerId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(p => p.DepartmentId);
            b.HasIndex(p => p.Status);
        });

        builder.Entity<TaskItem>(b =>
        {
            b.ToTable("Tasks");
            b.Property(t => t.Number).HasMaxLength(16).IsRequired();
            b.HasIndex(t => t.Number).IsUnique();
            b.Property(t => t.Title).HasMaxLength(300).IsRequired();
            b.Property(t => t.IdempotencyKey).HasMaxLength(200);
            b.HasOne(t => t.Department).WithMany(d => d.Tasks)
                .HasForeignKey(t => t.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(t => t.Project).WithMany(p => p.Tasks)
                .HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Restrict);
            // The assignees (§6.2.3) come with every task, however it was reached: being one is part of what makes a task
            // someone's own (§6.5), and a task is checked in many places that never asked for its assignees.
            b.Navigation(t => t.Assignments).AutoInclude();
            b.HasOne(t => t.CreatedBy).WithMany(u => u.CreatedTasks)
                .HasForeignKey(t => t.CreatedById).OnDelete(DeleteBehavior.Restrict);
            // The requestee, whom the task is for (§6.2.2); users are deactivated, never deleted.
            b.HasOne(t => t.Requestee).WithMany()
                .HasForeignKey(t => t.RequesteeId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => t.RequesteeId);
            b.HasOne(t => t.Sprint).WithMany(s => s.Tasks)
                .HasForeignKey(t => t.SprintId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(t => t.RecurringTaskDefinition).WithMany(r => r.GeneratedTasks)
                .HasForeignKey(t => t.RecurringTaskDefinitionId).OnDelete(DeleteBehavior.SetNull);
            // Subtasks (§6.15): a self-reference. A parent with children can't be deleted from under them.
            b.HasOne(t => t.ParentTask).WithMany(p => p.Children)
                .HasForeignKey(t => t.ParentTaskId).OnDelete(DeleteBehavior.Restrict);
            // The asset the task is about (§6.19): an asset with linked tasks can't be deleted, only disposed of.
            b.HasOne(t => t.Asset).WithMany(a => a.Tasks)
                .HasForeignKey(t => t.AssetId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => t.AssetId);
            b.HasIndex(t => t.ParentTaskId);
            b.HasIndex(t => t.StartDate);
            b.HasIndex(t => t.ProjectId);
            b.HasIndex(t => t.Status);
            b.HasIndex(t => t.DepartmentId);
            b.HasIndex(t => t.SprintId);
            b.HasIndex(t => t.DueDate);
            b.HasIndex(t => t.PlannedFor);
            b.HasIndex(t => t.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            b.HasIndex(t => new { t.RecurringTaskDefinitionId, t.DueDate });
            b.Ignore(t => t.IsOpen);
            b.Ignore(t => t.IsUnassigned);
            b.Ignore(t => t.AssigneeIds);
            b.Ignore(t => t.Assignees);
            b.Ignore(t => t.AssigneeNames);
        });

        builder.Entity<TaskAssignment>(b =>
        {
            // A person is assigned to a task at most once; the row dies with the task, and users are deactivated rather than deleted.
            b.HasKey(x => new { x.TaskId, x.UserId });
            b.HasOne(x => x.Task).WithMany(t => t.Assignments)
                .HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.AssignedBy).WithMany()
                .HasForeignKey(x => x.AssignedById).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.User).AutoInclude();
            b.HasIndex(x => x.UserId);
        });

        builder.Entity<TaskDependency>(b =>
        {
            // A link dies with either of its tasks. One link per ordered pair; the graph must stay acyclic (TaskStructureService).
            b.HasOne(d => d.Predecessor).WithMany(t => t.SuccessorLinks)
                .HasForeignKey(d => d.PredecessorTaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(d => d.Successor).WithMany(t => t.PredecessorLinks)
                .HasForeignKey(d => d.SuccessorTaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(d => d.CreatedBy).WithMany()
                .HasForeignKey(d => d.CreatedById).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(d => new { d.PredecessorTaskId, d.SuccessorTaskId }).IsUnique();
            b.HasIndex(d => d.SuccessorTaskId);
        });

        builder.Entity<Sprint>(b =>
        {
            b.Property(s => s.Name).HasMaxLength(200).IsRequired();
            // Only one Active sprint at a time, company-wide - enforced at the database level as well.
            b.HasIndex(s => s.Status).IsUnique().HasFilter("\"Status\" = 'Active'")
                .HasDatabaseName("IX_Sprints_SingleActive");
        });

        builder.Entity<RecurringTaskDefinition>(b =>
        {
            b.Property(r => r.Title).HasMaxLength(300).IsRequired();
            b.Property(r => r.RecurrenceRule).HasMaxLength(500).IsRequired();
            b.HasOne(r => r.Department).WithMany(d => d.RecurringTaskDefinitions)
                .HasForeignKey(r => r.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(r => r.Project).WithMany(p => p.RecurringTaskDefinitions)
                .HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
            // As a task's are (§6.2.3): a definition is its assignees' own too.
            b.Navigation(r => r.Assignments).AutoInclude();
            b.HasOne(r => r.Asset).WithMany()
                .HasForeignKey(r => r.AssetId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(r => r.CreatedBy).WithMany()
                .HasForeignKey(r => r.CreatedById).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(r => r.AssetId);
            b.HasIndex(r => new { r.Active, r.NextRunDate });
            b.Ignore(r => r.AssigneeIds);
            b.Ignore(r => r.Assignees);
            b.Ignore(r => r.AssigneeNames);
        });

        builder.Entity<RecurringTaskAssignment>(b =>
        {
            b.HasKey(x => new { x.RecurringTaskDefinitionId, x.UserId });
            b.HasOne(x => x.RecurringTaskDefinition).WithMany(r => r.Assignments)
                .HasForeignKey(x => x.RecurringTaskDefinitionId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.User).AutoInclude();
            b.HasIndex(x => x.UserId);
        });

        builder.Entity<Comment>(b =>
        {
            // On exactly one task, project or asset (§6.1, §6.19); the comment dies with it.
            b.Property(c => c.Body).IsRequired();
            b.HasOne(c => c.Task).WithMany(t => t.Comments)
                .HasForeignKey(c => c.TaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(c => c.Project).WithMany(p => p.Comments)
                .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(c => c.Asset).WithMany(a => a.Comments)
                .HasForeignKey(c => c.AssetId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(c => c.Author).WithMany(u => u.Comments)
                .HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => c.TaskId);
            b.HasIndex(c => c.ProjectId);
            b.HasIndex(c => c.AssetId);
            b.ToTable(t => t.HasCheckConstraint("CK_Comments_OneParent", "num_nonnulls(\"TaskId\", \"ProjectId\", \"AssetId\") = 1"));
        });

        builder.Entity<TimeEntry>(b =>
        {
            b.Property(t => t.Note).HasMaxLength(1000);
            b.Property(t => t.IdempotencyKey).HasMaxLength(200);
            b.HasOne(t => t.Task).WithMany(x => x.TimeEntries)
                .HasForeignKey(t => t.TaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(t => t.User).WithMany(u => u.TimeEntries)
                .HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => t.TaskId);
            b.HasIndex(t => new { t.UserId, t.Date });
            b.HasIndex(t => t.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        });

        builder.Entity<RunningClock>(b =>
        {
            b.HasOne(c => c.Task).WithMany()
                .HasForeignKey(c => c.TaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(c => c.User).WithMany()
                .HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            // One running clock per user per task, enforced at the database level as well.
            b.HasIndex(c => new { c.UserId, c.TaskId }).IsUnique();
            b.HasIndex(c => c.TaskId);
        });

        builder.Entity<AuditLog>(b =>
        {
            b.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
            b.Property(a => a.Action).HasMaxLength(100).IsRequired();
            b.Property(a => a.ActorName).HasMaxLength(200);
            b.Property(a => a.Summary).HasMaxLength(500);
            b.Property(a => a.Details).HasColumnType("jsonb");
            b.HasIndex(a => a.Timestamp);
            b.HasIndex(a => new { a.EntityType, a.EntityId });
            b.HasIndex(a => a.DepartmentId);
        });

        builder.Entity<ApiKey>(b =>
        {
            b.Property(k => k.Name).HasMaxLength(200).IsRequired();
            b.Property(k => k.HashedKey).HasMaxLength(128).IsRequired();
            b.Property(k => k.Prefix).HasMaxLength(16);
            b.HasOne(k => k.Department).WithMany()
                .HasForeignKey(k => k.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            // A role in use by a key (revoked or not) can't be deleted (spec §6.5).
            b.HasOne(k => k.Role).WithMany()
                .HasForeignKey(k => k.RoleId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(k => k.RoleId);
            b.HasIndex(k => k.HashedKey).IsUnique();
            b.Ignore(k => k.IsRevoked);
        });

        builder.Entity<Agent>(b =>
        {
            b.Property(a => a.Name).HasMaxLength(200).IsRequired();
            b.Property(a => a.RegistrationTokenHash).HasMaxLength(128);
            b.Property(a => a.HashedSecret).HasMaxLength(128);
            b.Property(a => a.SecretPrefix).HasMaxLength(24);
            b.Property(a => a.MachineName).HasMaxLength(200);
            b.Property(a => a.OsDescription).HasMaxLength(200);
            b.Property(a => a.Version).HasMaxLength(50);
            b.Property(a => a.LastIpAddress).HasMaxLength(64);
            b.HasIndex(a => a.HashedSecret).IsUnique().HasFilter("\"HashedSecret\" IS NOT NULL");
            b.HasIndex(a => a.RegistrationTokenHash).IsUnique().HasFilter("\"RegistrationTokenHash\" IS NOT NULL");
        });

        builder.Entity<LdapSettings>(b =>
        {
            b.Property(s => s.Server).HasMaxLength(255);
            b.Property(s => s.BindDn).HasMaxLength(500);
            b.Property(s => s.SearchBase).HasMaxLength(500);
            b.Property(s => s.UserFilter).HasMaxLength(500);
        });

        builder.Entity<WorkingCalendar>(b => b.Ignore(c => c.WorkingDays));

        builder.Entity<WorkingCalendarException>(b =>
        {
            b.Property(e => e.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(e => e.Date).IsUnique();
        });

        builder.Entity<CriticalPathAnalysis>(b =>
        {
            // An analysis dies with its project; it outlives the user who ran it.
            b.HasOne(a => a.Project).WithMany()
                .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.RunBy).WithMany()
                .HasForeignKey(a => a.RunById).OnDelete(DeleteBehavior.SetNull);
            b.Property(a => a.InputFingerprint).HasMaxLength(64).IsRequired();
            b.Property(a => a.ResultData).HasColumnType("jsonb");
            b.HasIndex(a => new { a.ProjectId, a.RunAt });
        });

        builder.Entity<Attachment>(b =>
        {
            // Attached to exactly one task, project, asset or request (§6.18, §6.19, §6.20); the row dies with it, the uploader is only recorded.
            b.Property(a => a.FileName).HasMaxLength(255).IsRequired();
            b.Property(a => a.ContentType).HasMaxLength(200).IsRequired();
            b.HasOne(a => a.Task).WithMany(t => t.Attachments)
                .HasForeignKey(a => a.TaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Project).WithMany(p => p.Attachments)
                .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Asset).WithMany(x => x.Attachments)
                .HasForeignKey(a => a.AssetId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Request).WithMany(r => r.Attachments)
                .HasForeignKey(a => a.RequestId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.UploadedBy).WithMany()
                .HasForeignKey(a => a.UploadedById).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => a.TaskId);
            b.HasIndex(a => a.ProjectId);
            b.HasIndex(a => a.AssetId);
            b.HasIndex(a => a.RequestId);
            b.ToTable(t => t.HasCheckConstraint("CK_Attachments_OneParent", "num_nonnulls(\"TaskId\", \"ProjectId\", \"AssetId\", \"RequestId\") = 1"));
        });

        builder.Entity<AttachmentContent>(b =>
        {
            // The bytes, one row per attachment (§6.18), keyed by the attachment so the pair is one-to-one; gone when the attachment goes.
            b.HasKey(c => c.AttachmentId);
            b.Property(c => c.Data).IsRequired();
            b.HasOne(c => c.Attachment).WithOne(a => a.Content)
                .HasForeignKey<AttachmentContent>(c => c.AttachmentId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<NumberCounter>(b =>
        {
            b.HasKey(c => new { c.Prefix, c.Year });
            b.Property(c => c.Prefix).HasMaxLength(4);
        });

        // --- Assets (§6.19). The case-insensitive unique indexes - lower("AssetNumber") (the optional ERP number: unique when
        // set, any number of assets without one), and (DepartmentId, lower("Name")) on types and locations - plus
        // lower("SerialNumber") for the duplicate check are expression indexes created in SQL by the AddAssets migration,
        // since EF can't express them; the services check the same rules first.
        builder.Entity<Asset>(b =>
        {
            b.Property(a => a.AssetNumber).HasMaxLength(50);
            b.Property(a => a.IdempotencyKey).HasMaxLength(200);
            b.HasIndex(a => a.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            b.Property(a => a.Name).HasMaxLength(200).IsRequired();
            b.Property(a => a.Description).HasMaxLength(4000);
            b.Property(a => a.Manufacturer).HasMaxLength(200);
            b.Property(a => a.Model).HasMaxLength(200);
            b.Property(a => a.SerialNumber).HasMaxLength(100);
            b.Property(a => a.PurchaseOrder).HasMaxLength(100);
            b.Property(a => a.InvoiceNumber).HasMaxLength(100);
            b.Property(a => a.Supplier).HasMaxLength(200);
            b.Property(a => a.PurchaseValue).HasPrecision(18, 2);
            b.HasOne(a => a.Department).WithMany()
                .HasForeignKey(a => a.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(a => a.AssetType).WithMany(t => t.Assets)
                .HasForeignKey(a => a.AssetTypeId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(a => a.AssetLocation).WithMany(l => l.Assets)
                .HasForeignKey(a => a.AssetLocationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(a => a.CreatedBy).WithMany()
                .HasForeignKey(a => a.CreatedById).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => a.DepartmentId);
            b.HasIndex(a => a.Status);
            b.HasIndex(a => a.AssetTypeId);
            b.HasIndex(a => a.AssetLocationId);
            b.HasIndex(a => a.LastCheckedOn);
            b.HasIndex(a => a.WarrantyExpiresOn);
        });

        builder.Entity<AssetAssignment>(b =>
        {
            // A person holds an asset at most once; the row dies with the asset, and users are deactivated rather than deleted.
            b.HasKey(x => new { x.AssetId, x.UserId });
            b.HasOne(x => x.Asset).WithMany(a => a.Assignments)
                .HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.AssignedBy).WithMany()
                .HasForeignKey(x => x.AssignedById).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.UserId);
        });

        builder.Entity<AssetCheck>(b =>
        {
            b.Property(c => c.Notes).HasMaxLength(2000);
            b.HasOne(c => c.Asset).WithMany(a => a.Checks)
                .HasForeignKey(c => c.AssetId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(c => c.CheckedBy).WithMany()
                .HasForeignKey(c => c.CheckedById).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => new { c.AssetId, c.CheckDate });
        });

        builder.Entity<AssetLocation>(b =>
        {
            b.Property(l => l.Name).HasMaxLength(200).IsRequired();
            b.Property(l => l.Description).HasMaxLength(1000);
            b.HasOne(l => l.Department).WithMany()
                .HasForeignKey(l => l.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(l => l.DepartmentId);
        });

        builder.Entity<AssetType>(b =>
        {
            b.Property(t => t.Name).HasMaxLength(100).IsRequired();
            b.Property(t => t.Description).HasMaxLength(1000);
            b.Property(t => t.Category).HasMaxLength(100);
            b.HasOne(t => t.Department).WithMany()
                .HasForeignKey(t => t.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => t.DepartmentId);
        });

        builder.Entity<AssetTypeProperty>(b =>
        {
            b.Property(p => p.Name).HasMaxLength(100).IsRequired();
            b.HasOne(p => p.AssetType).WithMany(t => t.Properties)
                .HasForeignKey(p => p.AssetTypeId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(p => p.AssetTypeId);
        });

        builder.Entity<AssetPropertyValue>(b =>
        {
            b.Property(v => v.Value).HasMaxLength(500).IsRequired();
            b.HasOne(v => v.Asset).WithMany(a => a.PropertyValues)
                .HasForeignKey(v => v.AssetId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(v => v.Property).WithMany(p => p.Values)
                .HasForeignKey(v => v.AssetTypePropertyId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(v => new { v.AssetId, v.AssetTypePropertyId }).IsUnique();
            b.HasIndex(v => v.AssetTypePropertyId);
        });

        // --- Request flows (§6.20). A category belongs to a department; a flow, its steps and everything under them die with their
        // parent. Titles are unique ignoring case - a category's within its department, a flow's within its category, an action's
        // everywhere - through lower() expression indexes created in SQL by the migrations; the services check the same rule first.
        // A request references its flow and each flow step (Restrict), so the services refuse to delete what requests still use.
        builder.Entity<RequestCategory>(b =>
        {
            b.Property(c => c.Title).HasMaxLength(100).IsRequired();
            b.Property(c => c.Description).HasMaxLength(500);
            b.Property(c => c.Icon).HasMaxLength(32).IsRequired();
            b.HasOne(c => c.Department).WithMany()
                .HasForeignKey(c => c.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => c.DepartmentId);
        });

        builder.Entity<RequestFlow>(b =>
        {
            b.Property(f => f.Title).HasMaxLength(150).IsRequired();
            b.Property(f => f.Description).HasMaxLength(500);
            b.HasOne(f => f.Category).WithMany(c => c.Flows)
                .HasForeignKey(f => f.CategoryId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(f => f.CategoryId);
        });

        builder.Entity<RequestFlowStep>(b =>
        {
            b.Property(s => s.Key).HasMaxLength(40).IsRequired();
            b.Property(s => s.Title).HasMaxLength(150).IsRequired();
            b.Property(s => s.Url).HasMaxLength(2000);
            b.Property(s => s.TitleTemplate).HasMaxLength(500);
            b.Property(s => s.DescriptionTemplate).HasMaxLength(4000);
            b.HasOne(s => s.Flow).WithMany(f => f.Steps)
                .HasForeignKey(s => s.FlowId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(s => s.PerformedBy).WithMany()
                .HasForeignKey(s => s.PerformedById).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(s => s.TaskDepartment).WithMany()
                .HasForeignKey(s => s.TaskDepartmentId).OnDelete(DeleteBehavior.Restrict);
            // A task step's field references point at fields of earlier form steps; a deleted field just unsets them.
            b.HasOne(s => s.DueDateField).WithMany()
                .HasForeignKey(s => s.DueDateFieldId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(s => s.AssetField).WithMany()
                .HasForeignKey(s => s.AssetFieldId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(s => s.ProjectField).WithMany()
                .HasForeignKey(s => s.ProjectFieldId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(s => s.PriorityField).WithMany()
                .HasForeignKey(s => s.PriorityFieldId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(s => s.RequesteeField).WithMany()
                .HasForeignKey(s => s.RequesteeFieldId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(s => s.CopyAttachmentsFromStep).WithMany()
                .HasForeignKey(s => s.CopyAttachmentsFromStepId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(s => s.Action).WithMany(a => a.Steps)
                .HasForeignKey(s => s.ActionId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(s => new { s.FlowId, s.Key }).IsUnique();
        });

        builder.Entity<RequestFlowStepDependency>(b =>
        {
            b.HasKey(d => new { d.StepId, d.DependsOnStepId });
            b.HasOne(d => d.Step).WithMany(s => s.Dependencies)
                .HasForeignKey(d => d.StepId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(d => d.DependsOnStep).WithMany()
                .HasForeignKey(d => d.DependsOnStepId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(d => d.DependsOnStepId);
        });

        builder.Entity<RequestFormField>(b =>
        {
            b.Property(f => f.Key).HasMaxLength(40).IsRequired();
            b.Property(f => f.Prompt).HasMaxLength(300).IsRequired();
            b.Property(f => f.HelpText).HasMaxLength(500);
            b.HasOne(f => f.Step).WithMany(s => s.Fields)
                .HasForeignKey(f => f.StepId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(f => new { f.StepId, f.Key }).IsUnique();
        });

        builder.Entity<RequestFlowApprovalStage>(b =>
        {
            b.HasOne(s => s.Step).WithMany(x => x.Stages)
                .HasForeignKey(s => s.StepId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(s => s.StepId);
        });

        builder.Entity<RequestFlowApprover>(b =>
        {
            b.HasOne(a => a.Stage).WithMany(s => s.Approvers)
                .HasForeignKey(a => a.StageId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.User).WithMany()
                .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(a => a.Department).WithMany()
                .HasForeignKey(a => a.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(a => a.Role).WithMany()
                .HasForeignKey(a => a.RoleId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => a.StageId);
        });

        builder.Entity<RequestFlowStepAssignee>(b =>
        {
            b.HasKey(a => new { a.StepId, a.UserId });
            b.HasOne(a => a.Step).WithMany(s => s.Assignees)
                .HasForeignKey(a => a.StepId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.User).WithMany()
                .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RequestFlowStepActionInput>(b =>
        {
            b.HasKey(i => new { i.StepId, i.ParameterKey });
            b.Property(i => i.ParameterKey).HasMaxLength(40);
            b.Property(i => i.ValueTemplate).HasMaxLength(4000);
            b.HasOne(i => i.Step).WithMany(s => s.ActionInputs)
                .HasForeignKey(i => i.StepId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RequestAction>(b =>
        {
            b.Property(a => a.Name).HasMaxLength(100).IsRequired();
            b.Property(a => a.Description).HasMaxLength(500);
            b.Property(a => a.Script).IsRequired();
            // An agent that is deleted leaves the action to any agent that can run scripts.
            b.HasOne(a => a.Agent).WithMany()
                .HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RequestActionParameter>(b =>
        {
            b.Property(p => p.Key).HasMaxLength(40).IsRequired();
            b.Property(p => p.Label).HasMaxLength(150).IsRequired();
            b.HasOne(p => p.Action).WithMany(a => a.Parameters)
                .HasForeignKey(p => p.ActionId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(p => new { p.ActionId, p.Key }).IsUnique();
        });

        // --- Requests (§6.20): the instances. A request's steps, answers and approvals die with it; what they point at stays.
        builder.Entity<Request>(b =>
        {
            b.Property(r => r.Number).HasMaxLength(16).IsRequired();
            b.HasIndex(r => r.Number).IsUnique();
            b.Property(r => r.Title).HasMaxLength(300).IsRequired();
            b.Property(r => r.IdempotencyKey).HasMaxLength(200);
            b.HasIndex(r => r.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            b.HasOne(r => r.Flow).WithMany()
                .HasForeignKey(r => r.FlowId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(r => r.Department).WithMany()
                .HasForeignKey(r => r.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(r => r.Requester).WithMany()
                .HasForeignKey(r => r.RequesterId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(r => r.RequesterId);
            b.HasIndex(r => r.FlowId);
            b.HasIndex(r => new { r.DepartmentId, r.Status });
        });

        builder.Entity<RequestStep>(b =>
        {
            b.Property(s => s.Error).HasMaxLength(4000);
            b.HasOne(s => s.Request).WithMany(r => r.Steps)
                .HasForeignKey(s => s.RequestId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(s => s.FlowStep).WithMany()
                .HasForeignKey(s => s.FlowStepId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(s => s.AssignedTo).WithMany()
                .HasForeignKey(s => s.AssignedToId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(s => s.Task).WithMany()
                .HasForeignKey(s => s.TaskId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(s => s.CompletedBy).WithMany()
                .HasForeignKey(s => s.CompletedById).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(s => new { s.RequestId, s.FlowStepId }).IsUnique();
            b.HasIndex(s => s.Status);
            b.HasIndex(s => s.TaskId);
            b.HasIndex(s => s.AssignedToId);
        });

        builder.Entity<RequestFormAnswer>(b =>
        {
            b.Property(a => a.Value).HasMaxLength(4000);
            b.HasOne(a => a.Step).WithMany(s => s.Answers)
                .HasForeignKey(a => a.StepId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Field).WithMany()
                .HasForeignKey(a => a.FieldId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(a => a.Asset).WithMany()
                .HasForeignKey(a => a.AssetId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(a => a.AssetType).WithMany()
                .HasForeignKey(a => a.AssetTypeId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(a => a.Project).WithMany()
                .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(a => a.User).WithMany()
                .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(a => a.Attachment).WithMany()
                .HasForeignKey(a => a.AttachmentId).OnDelete(DeleteBehavior.SetNull);
            b.HasIndex(a => new { a.StepId, a.FieldId });
        });

        builder.Entity<RequestApproval>(b =>
        {
            b.Property(a => a.Comment).HasMaxLength(2000);
            b.HasOne(a => a.Step).WithMany(s => s.Approvals)
                .HasForeignKey(a => a.StepId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Approver).WithMany()
                .HasForeignKey(a => a.ApproverId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => new { a.StepId, a.StageOrder, a.ApproverId }).IsUnique();
            b.HasIndex(a => new { a.ApproverId, a.Decision });
        });

        // Store every enum as its name so the database is readable and filterable in SQL.
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clr.IsEnum)
                {
                    property.SetProviderClrType(typeof(string));
                    property.SetMaxLength(32);
                }
            }
        }
    }
}
