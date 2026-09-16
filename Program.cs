using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using Orbit.Application;
using Orbit.Application.Services;
using Orbit.Auth;
using Orbit.Data;
using Orbit.Data.Entities;
using Orbit.Jobs;
using Orbit.Mcp;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// --- Data ---------------------------------------------------------------------------------
var connectionString = config.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.Configure<AppOptions>(config.GetSection(AppOptions.Section));
builder.Services.Configure<JobOptions>(config.GetSection(JobOptions.Section));
builder.Services.Configure<SeedOptions>(config.GetSection(SeedOptions.Section));
builder.Services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.Section));

// Persisted key ring (production): without it every restart signs everyone out. See deploy/README.md.
var keyRingPath = config[$"{Orbit.Application.DataProtectionOptions.Section}:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
        .SetApplicationName("Orbit");
}

// --- Identity (interactive users) ---------------------------------------------------------
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false; // accounts are created by a System Admin
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddClaimsPrincipalFactory<OrbitClaimsPrincipalFactory>();

// Re-validate the cookie against the security stamp often so role/department changes and deactivation bite quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Identity/Account/Login";
    o.AccessDeniedPath = "/Identity/Account/AccessDenied";
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
});

// --- API keys (MCP / machine callers) -----------------------------------------------------
builder.Services.AddAuthentication()
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationDefaults.Scheme, null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.SystemAdmin, p => p.RequireRole(Roles.SystemAdmin))
    .AddPolicy(Policies.McpApiKey, p => p
        .AddAuthenticationSchemes(ApiKeyAuthenticationDefaults.Scheme)
        .RequireAuthenticatedUser());

// --- Razor Pages --------------------------------------------------------------------------
builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AuthorizeFolder("/");
        options.Conventions.AllowAnonymousToPage("/Error");
        options.Conventions.AuthorizeFolder("/Admin", Policies.SystemAdmin);
        options.Conventions.AuthorizeFolder("/Reports", Policies.SystemAdmin);
    })
    .AddMvcOptions(o => o.Filters.Add(new OrbitExceptionPageFilter()));

// --- Application services -----------------------------------------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IActorProvider, HttpActorProvider>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<CommentService>();
builder.Services.AddScoped<SprintService>();
builder.Services.AddScoped<RecurrenceService>();
builder.Services.AddScoped<TimeEntryService>();
builder.Services.AddScoped<DepartmentService>();
builder.Services.AddScoped<UserDirectoryService>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddScoped<ReportingService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddTransient<IEmailSender, LoggingEmailSender>();

// --- MCP server (Streamable HTTP, stateless) ----------------------------------------------
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "Orbit", Version = "1.0.0" };
        o.ServerInstructions = "Orbit is the company's task and project tracker. Use list_departments / list_users / list_projects " +
            "to resolve ids before creating or updating tasks. Every write you make is tagged as API-created and audited.";
    })
    .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<OrbitTools>();

// --- Background jobs (Quartz.NET, cron-scheduled from Jobs:*) ------------------------------
builder.Services.AddOrbitJobs(config);

QuestPDF.Settings.License = LicenseType.Community;

var app = builder.Build();

// --- Pipeline -----------------------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapMcp("/mcp").RequireAuthorization(Policies.McpApiKey);

await DbInitializer.InitializeAsync(app.Services);

app.Run();
