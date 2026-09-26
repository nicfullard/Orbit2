using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Orbit.Reporting;

/// <summary>Renders a report's rows as a simple tabular PDF via QuestPDF (Community licence).</summary>
public static class ReportPdfBuilder
{
    /// <summary>A captioned table. <paramref name="Widths"/> gives each column a relative width; omitted, the columns share the page equally.</summary>
    public sealed record Table(
        IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, string? Caption = null, IReadOnlyList<float>? Widths = null);

    /// <summary>An A4 document of <paramref name="tables"/>; <paramref name="landscape"/> turns the page for a wide table.</summary>
    public static byte[] Build(string title, string subtitle, IReadOnlyList<Table> tables, bool landscape = false)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().Text(title).FontSize(18).SemiBold();
                    col.Item().Text(subtitle).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Spacing(16);
                    foreach (var table in tables)
                    {
                        col.Item().Column(section =>
                        {
                            if (!string.IsNullOrWhiteSpace(table.Caption))
                                section.Item().PaddingBottom(4).Text(table.Caption).SemiBold().FontSize(12);

                            if (table.Rows.Count == 0)
                            {
                                section.Item().Text("No data for the selected filters.").Italic().FontColor(Colors.Grey.Darken1);
                                return;
                            }

                            section.Item().Table(t =>
                            {
                                t.ColumnsDefinition(cd =>
                                {
                                    for (var i = 0; i < table.Headers.Count; i++)
                                        cd.RelativeColumn(table.Widths is { } w && i < w.Count ? w[i] : 1);
                                });
                                t.Header(h =>
                                {
                                    foreach (var header in table.Headers)
                                        h.Cell().Element(HeaderCell).Text(header).SemiBold();
                                });
                                foreach (var row in table.Rows)
                                    foreach (var cell in row)
                                        t.Cell().Element(BodyCell).Text(cell);
                            });
                        });
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
                    x.Span($"Orbit - generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC - page ");
                    x.CurrentPageNumber();
                    x.Span(" of ");
                    x.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    private static IContainer HeaderCell(IContainer c) => c
        .Background(Colors.Grey.Lighten3)
        .BorderBottom(1).BorderColor(Colors.Grey.Lighten1)
        .PaddingVertical(5).PaddingHorizontal(6);

    private static IContainer BodyCell(IContainer c) => c
        .BorderBottom(1).BorderColor(Colors.Grey.Lighten3)
        .PaddingVertical(4).PaddingHorizontal(6);
}
