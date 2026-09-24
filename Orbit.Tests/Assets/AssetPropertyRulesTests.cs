using Orbit.Application;
using Orbit.Application.Assets;
using Orbit.Data.Entities;

namespace Orbit.Tests.Assets;

/// <summary>Typed property values, required properties, carry-over on a change of type and changes to a type in use (spec §6.19).</summary>
public class AssetPropertyRulesTests
{
    private static AssetTypeProperty Prop(string name, AssetPropertyType type, bool required = false, params string[] options) =>
        new() { Name = name, PropertyType = type, IsRequired = required, Options = options.ToList() };

    /// <summary>AST-009: each type's canonical form, and values the type can't hold are refused.</summary>
    [Fact]
    public void Values_are_validated_and_stored_in_canonical_form()
    {
        var ram = Prop("RAM (GB)", AssetPropertyType.Number);
        Assert.Equal("16", AssetPropertyRules.Normalise(ram, " 16 "));
        Assert.Equal("2.5", AssetPropertyRules.Normalise(ram, "2.50"));
        Assert.Equal("-3", AssetPropertyRules.Normalise(ram, "-3"));
        Assert.Throws<ValidationException>(() => AssetPropertyRules.Normalise(ram, "sixteen"));
        Assert.Throws<ValidationException>(() => AssetPropertyRules.Normalise(ram, "1,5"));

        var bought = Prop("Serviced", AssetPropertyType.Date);
        Assert.Equal("2026-03-01", AssetPropertyRules.Normalise(bought, "2026-03-01"));
        Assert.Throws<ValidationException>(() => AssetPropertyRules.Normalise(bought, "next Tuesday"));

        var encrypted = Prop("Encrypted", AssetPropertyType.YesNo);
        Assert.Equal("true", AssetPropertyRules.Normalise(encrypted, "Yes"));
        Assert.Equal("false", AssetPropertyRules.Normalise(encrypted, "false"));
        Assert.Throws<ValidationException>(() => AssetPropertyRules.Normalise(encrypted, "maybe"));
        Assert.Equal("Yes", AssetPropertyRules.Display(AssetPropertyType.YesNo, "true"));

        var os = Prop("OS", AssetPropertyType.Choice, false, "Windows", "Linux");
        Assert.Equal("Windows", AssetPropertyRules.Normalise(os, "windows"));
        Assert.Throws<ValidationException>(() => AssetPropertyRules.Normalise(os, "win11"));

        var notes = Prop("Notes", AssetPropertyType.Text);
        Assert.Equal("dent on lid", AssetPropertyRules.Normalise(notes, "  dent on lid "));
        Assert.Null(AssetPropertyRules.Normalise(notes, "   "));
        Assert.Throws<ValidationException>(() => AssetPropertyRules.Normalise(notes, new string('x', 501)));
    }

    /// <summary>AST-010: a required property must have a value on save; an asset missing one is flagged, not failed, until it is saved.</summary>
    [Fact]
    public void Required_properties_must_have_a_value_when_saved()
    {
        var ram = Prop("RAM", AssetPropertyType.Number, required: true);
        var os = Prop("OS", AssetPropertyType.Text);
        var props = new[] { ram, os };

        var missing = Assert.Throws<ValidationException>(() =>
            AssetPropertyRules.Resolve(props, new Dictionary<Guid, string>(), new Dictionary<Guid, string?> { [os.Id] = "Linux" }));
        Assert.Contains("RAM", missing.Message);

        var saved = AssetPropertyRules.Resolve(props, new Dictionary<Guid, string>(), new Dictionary<Guid, string?> { [ram.Id] = "16" });
        Assert.Equal("16", saved[ram.Id]);

        // Made required later: the existing values are simply flagged as missing.
        var flagged = AssetPropertyRules.MissingRequired(props, new Dictionary<Guid, string> { [os.Id] = "Linux" });
        Assert.Equal(["RAM"], flagged);

        // A blank change clears; an absent key leaves the value alone.
        var existing = new Dictionary<Guid, string> { [ram.Id] = "16", [os.Id] = "Linux" };
        var cleared = AssetPropertyRules.Resolve(props, existing, new Dictionary<Guid, string?> { [os.Id] = "" });
        Assert.False(cleared.ContainsKey(os.Id));
        Assert.Equal("16", cleared[ram.Id]);
        Assert.Throws<ValidationException>(() =>
            AssetPropertyRules.Resolve(props, existing, new Dictionary<Guid, string?> { [Guid.NewGuid()] = "x" }));
    }

    /// <summary>AST-011: a change of type keeps values whose property has a same-named, same-typed namesake - a Choice only if still an option.</summary>
    [Fact]
    public void Changing_type_carries_over_namesakes_and_drops_the_rest()
    {
        var oldRam = Prop("RAM", AssetPropertyType.Number);
        var oldOs = Prop("OS", AssetPropertyType.Choice, false, "Windows", "Linux", "macOS");
        var oldScreen = Prop("Screen", AssetPropertyType.Text);
        var oldGpu = Prop("GPU", AssetPropertyType.Text);
        var current = new List<(AssetTypeProperty, string)> { (oldRam, "16"), (oldOs, "macOS"), (oldScreen, "15 inch"), (oldGpu, "none") };

        var newRam = Prop("ram", AssetPropertyType.Number);
        var newOs = Prop("OS", AssetPropertyType.Choice, false, "windows", "Linux");
        var newScreen = Prop("Screen", AssetPropertyType.Number);
        var carried = AssetPropertyRules.CarryOver(current, [newRam, newOs, newScreen]);

        Assert.Equal("16", carried[newRam.Id]);            // same name ignoring case, same type
        Assert.False(carried.ContainsKey(newOs.Id));        // macOS isn't one of the new options
        Assert.False(carried.ContainsKey(newScreen.Id));    // same name, different type
        Assert.Single(carried);                             // GPU has no namesake

        var windows = AssetPropertyRules.CarryOver([(oldOs, "Windows")], [newOs]);
        Assert.Equal("windows", windows[newOs.Id]);         // stored in the new option's spelling

        // The new type's required properties must then be supplied.
        var requiredOnNew = Prop("Asset tag colour", AssetPropertyType.Text, required: true);
        Assert.Throws<ValidationException>(() =>
            AssetPropertyRules.Resolve([newRam, requiredOnNew], carried, new Dictionary<Guid, string?>()));
    }

    /// <summary>AST-012: a property in use can't change type, and an option in use can't be removed or retyped.</summary>
    [Fact]
    public void Properties_in_use_keep_their_type_and_used_options()
    {
        var os = Prop("OS", AssetPropertyType.Choice, false, "Windows", "Linux");
        var inUse = new Dictionary<string, int> { ["Windows"] = 3 };
        var unused = new Dictionary<string, int>();

        Assert.Throws<ValidationException>(() => AssetPropertyRules.CheckChange(os, AssetPropertyType.Text, [], inUse));
        AssetPropertyRules.CheckChange(os, AssetPropertyType.Text, [], unused);

        Assert.Throws<ValidationException>(() => AssetPropertyRules.CheckChange(os, AssetPropertyType.Choice, ["Linux"], inUse));
        Assert.Throws<ValidationException>(() => AssetPropertyRules.CheckChange(os, AssetPropertyType.Choice, ["windows", "Linux"], inUse));
        AssetPropertyRules.CheckChange(os, AssetPropertyType.Choice, ["Windows", "macOS"], inUse); // Linux unused: may go; new options fine
    }

    [Fact]
    public void Definitions_have_a_name_and_a_choice_has_distinct_options()
    {
        var (name, options) = AssetPropertyRules.ValidateDefinition(" OS ", AssetPropertyType.Choice, ["Windows", " Linux ", ""]);
        Assert.Equal("OS", name);
        Assert.Equal(["Windows", "Linux"], options);
        Assert.Empty(AssetPropertyRules.ValidateDefinition("RAM", AssetPropertyType.Number, ["ignored"]).Options);
        Assert.Throws<ValidationException>(() => AssetPropertyRules.ValidateDefinition("", AssetPropertyType.Text, null));
        Assert.Throws<ValidationException>(() => AssetPropertyRules.ValidateDefinition("OS", AssetPropertyType.Choice, []));
        Assert.Throws<ValidationException>(() => AssetPropertyRules.ValidateDefinition("OS", AssetPropertyType.Choice, ["Linux", "linux"]));
    }
}
