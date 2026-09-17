using Orbit.Application.Models;

namespace Orbit.Pages.Reports;

public class IndexModel : OrbitPageModel
{
    public IReadOnlyList<ReportDefinition> Reports => ReportCatalog.All;
    public DateOnly DefaultFrom => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);
    public DateOnly DefaultTo => DateOnly.FromDateTime(DateTime.UtcNow);

    public void OnGet() { }
}
