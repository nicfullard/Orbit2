using System.ComponentModel.DataAnnotations;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Pages.Sprints;

public sealed class SprintForm
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    public string? Goal { get; set; }
    [DataType(DataType.Date)] public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    [DataType(DataType.Date)] public DateOnly EndDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(13);

    public SprintInput ToInput() => new() { Name = Name, Goal = Goal, StartDate = StartDate, EndDate = EndDate };

    public static SprintForm From(Sprint s) => new() { Name = s.Name, Goal = s.Goal, StartDate = s.StartDate, EndDate = s.EndDate };
}
