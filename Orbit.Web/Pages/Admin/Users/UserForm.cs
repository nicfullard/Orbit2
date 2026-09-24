using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;

namespace Orbit.Pages.Admin.Users;

public sealed class UserForm
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, StringLength(200)] public string DisplayName { get; set; } = string.Empty;
    [Required] public Guid RoleId { get; set; }
    public Guid? DepartmentId { get; set; }
    public AuthSource AuthSource { get; set; } = AuthSource.Local;
    [DataType(DataType.Password)] public string? Password { get; set; }

    public UserInput ToInput() => new() { Email = Email, DisplayName = DisplayName, RoleId = RoleId, DepartmentId = DepartmentId, AuthSource = AuthSource, Password = Password };

    public static UserForm From(UserSummary u) => new() { Email = u.Email, DisplayName = u.DisplayName, RoleId = u.Role.Id, DepartmentId = u.DepartmentId, AuthSource = u.AuthSource };

    /// <summary>"(none)" is offered; the form script withholds it while the chosen role needs a department, and the service checks regardless.</summary>
    public static async Task<IReadOnlyList<SelectListItem>> DepartmentItemsAsync(DepartmentService departments, Guid? selected, CancellationToken ct)
    {
        var items = new List<SelectListItem> { new("(none)", string.Empty, selected is null) };
        items.AddRange((await departments.ListAsync(false, ct)).Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == selected)));
        return items;
    }

    /// <summary>The default role for a new user or key: the shipped Member role when it still exists, else the first editable role.</summary>
    public static Guid DefaultRole(IReadOnlyList<RolePickerItem> roles) =>
        (roles.FirstOrDefault(r => r.Name == Orbit.Application.DefaultRoles.Member) ?? roles.FirstOrDefault(r => !r.IsBuiltIn) ?? roles.FirstOrDefault())?.Id ?? Guid.Empty;
}
