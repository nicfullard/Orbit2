# Orbit

Task and project tracker for the whole business: departments with server-side boundaries, company-wide
sprints, recurring tasks, time tracking, reports, and an MCP server so Claude can create and query work.
Built on ASP.NET Core Razor Pages (.NET 10), EF Core + PostgreSQL, ASP.NET Core Identity, the
`ModelContextProtocol` .NET SDK, Quartz.NET, Ical.Net and QuestPDF. The full specification is in `orbit-spec.md`.

## Solution layout

`Orbit.slnx` holds four projects, side by side at the root:

| Project | What it is |
|---|---|
| `Orbit.Web/` | The web app: Razor Pages UI, MCP server, background jobs, and the server side of the Orbit Agent. Builds `Orbit.Web.dll`. |
| `Orbit.Agent/` | The **Orbit Agent** - a separate Worker Service that runs inside the corporate network. Published on its own; not part of the web app's output. |
| `Orbit.Agents.Contracts/` | Messages and method names shared by the web app and the agent, so the two can't drift. No dependencies. |
| `Orbit.Tests/` | xunit tests for the pure code: the access rules, permission catalogue, role rules and list scoping (spec §6.5), and the working-day calendar, critical path engine and staleness fingerprint (spec §6.17). |

Plus `deploy/` (systemd units for Orbit and the agent, env file template, least-privilege DB role script,
deployment notes), `orbit-spec.md` and `dotnet-tools.json` (pins `dotnet-ef`).

Inside `Orbit.Web/`, folders stand in for the layers of spec §11, and namespaces follow them (`Orbit.Data`,
`Orbit.Application`, ... - not `Orbit.Web.*`):

| Folder | Contents |
|---|---|
| `Data/` | `ApplicationDbContext`, entities, `DbInitializer` (migrations + seed) |
| `Application/` | Services (`TaskService`, `ProjectService`, `RoleService`, ...), the permission catalogue, `Actor` + `AccessPolicy` + `Scoping` (the §6.5 rules), `RoleRules`, models |
| `Auth/` | API-key and Orbit Agent authentication schemes, `OrbitSignInManager` (directory sign-in), claims factory, actor resolution, page filters |
| `Agents/` | Server side of the Orbit Agent: the SignalR hub agents connect to, the registry of connected agents, the register/de-register endpoints |
| `Mcp/` | `OrbitTools` - the 32 MCP tools, mapped onto the same services the UI uses |
| `Jobs/` | Quartz.NET jobs: recurring-task generation and due-date notifications, cron-scheduled from `Jobs:*` |
| `Reporting/` | QuestPDF report rendering |
| `Pages/` | Razor Pages UI (dashboard, tasks, projects, backlog, sprints, recurring, time, assets, admin, reports) |
| `Areas/Identity/` | Overrides of the default Identity UI (login, self-registration disabled, no self-delete) |
| `Migrations/` | EF Core migrations |

```
dotnet build Orbit.slnx                              # all four projects
dotnet run --project Orbit.Web                       # the web app
dotnet test Orbit.slnx                               # the unit tests
dotnet ef migrations add <Name> --project Orbit.Web  # from the solution root (dotnet tool restore first)
```

## First run (development)

1. `Orbit.Web/appsettings.Development.json` (gitignored) points at the local Postgres `orbit` database and seeds a
   first System Admin (`admin@orbit.local`). Change the seed values if you like.
2. Run the app. On startup it applies the migrations in `Orbit.Web/Migrations/` (creating the schema in an empty
   database), creates the built-in **System Administrator** role and
   the shipped **Member** and **Department Admin** roles (once; later edits to them stick), the synthetic
   `Claude` user, and the seed admin if nobody holds the built-in role yet.
3. Sign in as the seed admin, then under **Admin** create departments, users and an API key, and adjust or add
   roles under **Admin > Roles**.

Self-registration is disabled: accounts are created under **Admin > Users** - one at a time, or many at once with
**Import from directory** (below).

## Permissions and scopes

A user or API key has one **role**; a role is a set of **permissions**, each granted at a **scope** (spec §6.5).
Roles are edited under **Admin > Roles**. The built-in **System Administrator** role holds every permission for
all departments and can't be edited or deleted; **Member** and **Department Admin** ship with the grants below
and can be changed like any other role. Scopes: *Own* = tasks assigned to or created by you, projects you own,
your own time; *Department* = everything in your department; *All* = every department. A grant covers the
scopes below it, and a role with no grants sees nothing.

| Permission | Scopes | What it gates | Member | Dept Admin |
|---|---|---|---|---|
| `tasks.view` | Own / Dept / All | See tasks, comment, attach; sets the dashboard tier and the Department column/filter | Dept | Dept |
| `tasks.create` | Dept / All | Create tasks and recurring definitions; All also files tasks for other departments under a project (spec §6.2.1) | Dept | Dept |
| `tasks.edit` | Own / Dept / All | Edit any field, incl. every status change (close/reopen), parent, dependencies, assignee | Own | Dept |
| `tasks.take` | Dept / All | Take an open, unassigned task for yourself | Dept | Dept |
| `tasks.plan` | Own / Dept / All | Backlog/sprint moves and the Today tick | Dept | Dept |
| `projects.view` | Own / Dept / All | See projects, their files and critical path results | Dept | Dept |
| `projects.create` | Dept / All | Create projects | Dept | Dept |
| `projects.edit` | Own / Dept / All | Edit/archive projects, run the critical path analysis; moving to another department needs All | Own | Dept |
| `time.log` | Own / Dept / All | Log, edit, delete time (Own: yours on tasks assigned to you; Dept: for anyone in the department) | Own | Dept |
| `sprints.manage` | All | Create/start/complete sprints | - | - |
| `reports.view` | Dept / All | Reports (Dept: fixed to your own department) | - | - |
| `audit.view` | Dept / All | Admin > Activity Log and `list_activity` | - | - |
| `users.manage`, `roles.manage`, `api_keys.manage` | All, reserved to the built-in role | Users, roles, API keys | - | - |
| `departments.manage`, `calendar.manage`, `directory.manage`, `agents.manage` | All | Departments, working calendar, directory (LDAP) settings, Orbit Agents | - | - |
| `assets.view` | Own / Dept / All | See assets, comment, attach, link them to tasks (Own: the assets you hold; Dept: the ones your department manages, plus yours) | Own | Dept |
| `assets.create` | Dept / All | Register assets | - | Dept |
| `assets.edit` | Dept / All | Edit assets: status, disposal, type, properties, location, holders; delete one registered in error | - | Dept |
| `assets.check` | Own / Dept / All | Record asset checks (Own: "Confirm I have it" on the assets you hold) | Own | Dept |
| `assets.configure` | Dept / All | The department's asset types (with properties and check intervals) and locations | - | Dept |

A role with any grant at Department scope needs its users and keys to belong to a department. The same rules
are enforced in `AccessPolicy` and `Scoping` for signed-in users and for API keys; grants are read from the
database on every request, so a role edit applies at once. Upgrading an existing database renames the old
fixed roles in place (`SystemAdmin` becomes the built-in System Administrator) and gives Member and Department
Admin the grants above, so nobody's rights change. The asset permissions arrived later; the migration that added
them gave their defaults to the roles still named Member and Department Admin.

A project is owned by one department, but someone whose role may create tasks in every department can file
tasks under it for other departments (unassigned, or assigned to someone in that department). Each such task
belongs to its own department, which sees and works it as usual; a department with tasks on another
department's project sees that project read-only.

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
4. Set users' **Sign-in method** to *Directory (LDAP)*, or bring many people in at once with **Import from directory**.

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

### Import from directory (take-on)

**Admin > Users > Import from directory** creates Orbit accounts for people already in AD, so a take-on of a hundred
staff isn't a hundred trips through *Create user* (spec §6.13.1). It needs directory sign-in enabled and an agent of
**version 1.1 or later** - older agents keep handling sign-ins but can't list, and the page says so.

1. **Load directory users.** The filter defaults to your sign-in filter with `*` for `{0}` (so it lists the people who
   could sign in); narrow it with `memberOf`, or narrow the search base to one OU. Up to 5,000 people per listing.
2. Each person's AD `department` (or another attribute you name) is matched to an Orbit department by name, ignoring
   case and spacing. **Departments that couldn't be linked** are listed with a head count: pick an Orbit department
   for each, or leave those people without one. Nothing is remembered and no department is created.
3. Tick people (filter the list, select all shown), choose **one role** for them, **Import ticked**. People already in
   Orbit, disabled in AD, without an email or sharing one with someone else can't be ticked, and say why.

Imported users sign in with their directory email and password. If the role needs a department and a ticked person
has none, nothing is imported until you choose one. Each account is audited as imported from the directory. It is a
one-off: nothing is kept in step with AD afterwards.

## Claude / MCP

- Endpoint: `https://<host>/mcp` (Streamable HTTP, stateless)
- Auth: `Authorization: Bearer <api key>` - keys are issued under **Admin > API Keys** with a role and
  department, and are shown once.
- Tools: `create_task`, `get_task`, `list_tasks`, `update_task`, `add_comment`, `list_comments`, `log_time`, `get_attachment`,
  `add_dependency`, `remove_dependency`, `create_project`, `get_project`, `get_project_status`,
  `list_projects`, `update_project`, `list_activity`, `list_users`, `list_departments`, `get_critical_path`, `run_critical_path_analysis`,
  `list_assets`, `get_asset`, `create_asset`, `update_asset`, `record_asset_check`, `list_asset_types`, `list_asset_locations`,
  `get_asset_type`, `create_asset_type`, `update_asset_type`, `get_asset_location`, `create_asset_location`, `update_asset_location`.
- Assets (spec §6.19): any `assetId` argument takes the GUID or, when the asset has one, its ERP asset number;
  `create_asset` takes an optional `idempotencyKey` so a retry can't register an asset twice; `create_asset` / `update_asset` take
  the type and location by id or name and property values by name; `add_comment` / `list_comments` take `assetId`
  in place of `taskId`. Types and locations are created and changed with `create_/update_asset_type` and
  `create_/update_asset_location` (Configure assets permission); a type's properties can be added and changed over MCP,
  all or nothing per call, but deleting one (which deletes its values) is web-UI only.
- A task can be about one asset: `create_task` / `update_task` take `assetId` (GUID or ERP asset number; `"none"`
  unlinks it on `update_task`), `list_tasks` filters on it, task results carry `assetId` and `asset`, and `get_asset`
  returns the asset's task history (`tasks`, with `tasksVisible` / `tasksNotVisible`). The key can link only an asset
  it can see that isn't disposed.
- Tasks can be subtasks (`parentTaskId`) and can depend on each other (`add_dependency`: FS, SS, FF or SF
  plus a lag in days, spec §6.15). Links gate status changes - the successor can't start / finish until the
  predecessor has - and a parent can't close while a subtask is open; `get_task` reports what a task is
  waiting on, and `get_project` returns the whole dependency list.
- Files attached to tasks and projects (spec §6.18) are listed by `get_task` / `get_project` and read with
  `get_attachment` - text as text, images as images, anything else as base64 - up to `Attachments:MaxMcpFileSizeMb`
  (default 5 MB); uploading is web-UI only.
- `log_time` logs time on a task (spec §6.10) for a named person (`userId` from `list_users`, never the Claude user):
  a duration in minutes (1-1440), a date (default today) and a note. The key's role needs **Log time** at Department
  scope for the task's department, or All; an `idempotencyKey` makes a retry return the entry already logged. It
  returns the entry and the task's total logged time against its estimate. Editing and deleting entries is web-UI only.

Every API write is stamped `Source = Api`, attributed to the `Claude` user and written to the audit log.
`create_task` accepts an `idempotencyKey` so retries do not create duplicates.
- `get_critical_path` returns the project's stored critical path analysis (spec §6.17) - planned completion against the target, project buffer status, the critical paths and every task's float - with `isStale` when the schedule has changed since; `run_critical_path_analysis` runs a fresh one. Claude should read these rather than work criticality out from raw tasks.

## Critical path analysis

A deliberate action, never automatic (spec §6.17): **Run critical path analysis** on a project page or its Gantt validates the plan, finds the critical and near-critical tasks and their float in *working days*, and compares the planned completion with the project's target date and its **required project buffer** (set on the project form). The Gantt then shows the critical path, the planned completion, the target and the buffer, and says when the schedule has changed since the analysis. The working week and public holidays live under **Admin > Working Calendar**; the thresholds (near-critical days, Amber/Red buffer percentages, hours per working day) are `CriticalPath:*` settings.

## Assets

The asset register (spec §6.19) - laptops, vehicles, tools, equipment - sits beside tasks and projects. Each
asset is managed by one department, which defines its own **asset types** (each with its own properties and check
interval) and **locations** under **Assets > Asset types / Locations**. An asset is named by its name and may carry
its number in the ERP asset register (optional - not every asset is on the ERP system - and unique ignoring case
when given); it can be held by any number of people in any department, and carries purchase
and warranty details, periodic **checks** (OK / issue found / not found - a check never changes the asset's
status), comments and files. Overdue checks, last checks that weren't OK, expiring warranties and assets still
held by deactivated users are flagged and filterable; disposing of an asset removes its holders.
**Quick check** on the Assets list is for an audit walk with a barcode scanner: scan one serial number (or ERP asset
number) after another, and each records today's OK check on the asset without leaving the scan box.
**Copy** on an asset's page opens the Register form filled from that asset, for a batch of identical items: the ERP
asset number, serial number and holders are left blank, and a copy of a disposed asset starts Active.

**Tasks about an asset.** A task (or a recurring task) can name the asset it is about in its optional **Asset**
field, beside Project. The field is a type-ahead rather than a dropdown, so it copes with any size of register: type
part of a name, ERP number, serial number, make, model, holder or location and pick from the top 20 matches, or scan
the asset's label - an exact serial or ERP number followed by Enter picks it at once. It offers the assets you can
see (**View assets**), never disposed ones. The asset's page then has a **Tasks** card - its task history, open tasks
first - and the Tasks list can be filtered by asset. An asset with linked tasks can't be deleted; dispose of it
instead.

## Reports

**Reports** (spec §12, needs **View reports**; at Department scope it is fixed to your own department) takes a date
range and optional project and department filters, shows each report on screen and exports it to PDF:
*Closed count by person*, *Created count by person*, *Mean time to respond*, *Mean time to resolve*, and two time
reports:

- **Time by person** - the time each person logged in the period, and for the tasks they worked on, each task's
  estimate against all time logged on it to date (cancelled tasks aside). Expand a person to see their tasks, with
  the ones over estimate flagged. A task several people worked on shows under each of them but counts once in the total.
  Active people in the department reported on (the project's department when only a project is chosen, everyone when
  neither is) who logged nothing in the period are listed too, with 0, greyed out at the bottom.
- **Estimate accuracy** - tasks completed in the period, grouped by assignee: estimated against actual time, the
  variance, and how many ran over. Expand a row to see its tasks, largest overrun first.

## Configuration

See `Orbit.Web/appsettings.json` for defaults and `deploy/orbit.env.example` for the production environment
variables (`Database:ApplyMigrations`, `DataProtection:KeyRingPath`, `Jobs:*`, `Security:*`, `Agents:*`, `CriticalPath:*`, `Assets:*`, `Seed:Admin:*`, `App:BaseUrl`).
`App:BaseUrl` is also the address put into an agent's `configure` command, so it must be the public `https` URL.
Notification emails go through Identity's `IEmailSender`; the shipped implementation only logs them.
