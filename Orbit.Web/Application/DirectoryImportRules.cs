using System.Text.RegularExpressions;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;

namespace Orbit.Application;

/// <summary>
/// The rules of "Import from directory" (spec §6.13), pure so they can be unit-tested: which directory entries to list
/// by default, where an imported user's email comes from, and how each entry is classified against the users and
/// departments Orbit already has.
/// </summary>
public static partial class DirectoryImportRules
{
    public const string DefaultDepartmentAttribute = "department";
    public const int MaxFilterLength = 1000;
    public const int MaxSearchBaseLength = 500;
    private const int MaxDisplayNameLength = 200;

    public const string MailAttribute = "mail";
    public const string UpnAttribute = "userPrincipalName";

    /// <summary>
    /// People (not computers or contacts) that the sign-in filter could find: the sign-in filter with its <c>{0}</c>
    /// replaced by <c>*</c>, so <c>(mail={0})</c> lists everyone with a mail attribute, and a <c>memberOf</c> restriction
    /// on sign-in restricts the list too. The admin can edit it before listing.
    /// </summary>
    public static string DefaultListFilter(string? userFilter)
    {
        var signIn = string.IsNullOrWhiteSpace(userFilter) ? "(mail={0})" : userFilter.Trim();
        return $"(&(objectCategory=person)(objectClass=user){signIn.Replace("{0}", "*", StringComparison.Ordinal)})";
    }

    /// <summary>
    /// A directory user signs in with their Orbit email, which the sign-in filter compares with some attribute. The
    /// imported email has to come from that same attribute or the person couldn't sign in: <c>userPrincipalName</c>
    /// when the filter compares that, otherwise <c>mail</c> - with a warning when the filter compares something else.
    /// </summary>
    public static EmailSource EmailSourceFor(string? userFilter)
    {
        var compared = ComparedWithSignInName().Matches(userFilter ?? string.Empty).Select(m => m.Groups[1].Value).ToList();
        if (compared.Any(a => a.Equals(MailAttribute, StringComparison.OrdinalIgnoreCase)))
            return new EmailSource(MailAttribute, null);
        if (compared.Any(a => a.Equals(UpnAttribute, StringComparison.OrdinalIgnoreCase)))
            return new EmailSource(UpnAttribute, null);
        return new EmailSource(MailAttribute,
            "The sign-in filter doesn't compare the sign-in email with mail or userPrincipalName, so Orbit can't tell which attribute people sign in with. " +
            "Imported emails are taken from mail: check a few with Test connection under Admin > Directory before relying on them.");
    }

    public static string ValidateListFilter(string? filter)
    {
        var f = filter?.Trim() ?? string.Empty;
        if (f.Length == 0) throw new ValidationException("A filter is required.");
        if (f.Length > MaxFilterLength) throw new ValidationException($"The filter must be {MaxFilterLength} characters or fewer.");
        if (!f.StartsWith('(') || !f.EndsWith(')') || !Balanced(f))
            throw new ValidationException("The filter must be an LDAP filter in balanced parentheses, e.g. (&(objectCategory=person)(objectClass=user)).");
        if (f.Contains("{0}", StringComparison.Ordinal))
            throw new ValidationException("This filter lists people, so it has no {0}: that belongs to the sign-in filter. Use * to mean \"any value\", e.g. (mail=*).");
        return f;
    }

    /// <summary>Null means "the directory settings' search base".</summary>
    public static string? ValidateSearchBase(string? searchBase)
    {
        var s = searchBase?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length > MaxSearchBaseLength) throw new ValidationException($"The search base must be {MaxSearchBaseLength} characters or fewer.");
        return s;
    }

    public static string ValidateDepartmentAttribute(string? attribute)
    {
        var a = attribute?.Trim();
        if (string.IsNullOrEmpty(a)) return DefaultDepartmentAttribute;
        if (!AttributeName().IsMatch(a))
            throw new ValidationException($"\"{a}\" isn't an LDAP attribute name. Use a name such as department, company or extensionAttribute1.");
        return a;
    }

    /// <summary>How a department name is compared: case, surrounding and repeated spaces don't matter. Blank is "no department".</summary>
    public static string DepartmentKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Whitespace().Replace(value.Trim(), " ").ToUpperInvariant();

    /// <summary>
    /// Classifies each directory entry. <paramref name="existingEmails"/> maps the email of every Orbit user, active or
    /// not, to their id, compared ignoring case. Rows come back sorted by name; the unlinked departments list the
    /// directory values of importable people that no active Orbit department matches, most people first.
    /// </summary>
    public static DirectoryImportClassification Classify(
        IEnumerable<LdapDirectoryUser> entries,
        string emailAttribute,
        IReadOnlyDictionary<string, Guid> existingEmails,
        IEnumerable<ImportDepartment> departments)
    {
        var existing = new Dictionary<string, Guid>(existingEmails, StringComparer.OrdinalIgnoreCase);
        var active = new Dictionary<string, ImportDepartment>();
        var archived = new Dictionary<string, ImportDepartment>();
        foreach (var d in departments)
            (d.IsArchived ? archived : active).TryAdd(DepartmentKey(d.Name), d);

        var list = entries.ToList();
        var useUpn = emailAttribute.Equals(UpnAttribute, StringComparison.OrdinalIgnoreCase);
        var emails = list.Select(e => ValidEmail(useUpn ? e.UserPrincipalName : e.Mail)).ToList();
        var emailCounts = emails.OfType<string>().GroupBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<DirectoryImportRow>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var email = emails[i];
            Guid? existingId = email is not null && existing.TryGetValue(email, out var id) ? id : null;
            var status = email is null ? DirectoryImportStatus.NoEmail
                : existingId is not null ? DirectoryImportStatus.InOrbit
                : e.Disabled ? DirectoryImportStatus.Disabled
                : emailCounts[email] > 1 ? DirectoryImportStatus.DuplicateEmail
                : DirectoryImportStatus.Ready;

            var key = DepartmentKey(e.Department);
            var linked = key.Length > 0 && active.TryGetValue(key, out var match) ? match : null;
            var archivedMatch = linked is null && key.Length > 0 && archived.TryGetValue(key, out var old) ? old.Name : null;

            rows.Add(new DirectoryImportRow(
                Key: string.IsNullOrEmpty(e.Id) ? e.Dn : e.Id,
                Dn: e.Dn,
                DisplayName: DisplayName(e, email),
                Email: email,
                Account: e.SamAccountName ?? e.UserPrincipalName,
                Title: e.Title,
                DirectoryDepartment: string.IsNullOrWhiteSpace(e.Department) ? null : e.Department.Trim(),
                Status: status,
                DepartmentId: linked?.Id,
                DepartmentName: linked?.Name,
                ArchivedMatch: archivedMatch,
                ExistingUserId: existingId));
        }

        rows.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        var unlinked = rows.Where(r => r.CanImport && !r.IsLinked)
            .GroupBy(r => r.DepartmentKey)
            .Select(g => new UnlinkedDepartment(g.Key, g.First().DirectoryDepartment ?? string.Empty, g.Count(), g.First().ArchivedMatch))
            .OrderBy(u => u.IsBlank).ThenByDescending(u => u.Count).ThenBy(u => u.Value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new DirectoryImportClassification(rows, unlinked);
    }

    /// <summary>The department an imported person gets: the one their directory value matched, else the admin's mapping for that value, else none.</summary>
    public static Guid? ResolveDepartment(DirectoryImportRow row, IReadOnlyDictionary<string, Guid?> mappings) =>
        row.DepartmentId ?? mappings.GetValueOrDefault(row.DepartmentKey);

    public static string StatusLabel(DirectoryImportStatus status) => status switch
    {
        DirectoryImportStatus.Ready => "Ready",
        DirectoryImportStatus.InOrbit => "Already in Orbit",
        DirectoryImportStatus.NoEmail => "No email in the directory",
        DirectoryImportStatus.Disabled => "Disabled in the directory",
        DirectoryImportStatus.DuplicateEmail => "Email shared with another entry",
        _ => status.ToString()
    };

    private static string? ValidEmail(string? value)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) || !v.Contains('@') || v.Length > 256 ? null : v;
    }

    private static string DisplayName(LdapDirectoryUser e, string? email)
    {
        var fullName = string.Join(' ', new[] { e.GivenName, e.Surname }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        var name = new[] { e.DisplayName, fullName, e.SamAccountName, email, e.Dn }.First(s => !string.IsNullOrWhiteSpace(s))!.Trim();
        return name.Length <= MaxDisplayNameLength ? name : name[..MaxDisplayNameLength].TrimEnd();
    }

    private static bool Balanced(string filter)
    {
        var depth = 0;
        foreach (var c in filter)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    [GeneratedRegex(@"\(\s*([A-Za-z][A-Za-z0-9-]*)\s*=\s*\{0\}\s*\)")]
    private static partial Regex ComparedWithSignInName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{0,63}$")]
    private static partial Regex AttributeName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
