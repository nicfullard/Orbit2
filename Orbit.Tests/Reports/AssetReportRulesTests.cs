using System.Text.Json;
using Orbit.Application;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Tests.Reports;

/// <summary>Asset status (spec §12).</summary>
public class AssetReportRulesTests
{
    private static readonly DateTime From = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 9, 26);

    // IT manages laptops (checked every 30 days) and phones (never checked) in its Office and Store; Works manages vans in Bay 1.
    private static readonly Guid It = Guid.NewGuid();
    private static readonly Guid Works = Guid.NewGuid();
    private static readonly Guid Laptop = Guid.NewGuid();
    private static readonly Guid Phone = Guid.NewGuid();
    private static readonly Guid Van = Guid.NewGuid();
    private static readonly Guid Office = Guid.NewGuid();
    private static readonly Guid Store = Guid.NewGuid();
    private static readonly Guid Bay = Guid.NewGuid();
    private static readonly Guid Alice = Guid.NewGuid();

    private static int _number;

    private static DateTime Sep(int day) => new(2026, 9, day, 10, 0, 0, DateTimeKind.Utc);
    private static DateOnly Date(int month, int day) => new(2026, month, day);

    private static AssetFacts Asset(
        Guid? type = null, Guid? location = null, AssetStatus status = AssetStatus.Active, DateOnly? disposedOn = null,
        DateTime? created = null, decimal? value = null, DateOnly? lastChecked = null, AssetCheckOutcome? outcome = null,
        int checks = 0, bool worked = false)
    {
        var t = type ?? Laptop;
        var (typeName, category, interval, department) =
            t == Laptop ? ("Laptop", "IT equipment", (int?)30, It)
            : t == Phone ? ("Phone", "IT equipment", null, It)
            : ("Van", "Vehicles", 180, Works);
        var locationName = location == Office ? "Office" : location == Store ? "Store" : location == Bay ? "Bay 1" : null;
        var n = Interlocked.Increment(ref _number);
        return new AssetFacts(Guid.NewGuid(), $"FA-{n:0000}", $"Asset {n}", status, disposedOn, created ?? From.AddDays(-200),
            department, department == It ? "IT" : "Works", t, typeName, category, interval, location, locationName, value,
            lastChecked, outcome ?? (lastChecked is null ? null : AssetCheckOutcome.Ok), checks, worked);
    }

    private static AssetTaskFacts Task(
        AssetFacts a, TaskItemStatus status = TaskItemStatus.InProgress, Guid? assignee = null, DateOnly? due = null,
        DateTime? created = null, DateTime? completed = null, int period = 0) =>
        new(Guid.NewGuid(), $"T-26-{Interlocked.Increment(ref _number):00000}", "Task", status, assignee, a.Id, due,
            created ?? From.AddDays(-50), completed, period);

    private static AssetStatusChange Change(AssetFacts a, DateTime at, AssetStatus from, AssetStatus to) => new(a.Id, at, from, to);

    private static AssetStatusHistory History(AssetFacts a, params AssetStatusChange[] changes) =>
        AssetReportRules.History(a.Status, a.DisposedOn, changes, From, To);

    private static AssetStatusReport Build(AssetFacts[] assets, AssetTaskFacts[]? tasks = null, AssetStatusChange[]? changes = null,
        DateTime? to = null)
    {
        var end = to ?? To;
        var histories = assets.ToDictionary(a => a.Id,
            a => AssetReportRules.History(a.Status, a.DisposedOn, (changes ?? []).Where(c => c.AssetId == a.Id), From, end));
        return AssetReportRules.Build(assets, histories, tasks ?? [], id => id == Alice ? "Alice" : "Unassigned", From, end, Today);
    }

    /// <summary>RPT-015: the status at the start and end of the period, and the changes in it, come from the changes since its start.</summary>
    [Fact]
    public void Status_over_the_period_is_rebuilt_from_the_changes_since_its_start()
    {
        // Damaged in the period and lost after it: the end of the period shows the first, "now" the second.
        var laptop = Asset(status: AssetStatus.Lost);
        var h = History(laptop,
            Change(laptop, To.AddDays(4), AssetStatus.Damaged, AssetStatus.Lost),
            Change(laptop, Sep(12), AssetStatus.Active, AssetStatus.Damaged));
        Assert.Equal((AssetStatus.Active, AssetStatus.Damaged, AssetStatus.Lost), (h.AtStart, h.AtEnd, h.Current));
        Assert.Equal(AssetStatus.Damaged, Assert.Single(h.Changes).To);

        // Nothing recorded: the current status throughout.
        h = History(Asset(status: AssetStatus.InStorage));
        Assert.Equal((AssetStatus.InStorage, AssetStatus.InStorage), (h.AtStart, h.AtEnd));
        Assert.Empty(h.Changes);
        Assert.True(h.WasInService);
    }

    /// <summary>RPT-015: a disposal counts from its disposal date, but never from before the change recorded ahead of it.</summary>
    [Fact]
    public void A_disposal_counts_from_its_disposal_date()
    {
        // Recorded after the period, back-dated into it: disposed of in the period.
        var late = Asset(status: AssetStatus.Disposed, disposedOn: Date(9, 25));
        var h = History(late, Change(late, To.AddDays(2), AssetStatus.Active, AssetStatus.Disposed));
        Assert.Equal((AssetStatus.Active, AssetStatus.Disposed), (h.AtStart, h.AtEnd));
        Assert.Equal(new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc), Assert.Single(h.Changes).At);

        // Recorded in the period, back-dated before it: disposed of throughout.
        var early = Asset(status: AssetStatus.Disposed, disposedOn: Date(8, 20));
        h = History(early, Change(early, Sep(10), AssetStatus.Active, AssetStatus.Disposed));
        Assert.Equal((AssetStatus.Disposed, AssetStatus.Disposed), (h.AtStart, h.AtEnd));
        Assert.Empty(h.Changes);
        Assert.False(h.WasInService);

        // Back-dated past a change recorded before it: it takes effect with that change, keeping their order.
        var damaged = Asset(status: AssetStatus.Disposed, disposedOn: Date(9, 5));
        h = History(damaged,
            Change(damaged, Sep(20), AssetStatus.Damaged, AssetStatus.Disposed),
            Change(damaged, Sep(12), AssetStatus.Active, AssetStatus.Damaged));
        Assert.Equal(AssetStatus.Active, h.AtStart);
        Assert.Equal([(AssetStatus.Active, Sep(12)), (AssetStatus.Damaged, Sep(12))], h.Changes.Select(c => (c.From, c.At)));

        // Reinstated since: the earlier disposal keeps the time it was recorded.
        var reinstated = Asset();
        h = History(reinstated,
            Change(reinstated, Sep(5), AssetStatus.Active, AssetStatus.Disposed),
            Change(reinstated, Sep(20), AssetStatus.Disposed, AssetStatus.Active));
        Assert.Equal(Sep(5), h.Changes[0].At);
        Assert.Equal(AssetStatus.Active, h.AtEnd);
    }

    /// <summary>RPT-015: status changes are read from the audit details the asset service writes, and nothing else.</summary>
    [Fact]
    public void Reads_the_status_change_the_audit_trail_records()
    {
        var disposal = JsonSerializer.Serialize(new
        {
            status = new { from = AssetStatus.Active, to = AssetStatus.Disposed },
            disposedOn = new { from = (DateOnly?)null, to = (DateOnly?)Date(9, 25) }
        }, OrbitJson.Options);
        Assert.True(AssetReportRules.TryReadStatusChange(disposal, out var from, out var to));
        Assert.Equal((AssetStatus.Active, AssetStatus.Disposed), (from, to));

        var inStorage = JsonSerializer.Serialize(new { status = new { from = AssetStatus.Damaged, to = AssetStatus.InStorage } }, OrbitJson.Options);
        Assert.True(AssetReportRules.TryReadStatusChange(inStorage, out from, out to));
        Assert.Equal((AssetStatus.Damaged, AssetStatus.InStorage), (from, to));

        // A new disposal date only: the same status on both sides.
        var redated = JsonSerializer.Serialize(new
        {
            status = new { from = AssetStatus.Disposed, to = AssetStatus.Disposed },
            disposedOn = new { from = (DateOnly?)Date(9, 25), to = (DateOnly?)Date(9, 20) }
        }, OrbitJson.Options);
        Assert.False(AssetReportRules.TryReadStatusChange(redated, out _, out _));

        var moved = JsonSerializer.Serialize(new ChangeSet().Track("location", "Office", "Store").Changes, OrbitJson.Options);
        Assert.False(AssetReportRules.TryReadStatusChange(moved, out _, out _));
        Assert.False(AssetReportRules.TryReadStatusChange("""{"name":"Laptop","status":"Active"}""", out _, out _)); // Created
        Assert.False(AssetReportRules.TryReadStatusChange("""{"status":{"from":"Active","to":"Broken"}}""", out _, out _));
        Assert.False(AssetReportRules.TryReadStatusChange("not json", out _, out _));
        Assert.False(AssetReportRules.TryReadStatusChange(null, out _, out _));
    }

    /// <summary>RPT-016: an asset is in the period when in service at some point in it, registered in it, or worked on in it.</summary>
    [Fact]
    public void Which_assets_are_in_the_period()
    {
        Assert.True(AssetReportRules.Includes(History(Asset()), registeredInPeriod: false, hadTaskActivity: false));

        // Disposed of before the period: only with work on a linked task in it.
        var gone = History(Asset(status: AssetStatus.Disposed, disposedOn: Date(6, 1)));
        Assert.False(AssetReportRules.Includes(gone, registeredInPeriod: false, hadTaskActivity: false));
        Assert.True(AssetReportRules.Includes(gone, registeredInPeriod: false, hadTaskActivity: true));

        // Disposed of during the period, and reinstated during it.
        var disposed = Asset(status: AssetStatus.Disposed, disposedOn: Date(9, 15));
        Assert.True(AssetReportRules.Includes(History(disposed, Change(disposed, Sep(15), AssetStatus.Active, AssetStatus.Disposed)), false, false));
        var reinstated = Asset();
        var back = History(reinstated, Change(reinstated, Sep(20), AssetStatus.Disposed, AssetStatus.Active));
        Assert.Equal(AssetStatus.Disposed, back.AtStart);
        Assert.True(AssetReportRules.Includes(back, false, false));

        // Registered in the period already disposed of: in, as a registration.
        var loaded = History(Asset(status: AssetStatus.Disposed, disposedOn: Date(9, 3), created: Sep(3)));
        Assert.False(loaded.WasInService);
        Assert.True(AssetReportRules.Includes(loaded, registeredInPeriod: true, hadTaskActivity: false));
    }

    /// <summary>RPT-017: statuses at the end of the period, registrations and the value at cost held and disposed of.</summary>
    [Fact]
    public void Counts_statuses_registrations_and_value()
    {
        var active = Asset(location: Office, value: 1000m);
        var damaged = Asset(location: Office, status: AssetStatus.Damaged, value: 800m);
        var spare = Asset(location: Store, status: AssetStatus.InStorage);
        var disposed = Asset(location: Store, status: AssetStatus.Disposed, disposedOn: Date(9, 15), value: 500m);
        var newPhone = Asset(Phone, status: AssetStatus.Lost, created: Sep(3), value: 200m);
        var report = Build([active, damaged, spare, disposed, newPhone], changes:
        [
            Change(damaged, Sep(12), AssetStatus.Active, AssetStatus.Damaged),
            Change(disposed, Sep(15), AssetStatus.Active, AssetStatus.Disposed)
        ]);

        var t = report.Total;
        Assert.Equal((1, 1, 1, 1, 1, 5, 4), (t.Active, t.InStorage, t.Damaged, t.Lost, t.Disposed, t.Total, t.Held));
        Assert.Equal(1, t.Registered);
        Assert.Equal(2000m, t.HeldValue);
        Assert.Equal(1, t.HeldUnvalued);
        Assert.Equal(500m, t.DisposedValue);

        var laptops = report.ByType.Single(r => r.Id == Laptop).Counts;
        Assert.Equal((4, 1800m, 500m), (laptops.Total, laptops.HeldValue, laptops.DisposedValue));
        Assert.Equal(t, report.ByType.Aggregate(AssetGroupCounts.None, (sum, r) => sum.Plus(r.Counts)));
        Assert.Equal(t, report.ByLocation.Aggregate(AssetGroupCounts.None, (sum, r) => sum.Plus(r.Counts)));

        // Registered already Lost: in that status from the start, so not a change in the period.
        Assert.Equal(
        [
            new AssetStatusCount(AssetStatus.Active, 1, 0), new AssetStatusCount(AssetStatus.InStorage, 1, 0),
            new AssetStatusCount(AssetStatus.Damaged, 1, 1), new AssetStatusCount(AssetStatus.Lost, 1, 0),
            new AssetStatusCount(AssetStatus.Disposed, 1, 1)
        ], report.StatusCounts);
    }

    /// <summary>RPT-018: checked in the period among the assets held at its end; overdue and not OK as at its last day or today.</summary>
    [Fact]
    public void Checks_are_read_as_at_the_end_of_the_period()
    {
        Assert.Equal(Today, AssetReportRules.AsAt(To, Today));                           // the period runs on past today
        Assert.Equal(Date(9, 15), AssetReportRules.AsAt(From.AddDays(15), Today));       // it ended on 15 September
        Assert.Equal(Today, AssetReportRules.AsAt(Today.AddDays(1).ToDateTime(TimeOnly.MinValue), Today));

        var checkedOk = Asset(lastChecked: Date(9, 10), checks: 1);                      // due 10 October
        var lapsed = Asset(lastChecked: Date(8, 1));                                     // due 31 August: overdue
        var neverChecked = Asset(created: new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc)); // due 31 July: overdue
        var notFound = Asset(lastChecked: Date(9, 20), outcome: AssetCheckOutcome.NotFound, checks: 1);
        var lost = Asset(status: AssetStatus.Lost, lastChecked: Date(8, 1));             // not scheduled
        var disposed = Asset(status: AssetStatus.Disposed, disposedOn: Date(9, 10), lastChecked: Date(9, 2), checks: 1);
        var phone = Asset(Phone);                                                        // its type has no interval
        var report = Build([checkedOk, lapsed, neverChecked, notFound, lost, disposed, phone],
            changes: [Change(disposed, Sep(10), AssetStatus.Active, AssetStatus.Disposed)]);

        var t = report.Total;
        Assert.Equal(Today, report.AsAt);
        Assert.Equal((6, 2, 2, 1), (t.Held, t.Checked, t.CheckOverdue, t.CheckNotOk));

        // A period that ended earlier: overdue as at its last day.
        var dueMidPeriod = Asset(lastChecked: Date(8, 10));                              // due 9 September
        var dueLater = Asset(lastChecked: Date(8, 20));                                  // due 19 September
        var past = Build([dueMidPeriod, dueLater], to: From.AddDays(15));
        Assert.Equal(Date(9, 15), past.AsAt);
        Assert.Equal(1, past.Total.CheckOverdue);
    }

    /// <summary>RPT-019: open and overdue linked tasks are now; created, done and logged count the period; the task list and its order.</summary>
    [Fact]
    public void Linked_tasks_are_summed_and_listed()
    {
        var laptop = Asset(location: Office);
        var overdue = Task(laptop, TaskItemStatus.Todo, Alice, due: Date(9, 20));
        var doneInPeriod = Task(laptop, TaskItemStatus.Done, created: Sep(2), completed: Sep(12), period: 90);
        var cancelledLongAgo = Task(laptop, TaskItemStatus.Cancelled);
        var doneBeforeButLogged = Task(laptop, TaskItemStatus.Done, completed: From.AddDays(-10), period: 30);
        var blocked = Task(laptop, TaskItemStatus.Blocked);
        var quiet = Asset(Van, Bay);
        var doneLongAgo = Task(quiet, TaskItemStatus.Done, completed: From.AddDays(-60));

        var report = Build([laptop, quiet], [overdue, doneInPeriod, cancelledLongAgo, doneBeforeButLogged, blocked, doneLongAgo]);

        Assert.Equal(new AssetTaskCounts(2, 1, 1, 1, 120), report.Total.Tasks);
        Assert.Equal(new AssetTaskCounts(2, 1, 1, 1, 120), report.ByLocation.Single(r => r.Id == Office).Counts.Tasks);
        Assert.Equal(AssetTaskCounts.None, report.ByType.Single(r => r.Id == Van).Counts.Tasks);

        // Only the asset with open or recent work is listed, with those tasks by status, then due date, then number.
        var row = Assert.Single(report.Assets);
        Assert.Equal(laptop.Id, row.AssetId);
        Assert.Equal(report.Total.Tasks, row.Tasks);
        Assert.Equal(new[] { overdue, blocked, doneInPeriod, doneBeforeButLogged }.Select(t => t.Id), row.TaskRows.Select(t => t.TaskId));
        Assert.True(row.TaskRows[0].IsOverdue);
        Assert.Equal("Alice", row.TaskRows[0].Assignee);
        Assert.Equal("Unassigned", row.TaskRows[1].Assignee);
        Assert.True(row.TaskRows[2].CreatedInPeriod && row.TaskRows[2].DoneInPeriod);
        Assert.False(row.TaskRows[3].DoneInPeriod);
    }

    /// <summary>RPT-020: by type with each type's locations, by location with each location's types; department, then name.</summary>
    [Fact]
    public void Groups_by_type_and_by_location()
    {
        var assets = new[]
        {
            Asset(Van, Bay), Asset(Van), Asset(Laptop, Store), Asset(Laptop), Asset(Laptop, Office), Asset(Laptop, Office),
            Asset(Phone, Office)
        };
        var report = Build(assets);

        Assert.Equal(["Laptop", "Phone", "Van"], report.ByType.Select(r => r.Name));
        Assert.Equal([("IT equipment", "IT"), ("IT equipment", "IT"), ("Vehicles", "Works")],
            report.ByType.Select(r => (r.Category!, r.DepartmentName)));
        var laptops = report.ByType[0];
        Assert.Equal(["Office", "Store", AssetReportRules.NoLocation], laptops.Breakdown.Select(r => r.Name));
        Assert.Equal([2, 1, 1], laptops.Breakdown.Select(r => r.Counts.Total));
        Assert.Null(laptops.Breakdown[2].Id);
        Assert.All(laptops.Breakdown, r => Assert.Empty(r.Breakdown));

        // "No location" is grouped per department, and comes last in each.
        Assert.Equal([("Office", "IT"), ("Store", "IT"), ("No location", "IT"), ("Bay 1", "Works"), ("No location", "Works")],
            report.ByLocation.Select(r => (r.Name, r.DepartmentName)));
        Assert.Equal(["Laptop", "Phone"], report.ByLocation[0].Breakdown.Select(r => r.Name));
        Assert.Equal(7, report.Total.Total);
    }
}
