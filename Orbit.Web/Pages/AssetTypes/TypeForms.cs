using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Pages.AssetTypes;

/// <summary>An asset type's own fields (spec §6.19). The department is chosen once, when the type is created.</summary>
public sealed class TypeForm
{
    public Guid? DepartmentId { get; set; }
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public int? CheckIntervalDays { get; set; }

    public AssetTypeInput ToInput() => new()
    {
        DepartmentId = DepartmentId, Name = Name ?? string.Empty, Category = Category, Description = Description, CheckIntervalDays = CheckIntervalDays
    };

    public static TypeForm From(AssetType t) => new()
    {
        DepartmentId = t.DepartmentId, Name = t.Name, Category = t.Category, Description = t.Description, CheckIntervalDays = t.CheckIntervalDays
    };
}

/// <summary>One property on the type's edit page. A Choice property's options are typed one per line.</summary>
public sealed class PropertyForm
{
    public string? Name { get; set; }
    public AssetPropertyType PropertyType { get; set; } = AssetPropertyType.Text;
    public bool IsRequired { get; set; }
    public string? Options { get; set; }

    public AssetPropertyInput ToInput() => new()
    {
        Name = Name ?? string.Empty,
        PropertyType = PropertyType,
        IsRequired = IsRequired,
        Options = (Options ?? string.Empty).Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).ToList()
    };
}
