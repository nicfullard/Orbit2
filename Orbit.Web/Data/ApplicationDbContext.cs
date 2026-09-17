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
    public DbSet<Sprint> Sprints => Set<Sprint>();
    public DbSet<RecurringTaskDefinition> RecurringTaskDefinitions => Set<RecurringTaskDefinition>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<RunningClock> RunningClocks => Set<RunningClock>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<LdapSettings> LdapSettings => Set<LdapSettings>();

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
