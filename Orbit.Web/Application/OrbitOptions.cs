namespace Orbit.Application;

public sealed class AppOptions
{
    public const string Section = "App";
    /// <summary>Public base URL used in notification emails.</summary>
    public string BaseUrl { get; set; } = "https://localhost:7179";
}

public sealed class JobOptions
{
    public const string Section = "Jobs";
    public RecurringTaskJobOptions RecurringTasks { get; set; } = new();
    public DueDateNotificationJobOptions DueDateNotifications { get; set; } = new();
}

/// <summary>Schedule for the Quartz.NET recurring-task generator.</summary>
public sealed class RecurringTaskJobOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Quartz cron expression (seconds first). Default: every day at 02:00 server time.</summary>
    public string Cron { get; set; } = "0 0 2 * * ?";
    /// <summary>Also run once shortly after the app starts, so a restart never skips a day.</summary>
    public bool RunOnStartup { get; set; } = true;
}

/// <summary>Schedule for the Quartz.NET due-date notification job.</summary>
public sealed class DueDateNotificationJobOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Notify when a task is due within this many days (0 = day-of only).</summary>
    public int LeadDays { get; set; } = 1;
    /// <summary>Quartz cron expression (seconds first). Default: every day at 07:00 server time.</summary>
    public string Cron { get; set; } = "0 0 7 * * ?";
    public bool RunOnStartup { get; set; } = true;
}

public sealed class SeedOptions
{
    public const string Section = "Seed";
    public SeedAdminOptions Admin { get; set; } = new();
}

/// <summary>First-run System Admin bootstrap. Consumed only when no System Admin exists yet.</summary>
public sealed class SeedAdminOptions
{
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Password { get; set; }
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    /// <summary>Apply pending EF migrations on startup.</summary>
    public bool ApplyMigrations { get; set; } = true;
}

/// <summary>On-premises Orbit Agents (spec §8.2).</summary>
public sealed class AgentOptions
{
    public const string Section = "Agents";
    /// <summary>How long Orbit waits for an agent to answer a command (e.g. a directory sign-in) before trying the next agent.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;
    /// <summary>How long a registration token stays redeemable after it is shown to the admin.</summary>
    public int RegistrationTokenLifetimeMinutes { get; set; } = 60;
}

/// <summary>Sign-in hardening (spec §8.3). Orbit is on the internet and, for directory users, is a door onto Active Directory.</summary>
public sealed class SecurityOptions
{
    public const string Section = "Security";
    public LockoutSettings Lockout { get; set; } = new();
    public LoginThrottleSettings LoginThrottle { get; set; } = new();
}

/// <summary>
/// Per-account lockout, for local and directory users alike. For a directory user every wrong guess at Orbit is a real
/// failed bind in Active Directory and counts towards AD's own lockout - so Orbit must lock FIRST, or anyone who knows
/// a colleague's email could lock their Windows account from the internet. A locked Orbit account is refused without
/// the directory being contacted, so AD sees at most <see cref="MaxFailedAttempts"/> bad binds per <see cref="LockoutMinutes"/>.
/// Keep MaxFailedAttempts below AD's lockout threshold and LockoutMinutes at least AD's "reset lockout counter after".
/// </summary>
public sealed class LockoutSettings
{
    public int MaxFailedAttempts { get; set; } = 3;
    public int LockoutMinutes { get; set; } = 30;
}

/// <summary>
/// Per-address limit on FAILED sign-ins. Per-account lockout can't see one password being tried across many accounts;
/// this can. Successful sign-ins cost nothing, so an office sharing one address isn't penalised for signing in.
/// </summary>
public sealed class LoginThrottleSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxFailures { get; set; } = 20;
    public int WindowMinutes { get; set; } = 15;
}

public sealed class DataProtectionOptions
{
    public const string Section = "DataProtection";
    /// <summary>Directory for the key ring. Leave empty to use the platform default.</summary>
    public string? KeyRingPath { get; set; }
}

/// <summary>Critical path analysis thresholds (spec §6.17). The working week and calendar exceptions are data, edited under Admin &gt; Working Calendar.</summary>
public sealed class CriticalPathOptions
{
    public const string Section = "CriticalPath";
    /// <summary>A task whose total float is between 1 and this many working days is near-critical.</summary>
    public int NearCriticalThresholdWorkingDays { get; set; } = 5;
    /// <summary>Buffer consumption up to this percentage is Green.</summary>
    public int BufferAmberPercent { get; set; } = 33;
    /// <summary>Buffer consumption above the amber limit and up to this percentage is Amber; beyond it, Red.</summary>
    public int BufferRedPercent { get; set; } = 66;
    /// <summary>Hands-on hours a working day is taken to hold when an estimate is compared with its planning window.</summary>
    public int HoursPerWorkingDay { get; set; } = 8;
}

/// <summary>File attachments on tasks and projects (spec §6.18). The bytes live in the database, so there is nothing to configure but the limits.</summary>
public sealed class AttachmentOptions
{
    public const string Section = "Attachments";
    /// <summary>Largest single file accepted, in megabytes. A file is held in memory while it is saved and served, so keep this modest.</summary>
    public int MaxFileSizeMb { get; set; } = 25;
    /// <summary>How many files one upload may carry.</summary>
    public int MaxFilesPerUpload { get; set; } = 5;
    /// <summary>
    /// Largest attachment the get_attachment MCP tool returns, in megabytes; a bigger file is described but not returned.
    /// The whole file lands in the model's context (a third larger again as base64), so keep this small.
    /// </summary>
    public int MaxMcpFileSizeMb { get; set; } = 5;
    public long MaxFileSizeBytes => (long)MaxFileSizeMb * 1024 * 1024;
    public long MaxMcpFileSizeBytes => (long)MaxMcpFileSizeMb * 1024 * 1024;
    /// <summary>The request body an upload may need: every file at the limit, plus room for the form itself.</summary>
    public long MaxUploadBodyBytes => MaxFilesPerUpload * MaxFileSizeBytes + 1024 * 1024;
}
