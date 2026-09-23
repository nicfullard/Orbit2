using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Orbit.Data;

namespace Orbit.Application.Services;

/// <summary>
/// Human-readable numbers for tasks and projects (spec §5.1): T-26-00012 / P-26-00003 - a prefix, the two-digit year the
/// record was created in, and a counter that restarts at 1 each year. The counter lives in <c>NumberCounters</c> and is
/// bumped with one atomic upsert, so two people creating at the same moment never share a number; the unique index on
/// Number is the backstop. A number is taken before the row is saved, so a create that then fails leaves a gap - which is fine.
/// </summary>
public sealed partial class NumberingService(ApplicationDbContext db)
{
    public const string TaskPrefix = "T";
    public const string ProjectPrefix = "P";

    /// <summary>Hand out the next number for the prefix in the year of <paramref name="createdAtUtc"/>.</summary>
    public async Task<string> NextAsync(string prefix, DateTime createdAtUtc, CancellationToken ct = default)
    {
        var year = createdAtUtc.Year;
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            INSERT INTO "NumberCounters" ("Prefix", "Year", "Last") VALUES (@prefix, @year, 1)
            ON CONFLICT ("Prefix", "Year") DO UPDATE SET "Last" = "NumberCounters"."Last" + 1
            RETURNING "Last";
            """;
        Add(command, "prefix", prefix);
        Add(command, "year", year);
        var last = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        return Format(prefix, year, last);
    }

    /// <summary>"T-26-00012": prefix, two-digit year, at least five digits.</summary>
    public static string Format(string prefix, int year, int sequence) => $"{prefix}-{year % 100:00}-{sequence:00000}";

    /// <summary>Whether the text looks like a task or project number (any case, surrounding spaces ignored).</summary>
    public static bool IsNumber(string? text) => text is not null && NumberPattern().IsMatch(text.Trim());

    public static string Normalise(string number) => number.Trim().ToUpperInvariant();

    public async Task<Guid?> FindTaskIdAsync(string number, CancellationToken ct = default)
    {
        var n = Normalise(number);
        return await db.Tasks.AsNoTracking().Where(t => t.Number == n).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<Guid?> FindProjectIdAsync(string number, CancellationToken ct = default)
    {
        var n = Normalise(number);
        return await db.Projects.AsNoTracking().Where(p => p.Number == n).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    [GeneratedRegex("^[TP]-[0-9]{2}-[0-9]{5,}$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberPattern();
}
