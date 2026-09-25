using Orbit.Agents.Contracts;
using Orbit.Application;
using Orbit.Application.Models;

namespace Orbit.Tests.Users;

/// <summary>The rules of "Import from directory" (spec §6.13, DIM-001 to DIM-011).</summary>
public class DirectoryImportRulesTests
{
    private static readonly ImportDepartment Finance = new(Guid.NewGuid(), "Finance", false);
    private static readonly ImportDepartment SalesEmea = new(Guid.NewGuid(), "Sales EMEA", false);
    private static readonly ImportDepartment OldIt = new(Guid.NewGuid(), "IT", true);
    private static readonly ImportDepartment[] Departments = [Finance, SalesEmea, OldIt];
    private static readonly Dictionary<string, Guid> NoUsers = [];

    private static LdapDirectoryUser Entry(string name, string? mail, string? department = null, bool disabled = false, string? upn = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Dn = $"CN={name},OU=Staff,DC=corp,DC=example,DC=com",
        DisplayName = name,
        Mail = mail,
        UserPrincipalName = upn,
        SamAccountName = name.Replace(" ", "").ToLowerInvariant(),
        Department = department,
        Disabled = disabled
    };

    private static DirectoryImportClassification Classify(IReadOnlyDictionary<string, Guid>? users = null, params LdapDirectoryUser[] entries) =>
        DirectoryImportRules.Classify(entries, DirectoryImportRules.MailAttribute, users ?? NoUsers, Departments);

    /// <summary>DIM-001: the default list is the sign-in filter with * for {0}, restricted to people.</summary>
    [Fact]
    public void The_default_filter_lists_whoever_the_sign_in_filter_could_find()
    {
        Assert.Equal("(&(objectCategory=person)(objectClass=user)(mail=*))", DirectoryImportRules.DefaultListFilter("(mail={0})"));
        Assert.Equal("(&(objectCategory=person)(objectClass=user)(&(mail=*)(memberOf=CN=Orbit Users,OU=Groups,DC=corp)))",
            DirectoryImportRules.DefaultListFilter("(&(mail={0})(memberOf=CN=Orbit Users,OU=Groups,DC=corp))"));
        Assert.Equal("(&(objectCategory=person)(objectClass=user)(userPrincipalName=*))", DirectoryImportRules.DefaultListFilter(" (userPrincipalName={0}) "));
        Assert.Equal("(&(objectCategory=person)(objectClass=user)(mail=*))", DirectoryImportRules.DefaultListFilter(null));
    }

    /// <summary>DIM-002: the email comes from the attribute the sign-in filter compares, so the imported person can sign in.</summary>
    [Fact]
    public void The_email_attribute_follows_the_sign_in_filter()
    {
        Assert.Equal(new EmailSource("mail", null), DirectoryImportRules.EmailSourceFor("(mail={0})"));
        Assert.Equal(new EmailSource("mail", null), DirectoryImportRules.EmailSourceFor("(&(objectClass=user)( mail = {0} ))"));
        Assert.Equal(new EmailSource("userPrincipalName", null), DirectoryImportRules.EmailSourceFor("(userPrincipalName={0})"));
        Assert.Equal(new EmailSource("mail", null), DirectoryImportRules.EmailSourceFor("(|(userPrincipalName={0})(mail={0}))"));

        var unclear = DirectoryImportRules.EmailSourceFor("(proxyAddresses=smtp:{0})");
        Assert.Equal("mail", unclear.Attribute);
        Assert.NotNull(unclear.Warning);
    }

    /// <summary>DIM-003: the list filter is a complete filter in balanced parentheses, with no {0}.</summary>
    [Fact]
    public void The_list_filter_is_validated()
    {
        Assert.Equal("(mail=*)", DirectoryImportRules.ValidateListFilter("  (mail=*) "));
        foreach (var bad in new[] { null, "", "   ", "mail=*", "(mail=*", "(&(mail=*)))", ")(mail=*)(", "(mail={0})", "(" + new string('a', 1000) + ")" })
            Assert.Throws<ValidationException>(() => DirectoryImportRules.ValidateListFilter(bad));
    }

    /// <summary>DIM-004: the department attribute defaults to "department" and must be an attribute name; a blank search base means the settings' one.</summary>
    [Fact]
    public void The_attribute_and_search_base_are_validated()
    {
        Assert.Equal("department", DirectoryImportRules.ValidateDepartmentAttribute(null));
        Assert.Equal("department", DirectoryImportRules.ValidateDepartmentAttribute("  "));
        Assert.Equal("extensionAttribute1", DirectoryImportRules.ValidateDepartmentAttribute(" extensionAttribute1 "));
        foreach (var bad in new[] { "dept;binary", "1dept", "de pt", "(department)", "department=*" })
            Assert.Throws<ValidationException>(() => DirectoryImportRules.ValidateDepartmentAttribute(bad));

        Assert.Null(DirectoryImportRules.ValidateSearchBase("  "));
        Assert.Equal("OU=Finance,DC=corp", DirectoryImportRules.ValidateSearchBase(" OU=Finance,DC=corp "));
        Assert.Throws<ValidationException>(() => DirectoryImportRules.ValidateSearchBase(new string('a', 501)));
    }

    /// <summary>DIM-005: department names match ignoring case, surrounding and repeated spaces.</summary>
    [Fact]
    public void Department_keys_ignore_case_and_spacing()
    {
        Assert.Equal(DirectoryImportRules.DepartmentKey("Sales EMEA"), DirectoryImportRules.DepartmentKey("  sales   emea "));
        Assert.NotEqual(DirectoryImportRules.DepartmentKey("Sales EMEA"), DirectoryImportRules.DepartmentKey("Sales-EMEA"));
        Assert.Equal(string.Empty, DirectoryImportRules.DepartmentKey(null));
        Assert.Equal(string.Empty, DirectoryImportRules.DepartmentKey("   "));
    }

    /// <summary>DIM-006: a directory department links to the active Orbit department of that name; an archived one is noted, not linked.</summary>
    [Fact]
    public void Departments_link_by_name_and_archived_ones_do_not()
    {
        var result = Classify(null,
            Entry("Ann", "ann@corp.com", "finance"),
            Entry("Bob", "bob@corp.com", " Sales  Emea "),
            Entry("Cat", "cat@corp.com", "It"),
            Entry("Dan", "dan@corp.com", "Warehouse"),
            Entry("Eve", "eve@corp.com"));
        var rows = result.Rows.ToDictionary(r => r.DisplayName);

        Assert.Equal(Finance.Id, rows["Ann"].DepartmentId);
        Assert.Equal("Finance", rows["Ann"].DepartmentName);
        Assert.Equal(SalesEmea.Id, rows["Bob"].DepartmentId);
        Assert.Null(rows["Cat"].DepartmentId);
        Assert.Equal("IT", rows["Cat"].ArchivedMatch);
        Assert.False(rows["Dan"].IsLinked);
        Assert.Null(rows["Dan"].ArchivedMatch);
        Assert.False(rows["Eve"].IsLinked);
        Assert.Null(rows["Eve"].DirectoryDepartment);
        Assert.All(result.Rows, r => Assert.Equal(DirectoryImportStatus.Ready, r.Status));
    }

    /// <summary>DIM-007: why an entry can't be imported - already in Orbit (any case), no email, disabled, or an email two entries share.</summary>
    [Fact]
    public void Entries_that_cannot_be_imported_say_why()
    {
        var existingId = Guid.NewGuid();
        var users = new Dictionary<string, Guid> { ["Ann@Corp.com"] = existingId };
        var result = Classify(users,
            Entry("Ann", "ann@corp.com", disabled: true),
            Entry("Bob", null),
            Entry("Cat", "not-an-email"),
            Entry("Dan", "dan@corp.com", disabled: true),
            Entry("Eve", "shared@corp.com"),
            Entry("Fay", "SHARED@corp.com"),
            Entry("Gus", "gus@corp.com"));
        var rows = result.Rows.ToDictionary(r => r.DisplayName);

        Assert.Equal(DirectoryImportStatus.InOrbit, rows["Ann"].Status); // being in Orbit is the more useful thing to show
        Assert.Equal(existingId, rows["Ann"].ExistingUserId);
        Assert.Equal(DirectoryImportStatus.NoEmail, rows["Bob"].Status);
        Assert.Equal(DirectoryImportStatus.NoEmail, rows["Cat"].Status);
        Assert.Null(rows["Cat"].Email);
        Assert.Equal(DirectoryImportStatus.Disabled, rows["Dan"].Status);
        Assert.Equal(DirectoryImportStatus.DuplicateEmail, rows["Eve"].Status);
        Assert.Equal(DirectoryImportStatus.DuplicateEmail, rows["Fay"].Status);
        Assert.Equal(DirectoryImportStatus.Ready, rows["Gus"].Status);
        Assert.Equal(["Gus"], result.Rows.Where(r => r.CanImport).Select(r => r.DisplayName));
    }

    /// <summary>DIM-008: with a userPrincipalName sign-in filter the email is the UPN, and mail is ignored.</summary>
    [Fact]
    public void The_upn_is_the_email_when_people_sign_in_with_it()
    {
        var result = DirectoryImportRules.Classify(
            [Entry("Ann", "ann.smith@corp.com", upn: "asmith@corp.com"), Entry("Bob", "bob@corp.com")],
            DirectoryImportRules.UpnAttribute, NoUsers, Departments);
        var rows = result.Rows.ToDictionary(r => r.DisplayName);

        Assert.Equal("asmith@corp.com", rows["Ann"].Email);
        Assert.Equal(DirectoryImportStatus.NoEmail, rows["Bob"].Status);
    }

    /// <summary>DIM-009: the display name falls back from displayName to given name and surname, the account name, then the email.</summary>
    [Fact]
    public void The_display_name_falls_back_sensibly()
    {
        var entries = new[]
        {
            new LdapDirectoryUser { Id = "1", Dn = "CN=a", GivenName = "Ann", Surname = "Smith", SamAccountName = "asmith", Mail = "a@corp.com" },
            new LdapDirectoryUser { Id = "2", Dn = "CN=b", DisplayName = "  ", SamAccountName = "bjones", Mail = "b@corp.com" },
            new LdapDirectoryUser { Id = "3", Dn = "CN=c", Mail = "c@corp.com" },
            new LdapDirectoryUser { Id = "4", Dn = "CN=d", DisplayName = new string('x', 250), Mail = "d@corp.com" }
        };
        var rows = DirectoryImportRules.Classify(entries, "mail", NoUsers, Departments).Rows.ToDictionary(r => r.Key);

        Assert.Equal("Ann Smith", rows["1"].DisplayName);
        Assert.Equal("bjones", rows["2"].DisplayName);
        Assert.Equal("c@corp.com", rows["3"].DisplayName);
        Assert.Equal(200, rows["4"].DisplayName.Length);
    }

    /// <summary>DIM-010: the unlinked departments count importable people only, grouped by name, most first, "no department" last; rows sort by name.</summary>
    [Fact]
    public void Unlinked_departments_are_grouped_and_counted()
    {
        var result = Classify(null,
            Entry("Zed", "zed@corp.com", "Warehouse"),
            Entry("Amy", "amy@corp.com", "warehouse "),
            Entry("Bea", "bea@corp.com", "Legal"),
            Entry("Cy", "cy@corp.com"),
            Entry("Di", "di@corp.com", "Legal", disabled: true),
            Entry("Ed", "ed@corp.com", "Finance"));

        Assert.Equal(["Amy", "Bea", "Cy", "Di", "Ed", "Zed"], result.Rows.Select(r => r.DisplayName));
        Assert.Collection(result.Unlinked,
            u => { Assert.Equal("WAREHOUSE", u.Key); Assert.Equal(2, u.Count); },
            u => { Assert.Equal("Legal", u.Value); Assert.Equal(1, u.Count); },
            u => { Assert.True(u.IsBlank); Assert.Equal(1, u.Count); });
    }

    /// <summary>DIM-011: the department imported with is the linked one, else the admin's choice for that directory value, else none.</summary>
    [Fact]
    public void The_department_comes_from_the_link_then_the_mapping()
    {
        var rows = Classify(null,
            Entry("Ann", "ann@corp.com", "Finance"),
            Entry("Bob", "bob@corp.com", "Warehouse"),
            Entry("Cat", "cat@corp.com"),
            Entry("Dan", "dan@corp.com", "Legal")).Rows.ToDictionary(r => r.DisplayName);
        var mappings = new Dictionary<string, Guid?>
        {
            [DirectoryImportRules.DepartmentKey("Finance")] = SalesEmea.Id, // ignored: Ann is linked already
            [DirectoryImportRules.DepartmentKey("warehouse")] = Finance.Id,
            [string.Empty] = SalesEmea.Id,
            [DirectoryImportRules.DepartmentKey("Legal")] = null
        };

        Assert.Equal(Finance.Id, DirectoryImportRules.ResolveDepartment(rows["Ann"], mappings));
        Assert.Equal(Finance.Id, DirectoryImportRules.ResolveDepartment(rows["Bob"], mappings));
        Assert.Equal(SalesEmea.Id, DirectoryImportRules.ResolveDepartment(rows["Cat"], mappings));
        Assert.Null(DirectoryImportRules.ResolveDepartment(rows["Dan"], mappings));
        Assert.Null(DirectoryImportRules.ResolveDepartment(rows["Dan"], new Dictionary<string, Guid?>()));
    }
}
