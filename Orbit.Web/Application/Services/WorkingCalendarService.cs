using Microsoft.EntityFrameworkCore;
using Orbit.Application.Models;
using Orbit.Application.Scheduling;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// The organisation working calendar (spec §6.17): the working week, a single row that exists once saved, plus dated
/// exceptions. Edited under Admin &gt; Working Calendar (calendar.manage); read without an actor by the analysis,
/// which is run by whoever may edit the project.
/// </summary>
public sealed class WorkingCalendarService(ApplicationDbContext db, IActorProvider actors, AuditService audit)
{
    /// <summary>The working week (defaults until first saved) and every exception, date order. Needs no actor.</summary>
    public async Task<WorkingCalendarView> GetAsync(CancellationToken ct = default)
    {
        var calendar = await db.WorkingCalendars.AsNoTracking().FirstOrDefaultAsync(c => c.Id == WellKnownIds.WorkingCalendarId, ct) ?? new WorkingCalendar();
        var exceptions = await db.WorkingCalendarExceptions.AsNoTracking().OrderBy(e => e.Date).ToListAsync(ct);
        return new WorkingCalendarView(calendar, exceptions);
    }

    /// <summary>The calendar as arithmetic, for the analysis engine.</summary>
    public async Task<WorkDayCalendar> BuildAsync(CancellationToken ct = default)
    {
        var view = await GetAsync(ct);
        return new WorkDayCalendar(view.Calendar.WorkingDays, view.Exceptions.Select(e => new CalendarDay(e.Date, e.IsWorking, e.Name)));
    }

    public async Task<WorkingCalendarView> UpdateAsync(WorkingCalendarInput input, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        if (!(input.Monday || input.Tuesday || input.Wednesday || input.Thursday || input.Friday || input.Saturday || input.Sunday))
            throw new ValidationException("At least one day of the week must be a working day.");

        var calendar = await db.WorkingCalendars.FirstOrDefaultAsync(c => c.Id == WellKnownIds.WorkingCalendarId, ct);
        var isNew = calendar is null;
        calendar ??= new WorkingCalendar();
        var changes = new ChangeSet()
            .Track("monday", calendar.MondayWorking, input.Monday)
            .Track("tuesday", calendar.TuesdayWorking, input.Tuesday)
            .Track("wednesday", calendar.WednesdayWorking, input.Wednesday)
            .Track("thursday", calendar.ThursdayWorking, input.Thursday)
            .Track("friday", calendar.FridayWorking, input.Friday)
            .Track("saturday", calendar.SaturdayWorking, input.Saturday)
            .Track("sunday", calendar.SundayWorking, input.Sunday);
        if (!changes.HasChanges && !isNew) return await GetAsync(ct);

        calendar.MondayWorking = input.Monday;
        calendar.TuesdayWorking = input.Tuesday;
        calendar.WednesdayWorking = input.Wednesday;
        calendar.ThursdayWorking = input.Thursday;
        calendar.FridayWorking = input.Friday;
        calendar.SaturdayWorking = input.Saturday;
        calendar.SundayWorking = input.Sunday;
        calendar.UpdatedAt = DateTime.UtcNow;
        calendar.UpdatedById = actor.UserId;
        if (isNew) db.WorkingCalendars.Add(calendar);

        audit.Add(actor, AuditEntity.WorkingCalendar, calendar.Id, isNew ? AuditAction.Created : AuditAction.Updated, null, "Working calendar", changes.Changes);
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    public async Task<WorkingCalendarException> AddExceptionAsync(CalendarExceptionInput input, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var date = input.Date ?? throw new ValidationException("A date is required.");
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new ValidationException("A name is required, e.g. the holiday's name.");
        if (name.Length > 100) throw new ValidationException("The name must be 100 characters or fewer.");
        if (await db.WorkingCalendarExceptions.AnyAsync(e => e.Date == date, ct))
            throw new ValidationException($"{date:d MMM yyyy} already has an exception. Remove it first to change it.");

        var exception = new WorkingCalendarException { Date = date, Name = name, IsWorking = input.IsWorking, CreatedById = actor.UserId };
        db.WorkingCalendarExceptions.Add(exception);
        audit.Add(actor, AuditEntity.WorkingCalendar, WellKnownIds.WorkingCalendarId, AuditAction.ExceptionAdded, null,
            $"{name} ({date:d MMM yyyy}, {(input.IsWorking ? "working" : "non-working")})", new { exception.Id, exception.Date, exception.Name, exception.IsWorking });
        await db.SaveChangesAsync(ct);
        return exception;
    }

    public async Task RemoveExceptionAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);
        var exception = await db.WorkingCalendarExceptions.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new NotFoundException("Calendar exception not found.");
        db.WorkingCalendarExceptions.Remove(exception);
        audit.Add(actor, AuditEntity.WorkingCalendar, WellKnownIds.WorkingCalendarId, AuditAction.ExceptionRemoved, null,
            $"{exception.Name} ({exception.Date:d MMM yyyy})", new { exception.Id, exception.Date, exception.Name, exception.IsWorking });
        await db.SaveChangesAsync(ct);
    }

    private async Task<Actor> RequireAdminAsync(CancellationToken ct)
    {
        var actor = await actors.GetAsync(ct);
        AccessPolicy.Require(AccessPolicy.CanManageWorkingCalendar(actor), "You don't have permission to manage the working calendar.");
        return actor;
    }
}
