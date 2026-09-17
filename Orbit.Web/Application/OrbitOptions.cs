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

public sealed class DataProtectionOptions
{
    public const string Section = "DataProtection";
    /// <summary>Directory for the key ring. Leave empty to use the platform default.</summary>
    public string? KeyRingPath { get; set; }
}
