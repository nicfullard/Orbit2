using System.Globalization;
using Orbit.Data.Entities;

namespace Orbit.Application.Assets;

/// <summary>
/// Asset type properties and their values (spec §6.19), as pure functions so the rules are unit-tested and shared by the pages
/// and the MCP tools. A value is stored as a string in one canonical form per type: Text trimmed, Number as an invariant decimal
/// ("1234.5"), Date as ISO "yyyy-MM-dd", YesNo as "true"/"false", Choice as one of the options in the option's own spelling.
/// A blank value is no value at all.
/// </summary>
public static class AssetPropertyRules
{
    public const int MaxNameLength = 100;
    public const int MaxValueLength = 500;
    public const int MaxOptions = 50;
    public const int MaxOptionLength = 100;

    /// <summary>The canonical form of <paramref name="raw"/> for the property, or null when it is blank. Refuses a value the type can't hold.</summary>
    public static string? Normalise(AssetTypeProperty property, string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        switch (property.PropertyType)
        {
            case AssetPropertyType.Number:
                if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                    return number.ToString("0.############################", CultureInfo.InvariantCulture);
                throw new ValidationException($"\"{property.Name}\" must be a number, like 16 or 2.5; got \"{text}\".");
            case AssetPropertyType.Date:
                if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                throw new ValidationException($"\"{property.Name}\" must be a date like 2026-09-30; got \"{text}\".");
            case AssetPropertyType.YesNo:
                return text.ToLowerInvariant() switch
                {
                    "true" or "yes" or "y" or "1" => "true",
                    "false" or "no" or "n" or "0" => "false",
                    _ => throw new ValidationException($"\"{property.Name}\" must be yes or no; got \"{text}\".")
                };
            case AssetPropertyType.Choice:
                return property.Options.FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ValidationException($"\"{property.Name}\" must be one of: {string.Join(", ", property.Options)}; got \"{text}\".");
            default:
                if (text.Length > MaxValueLength)
                    throw new ValidationException($"\"{property.Name}\" must be {MaxValueLength} characters or fewer.");
                return text;
        }
    }

    /// <summary>A stored value as people read it: Yes / No for a YesNo property, the value itself otherwise.</summary>
    public static string Display(AssetPropertyType type, string value) => type == AssetPropertyType.YesNo
        ? value == "true" ? "Yes" : value == "false" ? "No" : value
        : value;

    /// <summary>
    /// The values that survive a change of type: each current value whose property has a namesake on the new type - the same name
    /// ignoring case and the same <see cref="AssetPropertyType"/>, and for a Choice a value that is one of the new options - keyed by
    /// the new property's id. Everything else is dropped.
    /// </summary>
    public static Dictionary<Guid, string> CarryOver(
        IEnumerable<(AssetTypeProperty Property, string Value)> current, IEnumerable<AssetTypeProperty> newProperties)
    {
        var carried = new Dictionary<Guid, string>();
        var list = current.ToList();
        foreach (var target in newProperties)
        {
            var match = list.FirstOrDefault(c =>
                string.Equals(c.Property.Name.Trim(), target.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                && c.Property.PropertyType == target.PropertyType);
            if (match.Property is null) continue;
            if (target.PropertyType == AssetPropertyType.Choice)
            {
                var option = target.Options.FirstOrDefault(o => string.Equals(o, match.Value, StringComparison.OrdinalIgnoreCase));
                if (option is not null) carried[target.Id] = option;
            }
            else
            {
                carried[target.Id] = match.Value;
            }
        }
        return carried;
    }

    /// <summary>
    /// An asset's values after a save: <paramref name="existing"/> (the values already held for this type, carried over on a type
    /// change) with <paramref name="changes"/> applied - a key present sets the value, or clears it when blank; a key absent leaves it
    /// alone. A change for a property the type doesn't have is refused, and so is saving without a required property.
    /// </summary>
    public static Dictionary<Guid, string> Resolve(
        IReadOnlyList<AssetTypeProperty> properties, IReadOnlyDictionary<Guid, string> existing, IReadOnlyDictionary<Guid, string?> changes)
    {
        var byId = properties.ToDictionary(p => p.Id);
        var result = existing.Where(e => byId.ContainsKey(e.Key)).ToDictionary(e => e.Key, e => e.Value);
        foreach (var (id, raw) in changes)
        {
            if (!byId.TryGetValue(id, out var property))
                throw new ValidationException("A value was given for a property this asset's type doesn't have.");
            var value = Normalise(property, raw);
            if (value is null) result.Remove(id);
            else result[id] = value;
        }
        var missing = MissingRequired(properties, result);
        if (missing.Count > 0)
            throw new ValidationException($"Required: {string.Join(", ", missing)}.");
        return result;
    }

    /// <summary>The names of the required properties without a value, in display order.</summary>
    public static IReadOnlyList<string> MissingRequired(IEnumerable<AssetTypeProperty> properties, IReadOnlyDictionary<Guid, string> values) =>
        properties.Where(p => p.IsRequired && !values.ContainsKey(p.Id))
            .OrderBy(p => p.DisplayOrder).Select(p => p.Name).ToList();

    /// <summary>A property definition's name, and its options: cleaned, distinct and within limits for a Choice, none for any other type.</summary>
    public static (string Name, List<string> Options) ValidateDefinition(string? name, AssetPropertyType type, IEnumerable<string>? options)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationException("A property needs a name.");
        if (n.Length > MaxNameLength) throw new ValidationException($"A property name must be {MaxNameLength} characters or fewer.");
        if (type != AssetPropertyType.Choice) return (n, []);

        var list = new List<string>();
        foreach (var raw in options ?? [])
        {
            var o = raw?.Trim();
            if (string.IsNullOrEmpty(o)) continue;
            if (o.Length > MaxOptionLength) throw new ValidationException($"An option must be {MaxOptionLength} characters or fewer: \"{o[..20]}...\".");
            if (list.Any(x => string.Equals(x, o, StringComparison.OrdinalIgnoreCase)))
                throw new ValidationException($"\"{o}\" is listed twice.");
            list.Add(o);
        }
        if (list.Count == 0) throw new ValidationException($"A Choice property needs at least one option (one per line).");
        if (list.Count > MaxOptions) throw new ValidationException($"A Choice property can have at most {MaxOptions} options.");
        return (n, list);
    }

    /// <summary>
    /// Whether a property in use may change as asked (§6.19): its <see cref="AssetPropertyType"/> only while no asset has a value for
    /// it; a Choice option removed (or its text changed) only while no asset holds it. <paramref name="valueCounts"/> is how many assets
    /// hold each stored value. Renaming, reordering, required and new options are always allowed.
    /// </summary>
    public static void CheckChange(AssetTypeProperty current, AssetPropertyType newType, IReadOnlyList<string> newOptions, IReadOnlyDictionary<string, int> valueCounts)
    {
        var inUse = valueCounts.Values.Sum();
        if (newType != current.PropertyType)
        {
            if (inUse > 0)
                throw new ValidationException($"\"{current.Name}\" can't change type while {Plural(inUse, "asset has", "assets have")} a value for it. Add a new property and delete this one instead.");
            return;
        }
        if (current.PropertyType != AssetPropertyType.Choice) return;
        foreach (var removed in current.Options.Where(o => !newOptions.Contains(o, StringComparer.Ordinal)))
        {
            if (valueCounts.TryGetValue(removed, out var count) && count > 0)
                throw new ValidationException($"The option \"{removed}\" can't be removed or changed while {Plural(count, "asset holds", "assets hold")} it.");
        }
    }

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
