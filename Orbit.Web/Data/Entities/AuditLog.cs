namespace Orbit.Data.Entities;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public ActorType ActorType { get; set; }
    /// <summary>User id, API key id, or Guid.Empty for the system.</summary>
    public Guid ActorId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    /// <summary>Department the entity belongs to, so activity can be scoped per department.</summary>
    public Guid? DepartmentId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    /// <summary>jsonb payload of changed fields/values.</summary>
    public string Details { get; set; } = "{}";
    /// <summary>Short human-readable summary (e.g. task title) for feeds.</summary>
    public string? Summary { get; set; }
}
