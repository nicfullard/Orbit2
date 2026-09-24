using Microsoft.AspNetCore.Identity;

namespace Orbit.Data.Entities;

/// <summary>
/// The application's user. Profile fields live directly on the Identity user (Guid key) so
/// domain foreign keys (AssigneeId, OwnerId, AuthorId, ...) point straight at AspNetUsers.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Required when the user's role has any grant at Department scope (spec §6.5); otherwise optional - a home department for defaults.</summary>
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>Soft delete flag. Deactivated users can't sign in and are hidden from pickers.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>True for synthetic accounts such as the "Claude" agent that API keys act as.</summary>
    public bool IsSystemAccount { get; set; }

    /// <summary>
    /// Local password, or the company directory via an Orbit Agent. A directory user has no PasswordHash,
    /// and sign-in never consults one for them, so a stale local password can't bypass the directory.
    /// </summary>
    public AuthSource AuthSource { get; set; } = AuthSource.Local;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TaskItem> AssignedTasks { get; set; } = new List<TaskItem>();
    public ICollection<TaskItem> CreatedTasks { get; set; } = new List<TaskItem>();
    public ICollection<Project> OwnedProjects { get; set; } = new List<Project>();
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
    public ICollection<TimeEntry> TimeEntries { get; set; } = new List<TimeEntry>();
}
