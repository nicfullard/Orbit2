namespace Orbit.Application.Models;

/// <summary>What "Import from directory" lists (spec §6.13): the page's query card. Blank fields take the defaults.</summary>
public sealed class DirectoryImportQuery
{
    /// <summary>A complete LDAP filter; blank means <see cref="DirectoryImportRules.DefaultListFilter"/> of the sign-in filter.</summary>
    public string? Filter { get; set; }
    /// <summary>Blank searches the directory settings' search base.</summary>
    public string? SearchBase { get; set; }
    /// <summary>The attribute holding each person's department; blank means <c>department</c>.</summary>
    public string? DepartmentAttribute { get; set; }
}

/// <summary>Whether a directory entry can be imported, and if not, why.</summary>
public enum DirectoryImportStatus
{
    Ready,
    /// <summary>An Orbit user (active or not) already has this email.</summary>
    InOrbit,
    /// <summary>The attribute people sign in with is empty or isn't an email address.</summary>
    NoEmail,
    /// <summary>Disabled in the directory: the account couldn't sign in anyway.</summary>
    Disabled,
    /// <summary>Two entries share the email, so a directory sign-in with it would be ambiguous and refused.</summary>
    DuplicateEmail
}

/// <summary>A department as the import rules see it.</summary>
public sealed record ImportDepartment(Guid Id, string Name, bool IsArchived);

/// <summary>One directory entry on the import page, classified against Orbit's users and departments.</summary>
public sealed record DirectoryImportRow(
    string Key,
    string Dn,
    string DisplayName,
    string? Email,
    string? Account,
    string? Title,
    /// <summary>The raw value of the department attribute, as the directory holds it.</summary>
    string? DirectoryDepartment,
    DirectoryImportStatus Status,
    /// <summary>The Orbit department the directory's value matched by name; null when it couldn't be linked.</summary>
    Guid? DepartmentId,
    string? DepartmentName,
    /// <summary>The name of an archived department the value matches. Archived departments aren't linked.</summary>
    string? ArchivedMatch,
    Guid? ExistingUserId)
{
    public bool CanImport => Status == DirectoryImportStatus.Ready;
    public bool IsLinked => DepartmentId is not null;
    public string DepartmentKey => DirectoryImportRules.DepartmentKey(DirectoryDepartment);
}

/// <summary>A directory department value no Orbit department matched, with how many importable people carry it.</summary>
public sealed record UnlinkedDepartment(string Key, string Value, int Count, string? ArchivedMatch)
{
    public bool IsBlank => Key.Length == 0;
}

public sealed record DirectoryImportClassification(IReadOnlyList<DirectoryImportRow> Rows, IReadOnlyList<UnlinkedDepartment> Unlinked);

/// <summary>Which attribute supplies Orbit's email (the sign-in name), and a warning when the sign-in filter leaves that unclear.</summary>
public sealed record EmailSource(string Attribute, string? Warning);

/// <summary>What the import page shows before anything is listed.</summary>
public sealed record DirectoryImportSetup(
    bool DirectoryEnabled,
    DirectoryImportQuery Defaults,
    EmailSource EmailSource,
    int AgentsConnected,
    int AgentsAbleToList);

public sealed record DirectoryImportPreview(
    string? AgentName,
    DirectoryImportQuery Query,
    EmailSource EmailSource,
    IReadOnlyList<DirectoryImportRow> Rows,
    IReadOnlyList<UnlinkedDepartment> Unlinked,
    bool Truncated)
{
    public int ReadyCount => Rows.Count(r => r.CanImport);
    public int InOrbitCount => Rows.Count(r => r.Status == DirectoryImportStatus.InOrbit);
    public int BlockedCount => Rows.Count(r => r.Status is not (DirectoryImportStatus.Ready or DirectoryImportStatus.InOrbit));
    public int UnlinkedCount => Unlinked.Sum(u => u.Count);
}

/// <summary>The admin's choice for one unlinked directory department value: an Orbit department, or none.</summary>
public sealed record DirectoryImportMapping(string Key, Guid? DepartmentId);

public sealed class DirectoryImportRequest
{
    public DirectoryImportQuery Query { get; set; } = new();
    /// <summary><see cref="DirectoryImportRow.Key"/>s of the people ticked.</summary>
    public IReadOnlyCollection<string> Selected { get; set; } = [];
    /// <summary>One role for everyone in this import.</summary>
    public Guid RoleId { get; set; }
    public IReadOnlyList<DirectoryImportMapping> Mappings { get; set; } = [];
}

public sealed record ImportedUser(Guid Id, string DisplayName, string Email, string? DepartmentName);

public sealed record SkippedUser(string DisplayName, string? Email, string Reason);

public sealed record DirectoryImportOutcome(string RoleName, IReadOnlyList<ImportedUser> Imported, IReadOnlyList<SkippedUser> Skipped);
