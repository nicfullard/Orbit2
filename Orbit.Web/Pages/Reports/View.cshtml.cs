using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Orbit.Application.Models;
using Orbit.Application.Services;
using Orbit.Reporting;

namespace Orbit.Pages.Reports;

public class ViewModel(ReportingService reporting, ProjectService projects, DepartmentService departments) : OrbitPageModel
{
    [BindProperty(SupportsGet = true)] public ReportKind Kind { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? DepartmentId { get; set; }

    public ReportDefinition Definition { get; private set; } = null!;
    public IReadOnlyList<PersonCountRow>? CountRows { get; private set; }
    public MeanTimeReport? MeanTime { get; private set; }
    public IReadOnlyList<SelectListItem> ProjectItems { get; private set; } = [];
    public IReadOnlyList<SelectListItem> DepartmentItems { get; private set; } = [];
    public string FilterSummary { get; private set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await RunAsync(ct);
        ProjectItems = (await projects.ListOpenForPickerAsync(ct: ct))
            .Select(p => new SelectListItem($"{p.Department.Name} / {p.Name}", p.Id.ToString(), p.Id == ProjectId)).ToList();
        DepartmentItems = (await departments.ListAsync(true, ct))
            .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == DepartmentId)).ToList();
    }

    public async Task<IActionResult> OnGetPdfAsync(CancellationToken ct)
    {
        await RunAsync(ct);
        var tables = new List<ReportPdfBuilder.Table>();
        if (CountRows is not null)
        {
            tables.Add(new ReportPdfBuilder.Table(["Person", "Count"],
                CountRows.Select(r => (IReadOnlyList<string>)[r.Name, r.Count.ToString()]).ToList()));
        }
        if (MeanTime is not null)
        {
            var rows = MeanTime.Rows.Select(r => (IReadOnlyList<string>)[r.Name, r.Count.ToString(), DurationFormat.Hours(r.AverageHours)]).ToList();
            rows.Add(["Overall", MeanTime.OverallCount.ToString(), DurationFormat.Hours(MeanTime.OverallAverageHours)]);
            tables.Add(new ReportPdfBuilder.Table(["Assignee", "Tasks", "Average"], rows));
        }
        var bytes = ReportPdfBuilder.Build(Definition.Title, FilterSummary, tables);
        var fileName = $"orbit-{Kind.ToString().ToLowerInvariant()}-{From:yyyyMMdd}-{To:yyyyMMdd}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Definition = ReportCatalog.Get(Kind);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        To ??= today;
        From ??= To.Value.AddDays(-30);
        if (To < From) (From, To) = (To, From);

        var fromUtc = DateTime.SpecifyKind(From.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var filter = new ReportFilter(fromUtc, toUtc, ProjectId, DepartmentId);

        switch (Kind)
        {
            case ReportKind.ClosedByPerson: CountRows = await reporting.ClosedByPersonAsync(filter, ct); break;
            case ReportKind.CreatedByPerson: CountRows = await reporting.CreatedByPersonAsync(filter, ct); break;
            case ReportKind.MeanTimeToRespond: MeanTime = await reporting.MeanTimeToRespondAsync(filter, ct); break;
            case ReportKind.MeanTimeToResolve: MeanTime = await reporting.MeanTimeToResolveAsync(filter, ct); break;
        }

        var parts = new List<string> { $"{From:yyyy-MM-dd} to {To:yyyy-MM-dd}" };
        if (DepartmentId is Guid d) parts.Add("Department: " + ((await departments.GetAsync(d, ct)).Name));
        if (ProjectId is Guid p) parts.Add("Project: " + (await projects.GetStatusAsync(p, ct)).Name);
        FilterSummary = string.Join(" · ", parts);
    }
}
