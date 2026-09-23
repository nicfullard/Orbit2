using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Orbit.Data.Entities;

namespace Orbit.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<Sprint> Sprints => Set<Sprint>();
    public DbSet<RecurringTaskDefinition> RecurringTaskDefinitions => Set<RecurringTaskDefinition>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<RunningClock> RunningClocks => Set<RunningClock>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<LdapSettings> LdapSettings => Set<LdapSettings>();
    public DbSet<WorkingCalendar> WorkingCalendars => Set<WorkingCalendar>();
    public DbSet<WorkingCalendarException> WorkingCalendarExceptions => Set<WorkingCalendarException>();
    public DbSet<CriticalPathAnalysis> CriticalPathAnalyses => Set<CriticalPathAnalysis>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<NumberCounter> NumberCounters => Set<NumberCounter>();

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
            b.HasOne(t => t.Assignee).WithMany(u => u.AssignedTasks)
                .HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(t => t.CreatedBy).WithMany(u => u.CreatedTasks)
                .HasForeignKey(t => t.CreatedById).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(t => t.Sprint).WithMany(s => s.Tasks)
                .HasForeignKey(t => t.SprintId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne(t => t.RecurringTaskDefinition).WithMany(r => r.GeneratedTasks)
                .HasForeignKey(t => t.RecurringTaskDefinitionId).OnDelete(DeleteBehavior.SetNull);
            // Subtasks (§6.15): a self-reference. A parent with children can't be deleted from under them.
            b.HasOne(t => t.ParentTask).WithMany(p => p.Children)
                .HasForeignKey(t => t.ParentTaskId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => t.ParentTaskId);
            b.HasIndex(t => t.StartDate);
            b.HasIndex(t => t.ProjectId);
            b.HasIndex(t => t.AssigneeId);
            b.HasIndex(t => t.Status);
            b.HasIndex(t => t.DepartmentId);
            b.HasIndex(t => t.SprintId);
            b.HasIndex(t => t.DueDate);
            b.HasIndex(t => t.PlannedFor);
            b.HasIndex(t => t.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            b.HasIndex(t => new { t.RecurringTaskDefinitionId, t.DueDate });
            b.Ignore(t => t.IsOpen);
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
            b.HasOne(r => r.Assignee).WithMany()
                .HasForeignKey(r => r.AssigneeId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(r => r.CreatedBy).WithMany()
                .HasForeignKey(r => r.CreatedById).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(r => new { r.Active, r.NextRunDate });
        });

        builder.Entity<Comment>(b =>
        {
            b.Property(c => c.Body).IsRequired();
            b.HasOne(c => c.Task).WithMany(t => t.Comments)
                .HasForeignKey(c => c.TaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(c => c.Author).WithMany(u => u.Comments)
                .HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => c.TaskId);
        });

        builder.Entity<TimeEntry>(b =>
        {
            b.Property(t => t.Note).HasMaxLength(1000);
            b.HasOne(t => t.Task).WithMany(x => x.TimeEntries)
                .HasForeignKey(t => t.TaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(t => t.User).WithMany(u => u.TimeEntries)
                .HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => t.TaskId);
            b.HasIndex(t => new { t.UserId, t.Date });
        });

        builder.Entity<RunningClock>(b =>
        {
            b.HasOne(c => c.Task).WithMany()
                .HasForeignKey(c => c.TaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(c => c.User).WithMany()
                .HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            // One running clock per user, enforced at the database level as well.
            b.HasIndex(c => c.UserId).IsUnique();
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
            // Attached to exactly one task or one project (§6.18); the row dies with it, the uploader is only recorded.
            b.Property(a => a.FileName).HasMaxLength(255).IsRequired();
            b.Property(a => a.ContentType).HasMaxLength(200).IsRequired();
            b.Property(a => a.StoragePath).HasMaxLength(300).IsRequired();
            b.HasOne(a => a.Task).WithMany(t => t.Attachments)
                .HasForeignKey(a => a.TaskId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Project).WithMany(p => p.Attachments)
                .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.UploadedBy).WithMany()
                .HasForeignKey(a => a.UploadedById).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => a.TaskId);
            b.HasIndex(a => a.ProjectId);
            b.ToTable(t => t.HasCheckConstraint("CK_Attachments_OneParent", "(\"TaskId\" IS NULL) <> (\"ProjectId\" IS NULL)"));
        });

        builder.Entity<NumberCounter>(b =>
        {
            b.HasKey(c => new { c.Prefix, c.Year });
            b.Property(c => c.Prefix).HasMaxLength(4);
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
