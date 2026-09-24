using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using Orbit.Agents;
using Orbit.Agents.Contracts;
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
builder.Services.Configure<AgentOptions>(config.GetSection(AgentOptions.Section));
builder.Services.Configure<SecurityOptions>(config.GetSection(SecurityOptions.Section));
builder.Services.Configure<CriticalPathOptions>(config.GetSection(CriticalPathOptions.Section));
builder.Services.Configure<AttachmentOptions>(config.GetSection(AttachmentOptions.Section));
builder.Services.Configure<AssetOptions>(config.GetSection(AssetOptions.Section));
var security = config.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();

// Behind nginx/Caddy on the same host (deploy/README.md) the app only ever sees 127.0.0.1 over plain http.
// Honour X-Forwarded-For/Proto - from loopback proxies only, the default - so the request scheme and the client
// address are the real ones. The address matters twice: it is shown for each agent, and the sign-in throttle counts by it.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

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
        // Stricter than Identity's 5 attempts / 5 minutes, and deliberately stricter than Active Directory's own policy:
        // for a directory user each wrong guess here is a failed bind there, so Orbit has to lock first (see LockoutSettings).
        options.Lockout.MaxFailedAccessAttempts = Math.Max(1, security.Lockout.MaxFailedAttempts);
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(Math.Max(1, security.Lockout.LockoutMinutes));
    })
    .AddRoles<ApplicationRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddClaimsPrincipalFactory<OrbitClaimsPrincipalFactory>()
    .AddSignInManager<OrbitSignInManager>(); // directory (LDAP) users are checked against AD via an Orbit Agent

// Re-validate the cookie against the security stamp often so role/department changes and deactivation bite quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Identity/Account/Login";
    o.AccessDeniedPath = "/Identity/Account/AccessDenied";
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
});

// --- API keys (MCP / machine callers) and Orbit Agents --------------------------------------
builder.Services.AddAuthentication()
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationDefaults.Scheme, null)
    .AddScheme<AgentAuthenticationOptions, AgentAuthenticationHandler>(AgentAuthenticationDefaults.Scheme, null);

// One page-door policy per permission (spec §6.5): granted at any scope opens the page, the service applies the scope.
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
var authorization = builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.McpApiKey, p => p
        .AddAuthenticationSchemes(ApiKeyAuthenticationDefaults.Scheme)
        .RequireAuthenticatedUser())
    .AddPolicy(Policies.Agent, p => p
        .AddAuthenticationSchemes(AgentAuthenticationDefaults.Scheme)
        .RequireClaim(OrbitClaims.AgentId));
foreach (var permission in PermissionCatalog.All)
    authorization.AddPolicy(Policies.Permission(permission.Key), p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission.Key)));

// --- Razor Pages --------------------------------------------------------------------------
builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AuthorizeFolder("/");
        options.Conventions.AllowAnonymousToPage("/Error");
        options.Conventions.AuthorizeFolder("/Admin/Users", Policies.Permission(Permission.UsersManage));
        options.Conventions.AuthorizeFolder("/Admin/Roles", Policies.Permission(Permission.RolesManage));
        options.Conventions.AuthorizeFolder("/Admin/Departments", Policies.Permission(Permission.DepartmentsManage));
        options.Conventions.AuthorizeFolder("/Admin/ApiKeys", Policies.Permission(Permission.ApiKeysManage));
        options.Conventions.AuthorizeFolder("/Admin/Directory", Policies.Permission(Permission.DirectoryManage));
        options.Conventions.AuthorizeFolder("/Admin/Agents", Policies.Permission(Permission.AgentsManage));
        options.Conventions.AuthorizeFolder("/Admin/Calendar", Policies.Permission(Permission.CalendarManage));
        options.Conventions.AuthorizeFolder("/Admin/Activity", Policies.Permission(Permission.AuditView));
        options.Conventions.AuthorizeFolder("/Reports", Policies.Permission(Permission.ReportsView));
        options.Conventions.AuthorizePage("/Sprints/Create", Policies.Permission(Permission.SprintsManage));
        options.Conventions.AuthorizePage("/Sprints/Edit", Policies.Permission(Permission.SprintsManage));
        // Assets (§6.19): the page door opens on the permission at any scope; the services apply the scope.
        options.Conventions.AuthorizeFolder("/Assets", Policies.Permission(Permission.AssetsView));
        options.Conventions.AuthorizePage("/Assets/Create", Policies.Permission(Permission.AssetsCreate));
        options.Conventions.AuthorizePage("/Assets/Edit", Policies.Permission(Permission.AssetsEdit));
        options.Conventions.AuthorizeFolder("/AssetTypes", Policies.Permission(Permission.AssetsConfigure));
        options.Conventions.AuthorizeFolder("/AssetLocations", Policies.Permission(Permission.AssetsConfigure));
    })
    .AddMvcOptions(o =>
    {
        o.Filters.Add(new OrbitExceptionPageFilter());
        o.Filters.Add(new DirectoryAccountPageFilter());
    });

// --- Application services -----------------------------------------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IActorProvider, HttpActorProvider>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<TaskStructureService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<CommentService>();
builder.Services.AddScoped<NumberingService>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<SprintService>();
builder.Services.AddScoped<RecurrenceService>();
builder.Services.AddScoped<TimeEntryService>();
builder.Services.AddScoped<DepartmentService>();
builder.Services.AddScoped<UserDirectoryService>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddScoped<RoleService>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<LdapSettingsService>();
builder.Services.AddScoped<DirectoryAuthService>();
builder.Services.AddScoped<ReportingService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<WorkingCalendarService>();
builder.Services.AddScoped<CriticalPathService>();
builder.Services.AddScoped<AssetService>();
builder.Services.AddScoped<AssetTypeService>();
builder.Services.AddScoped<AssetLocationService>();
builder.Services.AddTransient<IEmailSender, LoggingEmailSender>();

// --- MCP server (Streamable HTTP, stateless) ----------------------------------------------
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "Orbit", Version = "1.0.0" };
        o.ServerInstructions = "Orbit is the company's task and project tracker. Use list_departments / list_users / list_projects " +
            "to resolve ids before creating or updating tasks. Every write you make is tagged as API-created and audited. " +
            "For a project's critical path, task float and project-buffer status use get_critical_path - Orbit's stored, deterministic analysis - " +
            "rather than deriving criticality from raw tasks; run_critical_path_analysis runs and stores a fresh one. " +
            "Files attached to tasks and projects are listed by get_task / get_project and read with get_attachment. " +
            "The asset register (laptops, vehicles, equipment) is separate from tasks and projects: list_assets / get_asset read it, " +
            "create_asset / update_asset / record_asset_check write it; each department has its own asset types and locations " +
            "(list_asset_types, list_asset_locations), and any assetId argument also accepts the asset's ERP asset number, which not every asset has. " +
            "Pass an idempotencyKey to create_asset so a retry can't register an asset twice.";
    })
    .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<OrbitTools>();

// --- Orbit Agents (on-premises connector, outbound SignalR connection) ----------------------
builder.Services.AddSignalR();
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // One shared window rather than one per client address: registration is a rare, admin-initiated event.
    o.AddFixedWindowLimiter(AgentEndpoints.RegistrationRateLimit, w =>
    {
        w.PermitLimit = 10;
        w.Window = TimeSpan.FromMinutes(1);
        w.QueueLimit = 0;
    });
});

// --- Background jobs (Quartz.NET, cron-scheduled from Jobs:*) ------------------------------
builder.Services.AddOrbitJobs(config);

QuestPDF.Settings.License = LicenseType.Community;

var app = builder.Build();

// --- Pipeline -----------------------------------------------------------------------------
app.UseForwardedHeaders();

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
// Every cookie - session, two-factor, antiforgery, TempData (which carries one-time password-reset links) - is marked
// Secure outside development, whatever scheme the app thinks the request had. Relying on the reverse proxy to forward
// the scheme correctly is one misconfiguration away from session cookies travelling over plain http.
if (!app.Environment.IsDevelopment())
    app.UseCookiePolicy(new CookiePolicyOptions { Secure = CookieSecurePolicy.Always });
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapMcp("/mcp").RequireAuthorization(Policies.McpApiKey);
app.MapHub<AgentHub>(AgentProtocol.HubPath); // authorised by [Authorize(Policy = Policies.Agent)] on the hub
app.MapAgentEndpoints();

await DbInitializer.InitializeAsync(app.Services);

app.Run();
