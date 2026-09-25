using System.ComponentModel;

namespace Orbit.Mcp;

/// <summary>
/// One entry of create_asset_type's / update_asset_type's properties list (spec §6.19, §7.1). On update it is matched to an existing
/// property by name; the fields left out keep their current values.
/// </summary>
public sealed class AssetPropertyArg
{
    [Description("The property's name (required). On update_asset_type, the name of the property to change; a name the type doesn't have adds a new property.")]
    public required string Name { get; set; }

    [Description("update_asset_type only: rename the property to this.")]
    public string? NewName { get; set; }

    [Description("Text, Number, Date, YesNo or Choice. Default Text for a new property. Can't change while any asset has a value for it.")]
    public string? Type { get; set; }

    [Description("Whether every asset of the type must have a value. Default false for a new property.")]
    public bool? Required { get; set; }

    [Description("A Choice property's options (at least one). On update this replaces the whole list; an option some asset holds can't be removed or reworded.")]
    public string[]? Options { get; set; }
}
