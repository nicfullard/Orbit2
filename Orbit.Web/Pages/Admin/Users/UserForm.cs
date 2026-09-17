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
    public OrbitRole Role { get; set; } = OrbitRole.Member;
    public Guid? DepartmentId { get; set; }
    public AuthSource AuthSource { get; set; } = AuthSource.Local;
    [DataType(DataType.Password)] public string? Password { get; set; }

    public UserInput ToInput() => new() { Email = Email, DisplayName = DisplayName, Role = Role, DepartmentId = DepartmentId, AuthSource = AuthSource, Password = Password };

    public static UserForm From(UserSummary u) => new() { Email = u.Email, DisplayName = u.DisplayName, Role = u.Role, DepartmentId = u.DepartmentId, AuthSource = u.AuthSource };

    public static async Task<IReadOnlyList<SelectListItem>> DepartmentItemsAsync(DepartmentService departments, Guid? selected, CancellationToken ct)
    {
        var items = new List<SelectListItem> { new("(none - System Admin only)", string.Empty, selected is null) };
        items.AddRange((await departments.ListAsync(false, ct)).Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == selected)));
        return items;
    }
}
