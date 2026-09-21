using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Pages.Admin.Calendar;

/// <summary>Admin &gt; Working Calendar (spec §6.17): the working week and dated exceptions that critical path analysis counts in.</summary>
public class IndexModel(WorkingCalendarService calendars) : OrbitPageModel
{
    [BindProperty] public WeekForm Week { get; set; } = new();
    [BindProperty] public ExceptionForm NewException { get; set; } = new();

    public IReadOnlyList<WorkingCalendarException> Exceptions { get; private set; } = [];
    public DateTime? UpdatedAt { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var view = await LoadAsync(ct);
        Week = WeekForm.From(view.Calendar);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            await calendars.UpdateAsync(Week.ToInput(), ct);
            Success("Working week saved.");
            return RedirectToPage();
        }
        catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAddExceptionAsync(CancellationToken ct)
    {
        try
        {
            var added = await calendars.AddExceptionAsync(NewException.ToInput(), ct);
            Success($"{added.Name} ({added.Date:d MMM yyyy}) added as a {(added.IsWorking ? "working" : "non-working")} day.");
            return RedirectToPage();
        }
        catch (ValidationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        var view = await LoadAsync(ct);
        Week = WeekForm.From(view.Calendar);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveExceptionAsync(Guid id, CancellationToken ct)
    {
        await calendars.RemoveExceptionAsync(id, ct);
        Success("Calendar exception removed.");
        return RedirectToPage();
    }

    private async Task<WorkingCalendarView> LoadAsync(CancellationToken ct)
    {
        var view = await calendars.GetAsync(ct);
        Exceptions = view.Exceptions;
        UpdatedAt = view.Calendar.UpdatedAt;
        return view;
    }
}

public sealed class WeekForm
{
    // Checkboxes post nothing when unticked, so these must default to false for "unticked" to bind as false.
    public bool Monday { get; set; }
    public bool Tuesday { get; set; }
    public bool Wednesday { get; set; }
    public bool Thursday { get; set; }
    public bool Friday { get; set; }
    public bool Saturday { get; set; }
    public bool Sunday { get; set; }

    public WorkingCalendarInput ToInput() => new()
    {
        Monday = Monday, Tuesday = Tuesday, Wednesday = Wednesday, Thursday = Thursday, Friday = Friday, Saturday = Saturday, Sunday = Sunday
    };

    public static WeekForm From(WorkingCalendar c) => new()
    {
        Monday = c.MondayWorking, Tuesday = c.TuesdayWorking, Wednesday = c.WednesdayWorking, Thursday = c.ThursdayWorking,
        Friday = c.FridayWorking, Saturday = c.SaturdayWorking, Sunday = c.SundayWorking
    };

    public bool IsWorking(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Monday,
        DayOfWeek.Tuesday => Tuesday,
        DayOfWeek.Wednesday => Wednesday,
        DayOfWeek.Thursday => Thursday,
        DayOfWeek.Friday => Friday,
        DayOfWeek.Saturday => Saturday,
        _ => Sunday
    };
}

public sealed class ExceptionForm
{
    public DateOnly? Date { get; set; }
    public string? Name { get; set; }
    public bool IsWorking { get; set; }

    public CalendarExceptionInput ToInput() => new() { Date = Date, Name = Name, IsWorking = IsWorking };
}
