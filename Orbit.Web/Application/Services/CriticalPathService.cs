using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Application.Models;
using Orbit.Application.Scheduling;
using Orbit.Data;
using Orbit.Data.Entities;

namespace Orbit.Application.Services;

/// <summary>
/// Critical path analysis (spec §6.17) as a deliberate project-management action: <see cref="RunAsync"/> analyses the
/// current plan and stores the result; <see cref="GetLatestAsync"/> returns the most recent stored result and whether
/// the schedule has changed since (by comparing the input fingerprint). Nothing here recalculates on its own.
/// </summary>
public sealed class CriticalPathService(
    ApplicationDbContext db,
    IActorProvider actors,
    AuditService audit,
    TaskStructureService structure,
    WorkingCalendarService calendars,
    IOptions<CriticalPathOptions> options)
{
    /// <summary>
    /// Runs the analysis on the project's current plan. A run that plan-readiness blocks (a circular dependency, nothing
    /// scheduled) is returned with its errors and nothing is stored; otherwise the result is stored and audited and
    /// becomes the project's current analysis. Requires edit rights on the project.
    /// </summary>
    public async Task<CriticalPathResult> RunAsync(Guid projectId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var project = await LoadProjectAsync(projectId, actor, ct);
        AccessPolicy.Require(AccessPolicy.CanRunCriticalPath(actor, project), "Only someone who can edit the project may run its critical path analysis.");

        var result = CriticalPathEngine.Analyse(await BuildInputAsync(project, ct));
        if (result.Blocked) return result;

        var s = result.Schedule;
        var row = new CriticalPathAnalysis
        {
            ProjectId = project.Id,
            RunAt = result.RunAt,
            RunById = actor.UserId,
            PlannedCompletionDate = s.PlannedCompletion,
            NetworkCompletionDate = s.NetworkCompletion,
            TargetDateAtRun = s.TargetDate,
            RequiredBufferAtRun = s.RequiredBufferDays,
            BufferConsumedDays = s.BufferConsumedDays,
            BufferRemainingDays = s.BufferRemainingDays,
            BufferConsumptionPercent = s.BufferConsumptionPercent,
            BufferStatus = s.BufferStatus,
            CriticalTaskCount = result.CriticalTaskCount,
            NearCriticalTaskCount = result.NearCriticalTaskCount,
            CriticalPathCount = result.CriticalPaths.Count,
            WarningCount = result.WarningCount,
            InputFingerprint = result.InputFingerprint,
            ResultData = JsonSerializer.Serialize(result, OrbitJson.Options)
        };
        db.CriticalPathAnalyses.Add(row);
        audit.Add(actor, AuditEntity.Project, project.Id, AuditAction.CriticalPathAnalysed, project.DepartmentId, project.Name, new
        {
            analysisId = row.Id,
            plannedCompletion = s.PlannedCompletion,
            targetDate = s.TargetDate,
            bufferStatus = s.BufferStatus,
            bufferRemainingDays = s.BufferRemainingDays,
            criticalTasks = row.CriticalTaskCount,
            nearCriticalTasks = row.NearCriticalTaskCount,
            criticalPaths = row.CriticalPathCount,
            warnings = row.WarningCount
        });
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>The project's most recent stored analysis, or null when none has been run. Requires view rights on the project.</summary>
    public async Task<CriticalPathView?> GetLatestAsync(Guid projectId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        var project = await LoadProjectAsync(projectId, actor, ct);
        return await LatestAsync(project, ct);
    }

    /// <summary>The headline of the latest analysis, or null when none has been run. Requires view rights on the project.</summary>
    public async Task<CriticalPathSummary?> GetSummaryAsync(Guid projectId, CancellationToken ct = default)
    {
        var actor = await actors.GetAsync(ct);
        return await GetSummaryAsync(await LoadProjectAsync(projectId, actor, ct), ct);
    }

    /// <summary>The headline of the latest analysis for a project the caller has already been allowed to see, or null.</summary>
    public async Task<CriticalPathSummary?> GetSummaryAsync(Project project, CancellationToken ct = default)
    {
        var view = await LatestAsync(project, ct);
        if (view is null) return null;
        var a = view.Analysis;
        return new CriticalPathSummary(a.Id, a.RunAt, view.IsStale, a.PlannedCompletionDate, a.TargetDateAtRun, a.BufferStatus,
            a.BufferRemainingDays, a.BufferConsumptionPercent, a.CriticalTaskCount, a.NearCriticalTaskCount, a.WarningCount);
    }

    private async Task<CriticalPathView?> LatestAsync(Project project, CancellationToken ct)
    {
        var row = await db.CriticalPathAnalyses.AsNoTracking()
            .Where(a => a.ProjectId == project.Id)
            .OrderByDescending(a => a.RunAt)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        CriticalPathResult? result;
        try { result = JsonSerializer.Deserialize<CriticalPathResult>(row.ResultData, OrbitJson.Options); }
        catch (JsonException) { result = null; }
        if (result is null) return null;

        // Out of date when any schedule-driving input differs from what the analysis saw (§6.17).
        var input = await BuildInputAsync(project, ct);
        var fingerprint = ScheduleFingerprint.Compute(input.Tasks, input.Links, input.TargetDate, input.RequiredBufferWorkingDays, input.Calendar);
        return new CriticalPathView(row, result, fingerprint != row.InputFingerprint);
    }

    private async Task<CriticalPathInput> BuildInputAsync(Project project, CancellationToken ct)
    {
        var links = await structure.ListForProjectAsync(project.Id, ct);
        var calendar = await calendars.BuildAsync(ct);
        return new CriticalPathInput(project.Id, project.TargetDate, project.RequiredBufferWorkingDays, project.Tasks.ToList(), links,
            calendar, DateOnly.FromDateTime(DateTime.UtcNow), options.Value);
    }

    /// <summary>The project with its tasks, under the same visibility rule as <see cref="ProjectService.GetAsync"/>.</summary>
    private async Task<Project> LoadProjectAsync(Guid projectId, Actor actor, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking()
            .Include(p => p.Department)
            .Include(p => p.Tasks).ThenInclude(t => t.Assignee)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project not found.");
        var shared = actor.DepartmentId is Guid d && project.Tasks.Any(t => t.DepartmentId == d);
        AccessPolicy.Require(AccessPolicy.CanViewProject(actor, project, shared), "This project belongs to another department.");
        return project;
    }
}
