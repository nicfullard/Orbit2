using Microsoft.EntityFrameworkCore;
using Orbit.Agents;
using Orbit.Agents.Contracts;
using Orbit.Application.Models;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// "Import from directory" (spec §6.13, users.manage): lists the directory's users through an Orbit Agent, shows how
/// each matches Orbit's users and departments, and creates the people the admin ticks as directory users. Each one is
/// created by <see cref="UserAdminService.CreateAsync"/>, so every rule and the audit entry are exactly those of a user
/// created by hand. Nothing read from the directory is kept beyond the accounts created.
/// </summary>
public sealed class DirectoryImportService(
    ApplicationDbContext db,
    IActorProvider actors,
    UserAdminService users,
    DirectoryAuthService directory,
    LdapSettingsService ldapSettings,
    AgentConnectionRegistry registry)
{
    /// <summary>The most entries one listing reads; beyond it, narrow the filter or the search base.</summary>
    public const int MaxUsers = 5000;

    public async Task<DirectoryImportSetup> GetSetupAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        var settings = await db.LdapSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == WellKnownIds.LdapSettingsId, ct);
        var defaults = new DirectoryImportQuery
        {
            Filter = DirectoryImportRules.DefaultListFilter(settings?.UserFilter),
            SearchBase = settings?.SearchBase,
            DepartmentAttribute = DirectoryImportRules.DefaultDepartmentAttribute
        };
        return new DirectoryImportSetup(settings?.Enabled == true, defaults, DirectoryImportRules.EmailSourceFor(settings?.UserFilter),
            registry.WithCapability(AgentCapabilities.LdapAuthenticate).Count,
            registry.WithCapability(AgentCapabilities.LdapListUsers).Count);
    }

    public async Task<DirectoryImportPreview> PreviewAsync(DirectoryImportQuery query, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        return await ListAsync(query, ct);
    }

    /// <summary>
    /// Creates the ticked people who can be imported, all with one role. Refuses before creating anyone when the role
    /// needs a department and a ticked person has none; otherwise each person succeeds or fails alone, since each
    /// account is saved as it is created.
    /// </summary>
    public async Task<DirectoryImportOutcome> ImportAsync(DirectoryImportRequest request, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        var selected = request.Selected.Where(k => !string.IsNullOrWhiteSpace(k)).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) throw new ValidationException("Tick at least one person to import.");
        var role = await RoleResolver.ForRoleAsync(db, request.RoleId, ct) ?? throw new ValidationException("Choose a role.");
        var mappings = await ValidateMappingsAsync(request.Mappings, ct);

        // The directory is read again rather than trusting rows posted back by the page: what is created reflects the
        // directory and Orbit as they are now, and someone imported meanwhile shows up as already in Orbit.
        var preview = await ListAsync(request.Query, ct);
        var chosen = preview.Rows.Where(r => selected.Contains(r.Key)).ToList();
        var ready = chosen.Where(r => r.CanImport).ToList();

        if (role.RequiresDepartment)
        {
            var homeless = ready.Count(r => DirectoryImportRules.ResolveDepartment(r, mappings) is null);
            if (homeless > 0)
                throw new ValidationException(
                    $"{People(homeless)} ticked {(homeless == 1 ? "has" : "have")} no department, and the {role.Name} role has permissions scoped to a department. " +
                    "Choose an Orbit department for their directory department, untick them, or choose another role. Nobody was imported.");
        }

        var skipped = chosen.Where(r => !r.CanImport)
            .Select(r => new SkippedUser(r.DisplayName, r.Email, DirectoryImportRules.StatusLabel(r.Status)))
            .ToList();
        var missing = selected.Count - chosen.Count;
        if (missing > 0)
            skipped.Add(new SkippedUser($"{People(missing)} ticked", null, "No longer listed by the directory with this filter."));

        var imported = new List<ImportedUser>();
        foreach (var row in ready)
        {
            try
            {
                var user = await users.CreateAsync(new UserInput
                {
                    Email = row.Email!,
                    DisplayName = row.DisplayName,
                    RoleId = role.Id,
                    DepartmentId = DirectoryImportRules.ResolveDepartment(row, mappings),
                    AuthSource = AuthSource.Ldap,
                    DirectoryDn = row.Dn
                }, ct);
                imported.Add(new ImportedUser(user.Id, user.DisplayName, user.Email, user.DepartmentName));
            }
            catch (ValidationException ex)
            {
                skipped.Add(new SkippedUser(row.DisplayName, row.Email, ex.Message));
            }
            catch (DbUpdateException)
            {
                // Typically the same email created elsewhere a moment ago. Drop the failed insert so the next person saves cleanly.
                db.ChangeTracker.Clear();
                skipped.Add(new SkippedUser(row.DisplayName, row.Email, "Couldn't be saved; a user with this email may have just been created. Load the directory again."));
            }
        }
        return new DirectoryImportOutcome(role.Name, imported, skipped);
    }

    private async Task<DirectoryImportPreview> ListAsync(DirectoryImportQuery query, CancellationToken ct)
    {
        var connection = await ldapSettings.GetForDispatchAsync(ct)
            ?? throw new ValidationException("Directory (LDAP) sign-in isn't enabled or fully set up. Imported users sign in with it, so set it up under Admin > Directory first.");
        var normalised = new DirectoryImportQuery
        {
            Filter = DirectoryImportRules.ValidateListFilter(query.Filter),
            SearchBase = DirectoryImportRules.ValidateSearchBase(query.SearchBase),
            DepartmentAttribute = DirectoryImportRules.ValidateDepartmentAttribute(query.DepartmentAttribute)
        };

        var outcome = await directory.ListUsersAsync(new LdapListUsersRequest
        {
            Settings = connection,
            Filter = normalised.Filter,
            SearchBase = normalised.SearchBase,
            DepartmentAttribute = normalised.DepartmentAttribute,
            MaxResults = MaxUsers
        }, ct);
        if (outcome.Result is null)
            throw new ValidationException(outcome.Error ?? "No Orbit Agent could list the directory.");
        if (!outcome.Result.Success)
            throw new ValidationException($"Agent \"{outcome.AgentName}\" couldn't list the directory: {outcome.Result.Error}.");

        var emailSource = DirectoryImportRules.EmailSourceFor(connection.UserFilter);
        var emails = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => new { u.Email, u.Id }).ToListAsync(ct))
            emails.TryAdd(u.Email!, u.Id);
        var departments = await db.Departments.AsNoTracking().Select(d => new ImportDepartment(d.Id, d.Name, d.IsArchived)).ToListAsync(ct);

        var classified = DirectoryImportRules.Classify(outcome.Result.Users, emailSource.Attribute, emails, departments);
        return new DirectoryImportPreview(outcome.AgentName, normalised, emailSource, classified.Rows, classified.Unlinked, outcome.Result.Truncated);
    }

    /// <summary>Keyed by <see cref="DirectoryImportRules.DepartmentKey"/>; each chosen department must exist and not be archived.</summary>
    private async Task<Dictionary<string, Guid?>> ValidateMappingsAsync(IEnumerable<DirectoryImportMapping> mappings, CancellationToken ct)
    {
        var list = mappings.ToList();
        var ids = list.Where(m => m.DepartmentId is not null).Select(m => m.DepartmentId!.Value).Distinct().ToList();
        var usable = (await db.Departments.AsNoTracking().Where(d => ids.Contains(d.Id) && !d.IsArchived).Select(d => d.Id).ToListAsync(ct)).ToHashSet();
        var result = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var m in list)
        {
            if (m.DepartmentId is Guid id && !usable.Contains(id))
                throw new ValidationException("A department chosen for a directory department no longer exists or has been archived. Load the directory again.");
            result[DirectoryImportRules.DepartmentKey(m.Key)] = m.DepartmentId;
        }
        return result;
    }

    private static string People(int n) => n == 1 ? "1 person" : $"{n} people";

    private async Task<Actor> RequireAdminAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageUsers(actor), "You don't have permission to manage users.");
        return actor;
    }
}
