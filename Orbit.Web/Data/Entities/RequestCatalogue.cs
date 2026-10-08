namespace Orbit.Data.Entities;

/// <summary>
/// A heading on the Requests page (spec §6.20) - "Report a problem", "Request something new" - grouping the flows people pick
/// from. Owned by one department, set when it is created and never changed: every request logged under its flows is filed with
/// that department, and requests.configure reaching it manages the category and its flows.
/// </summary>
public class RequestCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>One of the names in <c>RequestIcons</c>; an unknown name draws the default icon.</summary>
    public string Icon { get; set; } = string.Empty;
    public RequestColour Colour { get; set; } = RequestColour.Blue;
    public int DisplayOrder { get; set; }
    /// <summary>Archived: hidden from the Requests page with all its flows, kept for its configuration.</summary>
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RequestFlow> Flows { get; set; } = new List<RequestFlow>();
}
