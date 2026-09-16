# Orbit

Task and project tracker for the whole business: departments with server-side boundaries, company-wide
sprints, recurring tasks, time tracking, reports, and an MCP server so Claude can create and query work.
Built on ASP.NET Core Razor Pages (.NET 10), EF Core + PostgreSQL, ASP.NET Core Identity, the
`ModelContextProtocol` .NET SDK, Quartz.NET, Ical.Net and QuestPDF. The full specification is in `orbit-spec.md`.

## Project layout

| Folder | Contents |
|---|---|
| `Data/` | `ApplicationDbContext`, entities, `DbInitializer` (migrations + seed) |
| `Application/` | Services (`TaskService`, `ProjectService`, `SprintService`, ...), `Actor` + `AccessPolicy` (the §6.5 rules), models |
| `Auth/` | API-key authentication scheme, claims factory, actor resolution, page exception filter |
| `Mcp/` | `OrbitTools` - the 14 MCP tools, mapped onto the same services the UI uses |
| `Jobs/` | Quartz.NET jobs: recurring-task generation and due-date notifications, cron-scheduled from `Jobs:*` |
| `Reporting/` | QuestPDF report rendering |
| `Pages/` | Razor Pages UI (dashboard, tasks, projects, backlog, sprints, recurring, time, admin, reports) |
| `Areas/Identity/` | Overrides of the default Identity UI (self-registration disabled, no self-delete) |
| `deploy/` | systemd unit, env file template, least-privilege DB role script, deployment notes |

## First run (development)

1. `appsettings.Development.json` points at the local Postgres `orbit` database and seeds a first
   System Admin (`admin@orbit.local`). Change the seed values if you like.
2. Scaffold the initial migration in the Package Manager Console:
   `Add-Migration InitialCreate`
3. Run the app. On startup it applies pending migrations, seeds the three roles and the synthetic
   `Claude` user, and creates the seed admin if no System Admin exists yet.
4. Sign in as the seed admin, then under **Admin** create departments, users and an API key.

Self-registration is disabled: accounts are created under **Admin > Users**.

## Roles

| | Member | Department Admin | System Admin |
|---|---|---|---|
| Create tasks/projects | own department | own department | any |
| Edit tasks | own/assigned | any in department | any |
| Close tasks (Done/Cancelled) | no | own department | any |
| File a task under another department's project (spec §6.2.1) | no | no | yes |
| Sprints (create/start/complete) | no | no | yes |
| Users, departments, API keys, reports | no | no | yes |

The same rules are enforced in `AccessPolicy` for signed-in users and for API keys.

A project is owned by one department, but a System Admin can file tasks under it for other departments
(unassigned, or assigned to someone in that department). Each such task belongs to its own department, which
sees and works it as usual; a department with tasks on another department's project sees that project read-only.

## Claude / MCP

- Endpoint: `https://<host>/mcp` (Streamable HTTP, stateless)
- Auth: `Authorization: Bearer <api key>` - keys are issued under **Admin > API Keys** with a role and
  department, and are shown once.
- Tools: `create_task`, `get_task`, `list_tasks`, `update_task`, `add_comment`, `list_comments`,
  `create_project`, `get_project`, `get_project_status`, `list_projects`, `update_project`,
  `list_activity`, `list_users`, `list_departments`.

Every API write is stamped `Source = Api`, attributed to the `Claude` user and written to the audit log.
`create_task` accepts an `idempotencyKey` so retries do not create duplicates.

## Configuration

See `appsettings.json` for defaults and `deploy/orbit.env.example` for the production environment
variables (`Database:ApplyMigrations`, `DataProtection:KeyRingPath`, `Jobs:*`, `Seed:Admin:*`, `App:BaseUrl`).
Notification emails go through Identity's `IEmailSender`; the shipped implementation only logs them.
