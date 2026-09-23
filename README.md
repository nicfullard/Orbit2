# Orbit

Task and project tracker for the whole business: departments with server-side boundaries, company-wide
sprints, recurring tasks, time tracking, reports, and an MCP server so Claude can create and query work.
Built on ASP.NET Core Razor Pages (.NET 10), EF Core + PostgreSQL, ASP.NET Core Identity, the
`ModelContextProtocol` .NET SDK, Quartz.NET, Ical.Net and QuestPDF. The full specification is in `orbit-spec.md`.

## Solution layout

`Orbit.slnx` holds three projects, side by side at the root:

| Project | What it is |
|---|---|
| `Orbit.Web/` | The web app: Razor Pages UI, MCP server, background jobs, and the server side of the Orbit Agent. Builds `Orbit.Web.dll`. |
| `Orbit.Agent/` | The **Orbit Agent** - a separate Worker Service that runs inside the corporate network. Published on its own; not part of the web app's output. |
| `Orbit.Agents.Contracts/` | Messages and method names shared by the web app and the agent, so the two can't drift. No dependencies. |
| `Orbit.Tests/` | xunit tests for the pure scheduling code: the working-day calendar, the critical path engine and the staleness fingerprint (spec §6.17). |

Plus `deploy/` (systemd units for Orbit and the agent, env file template, least-privilege DB role script,
deployment notes), `orbit-spec.md` and `dotnet-tools.json` (pins `dotnet-ef`).

Inside `Orbit.Web/`, folders stand in for the layers of spec §11, and namespaces follow them (`Orbit.Data`,
`Orbit.Application`, ... - not `Orbit.Web.*`):

| Folder | Contents |
|---|---|
| `Data/` | `ApplicationDbContext`, entities, `DbInitializer` (migrations + seed) |
| `Application/` | Services (`TaskService`, `ProjectService`, `SprintService`, ...), `Actor` + `AccessPolicy` (the §6.5 rules), models |
| `Auth/` | API-key and Orbit Agent authentication schemes, `OrbitSignInManager` (directory sign-in), claims factory, actor resolution, page filters |
| `Agents/` | Server side of the Orbit Agent: the SignalR hub agents connect to, the registry of connected agents, the register/de-register endpoints |
| `Mcp/` | `OrbitTools` - the 19 MCP tools, mapped onto the same services the UI uses |
| `Jobs/` | Quartz.NET jobs: recurring-task generation and due-date notifications, cron-scheduled from `Jobs:*` |
| `Reporting/` | QuestPDF report rendering |
| `Pages/` | Razor Pages UI (dashboard, tasks, projects, backlog, sprints, recurring, time, admin, reports) |
| `Areas/Identity/` | Overrides of the default Identity UI (login, self-registration disabled, no self-delete) |
| `Migrations/` | EF Core migrations |

```
dotnet build Orbit.slnx                              # all three projects
dotnet run --project Orbit.Web                       # the web app
dotnet test Orbit.slnx                               # the unit tests
dotnet ef migrations add <Name> --project Orbit.Web  # from the solution root (dotnet tool restore first)
```

## First run (development)

1. `Orbit.Web/appsettings.Development.json` (gitignored) points at the local Postgres `orbit` database and seeds a
   first System Admin (`admin@orbit.local`). Change the seed values if you like.
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
| Take an unassigned task (assign it to yourself) | own department | own department | any |
| Attach files to tasks / projects (spec §6.18) | own department's tasks / projects | own department | any |
| Delete an attachment | own uploads, or on tasks/projects they can edit | any in department | any |
| Close tasks (Done/Cancelled) | no | own department | any |
| File a task under another department's project (spec §6.2.1) | no | no | yes |
| Set a task's parent; add/remove its dependencies (spec §6.15) | tasks they can edit | own department | any |
| Run a project's critical path analysis (spec §6.17) | projects they own | own department | any |
| Working calendar - working week and public holidays (spec §6.17) | no | no | yes |
| Sprints (create/start/complete) | no | no | yes |
| Users, departments, API keys, reports | no | no | yes |
| Directory (LDAP) settings, Orbit Agents, users' sign-in method | no | no | yes |

The same rules are enforced in `AccessPolicy` for signed-in users and for API keys.

A project is owned by one department, but a System Admin can file tasks under it for other departments
(unassigned, or assigned to someone in that department). Each such task belongs to its own department, which
sees and works it as usual; a department with tasks on another department's project sees that project read-only.

## Directory sign-in (LDAP / Active Directory)

Each user signs in with either a **local password** or their **company directory password** - a per-user setting under
**Admin > Users** (spec §6.13). Accounts are still created in Orbit first; a directory account alone grants nothing.

The directory sits behind the corporate firewall, so Orbit never talks to it. Instead an **Orbit Agent** - a small
service you run inside your network - connects *out* to Orbit over HTTPS and checks passwords on Orbit's behalf. No
inbound firewall ports. The agent has practically no settings; you register it like a GitHub Actions runner:

1. **Admin > Agents > New agent** gives you a one-time command:
   `Orbit.Agent configure --url https://<orbit> --token orbitreg_...`
2. Run it on the agent's machine, then `Orbit.Agent run` (or install it as a service). It shows as **Online**.
3. **Admin > Directory**: server, service account, search base, filter. **Test connection** runs through the agent
   against whatever is in the form, before you save or enable anything.
4. Set users' **Sign-in method** to *Directory (LDAP)*.

Directory settings live in Orbit (the bind password encrypted) and are sent with each request, so nothing is ever
configured on the agent. A directory or agent outage shows "temporarily unavailable" and never locks anyone out. At
least one System Admin must keep a local password - enforced - so Orbit stays administrable when the directory is
down. Publishing and installing the agent: `deploy/README.md` section 7.

**Set the lockout against your AD policy before going live.** Each wrong password typed into Orbit is a real failed
bind in AD and counts towards AD's own lockout, so Orbit locks first: 3 attempts / 30 minutes by default
(`Security:Lockout:*`), which must stay *below* AD's threshold or Orbit becomes a way to lock people's Windows accounts
from the internet. Failed sign-ins are also limited per client address (`Security:LoginThrottle:*`), which needs your
reverse proxy to send `X-Forwarded-For`. A System Admin can **Unlock** a user from their page. Directory sign-in is
password-only - it bypasses any MFA on AD - and Orbit's own 2FA is opt-in. Details: `deploy/README.md` section 8,
spec §8.3.

```
dotnet run --project Orbit.Agent -- help
```

## Claude / MCP

- Endpoint: `https://<host>/mcp` (Streamable HTTP, stateless)
- Auth: `Authorization: Bearer <api key>` - keys are issued under **Admin > API Keys** with a role and
  department, and are shown once.
- Tools: `create_task`, `get_task`, `list_tasks`, `update_task`, `add_comment`, `list_comments`, `get_attachment`,
  `add_dependency`, `remove_dependency`, `create_project`, `get_project`, `get_project_status`,
  `list_projects`, `update_project`, `list_activity`, `list_users`, `list_departments`, `get_critical_path`, `run_critical_path_analysis`.
- Tasks can be subtasks (`parentTaskId`) and can depend on each other (`add_dependency`: FS, SS, FF or SF
  plus a lag in days, spec §6.15). Links gate status changes - the successor can't start / finish until the
  predecessor has - and a parent can't close while a subtask is open; `get_task` reports what a task is
  waiting on, and `get_project` returns the whole dependency list.
- Files attached to tasks and projects (spec §6.18) are listed by `get_task` / `get_project` and read with
  `get_attachment` - text as text, images as images, anything else as base64 - up to `Attachments:MaxMcpFileSizeMb`
  (default 5 MB); uploading is web-UI only.

Every API write is stamped `Source = Api`, attributed to the `Claude` user and written to the audit log.
`create_task` accepts an `idempotencyKey` so retries do not create duplicates.
- `get_critical_path` returns the project's stored critical path analysis (spec §6.17) - planned completion against the target, project buffer status, the critical paths and every task's float - with `isStale` when the schedule has changed since; `run_critical_path_analysis` runs a fresh one. Claude should read these rather than work criticality out from raw tasks.

## Critical path analysis

A deliberate action, never automatic (spec §6.17): **Run critical path analysis** on a project page or its Gantt validates the plan, finds the critical and near-critical tasks and their float in *working days*, and compares the planned completion with the project's target date and its **required project buffer** (set on the project form). The Gantt then shows the critical path, the planned completion, the target and the buffer, and says when the schedule has changed since the analysis. The working week and public holidays live under **Admin > Working Calendar**; the thresholds (near-critical days, Amber/Red buffer percentages, hours per working day) are `CriticalPath:*` settings.

## Configuration

See `Orbit.Web/appsettings.json` for defaults and `deploy/orbit.env.example` for the production environment
variables (`Database:ApplyMigrations`, `DataProtection:KeyRingPath`, `Jobs:*`, `Security:*`, `Agents:*`, `CriticalPath:*`, `Seed:Admin:*`, `App:BaseUrl`).
`App:BaseUrl` is also the address put into an agent's `configure` command, so it must be the public `https` URL.
Notification emails go through Identity's `IEmailSender`; the shipped implementation only logs them.
