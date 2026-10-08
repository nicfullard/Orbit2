# Orbit

Task and project tracker for the whole business: departments with server-side boundaries, company-wide
sprints, recurring tasks, time tracking, reports, an asset register, request flows, tasks raised from Nagios
monitoring, and an MCP server so Claude can create and query work.
Built on ASP.NET Core Razor Pages (.NET 10), EF Core + PostgreSQL, ASP.NET Core Identity, the
`ModelContextProtocol` .NET SDK, Quartz.NET, Ical.Net and QuestPDF. The full specification is in `orbit-spec.md`.

## Solution layout

`Orbit.slnx` holds five projects, side by side at the root:

| Project | What it is |
|---|---|
| `Orbit.Web/` | The web app: Razor Pages UI, MCP server, background jobs, and the server side of the Orbit Agent. Builds `Orbit.Web.dll`. |
| `Orbit.Agent/` | The **Orbit Agent** - a separate Worker Service that runs inside the corporate network. Published on its own; not part of the web app's output. |
| `Orbit.Agents.Contracts/` | Messages and method names shared by the web app and the agent, so the two can't drift. No dependencies. |
| `Orbit.Scripting/` | The Roslyn script host that runs request actions (spec §6.20), referenced by the web app and the agent so a script compiles the same on both. Brings Npgsql and SqlClient for the scripts' database connections. |
| `Orbit.Tests/` | xunit tests for the pure code: the access rules, permission catalogue, role rules and list scoping (spec §6.5), the working-day calendar, critical path engine and staleness fingerprint (spec §6.17), the asset and request rules (spec §6.19, §6.20), the script host, and the Nagios status parser and incident rules (spec §6.21). |

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
| `Jobs/` | Quartz.NET jobs: recurring-task generation, due-date notifications, the clock sweep, the request action runner and the Nagios check, cron-scheduled from `Jobs:*` |
| `Reporting/` | QuestPDF report rendering |
| `Pages/` | Razor Pages UI (dashboard, tasks, projects, backlog, sprints, recurring, time, assets, requests, admin, reports) |
| `Areas/Identity/` | Overrides of the default Identity UI (login, self-registration disabled, no self-delete) |
| `Migrations/` | EF Core migrations |

```
dotnet build Orbit.slnx                              # all five projects
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
and can be changed like any other role. Scopes: *Own* = tasks assigned to you (alone or with others), created by or
created for you, projects you own, your own time; *Department* = everything in your department; *All* = every department. A grant covers the
scopes below it, and a role with no grants sees nothing.

| Permission | Scopes | What it gates | Member | Dept Admin |
|---|---|---|---|---|
| `tasks.view` | Own / Dept / All | See tasks, comment, attach; sets the dashboard tier and the Department column/filter | Dept | Dept |
| `tasks.create` | Dept / All | Create tasks and recurring definitions; All also files tasks for other departments under a project (spec §6.2.1) | Dept | Dept |
| `tasks.create_for` | Own / Dept / All | Name a task's requestee - the person it is created for (Own: only yourself, so the field isn't offered; Dept: anyone in your department; All: anyone) | Own | Own |
| `tasks.edit` | Own / Dept / All | Edit any field, incl. every status change (close/reopen), parent, dependencies, assignees | Own | Dept |
| `tasks.take` | Dept / All | Take an open task nobody is assigned to for yourself (joining one that has an assignee is an edit) | Dept | Dept |
| `tasks.plan` | Own / Dept / All | Backlog/sprint moves and the Today tick | Dept | Dept |
| `projects.view` | Own / Dept / All | See projects, comment on them, their files and critical path results | Dept | Dept |
| `projects.create` | Dept / All | Create projects | Dept | Dept |
| `projects.edit` | Own / Dept / All | Edit/archive projects, run the critical path analysis; moving to another department needs All | Own | Dept |
| `time.log` | Own / Dept / All | Log, edit, delete time (Own: yours on tasks you are an assignee of; Dept: for anyone in the department) | Own | Dept |
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
| `requests.submit` | Own / Dept | Log requests through any department's request flows, follow the ones you logged or were addressed in, and act on the steps addressed to you (Dept: *Your requests* also follows the ones anyone in your department logged) | Own | Own |
| `requests.configure` | Dept / All | The department's request categories and flows: steps, fields, approvers, dependencies, bindings | - | Dept |
| `requests.manage` | Dept / All | See every request filed with the department, act on any step, cancel, retry or skip a failed step | - | Dept |
| `actions.create` | All | Admin > Actions: the scripts request flows run, and where each runs (C# running as the server or an agent, so granted sparingly) | - | - |
| `nagios.manage` | All | Admin > Nagios: the Nagios instances Orbit watches, their sign-ins, what raises a task and where it goes | - | - |

The navbar follows the same grants: Today and My Tasks need **Edit tasks**, Tasks **View tasks**, Projects **Edit
projects**, Backlog and Sprints **Plan tasks**, Recurring **Create tasks**, My Time **Log time** and Assets **View
assets**, each at any scope. Hiding a link blocks nothing - the pages keep their own rules. The Dashboard is always
there and adapts: someone who can view tasks but not edit them sees their department's (or company's) work read-only,
and someone who can't see tasks gets links to what they can use. Anyone who can log requests also gets a **My waiting
approvals** card there: the requests waiting for their approval, oldest first. **New task** shows only with **Create
tasks** and **New project** only with **Create projects**, and the create pages behind them need the same permission.

A role with any grant at Department scope needs its users and keys to belong to a department. The same rules
are enforced in `AccessPolicy` and `Scoping` for signed-in users and for API keys; grants are read from the
database on every request, so a role edit applies at once. Upgrading an existing database renames the old
fixed roles in place (`SystemAdmin` becomes the built-in System Administrator) and gives Member and Department
Admin the grants above, so nobody's rights change. The asset, request and Create tasks for others permissions arrived
later; the migrations that added them gave their defaults to the roles still named Member and Department Admin.

A task can have **several assignees** (spec §6.2.3): one person, several who share it, or nobody. They are equal -
there is no lead - and each must be an active person in the task's department, or one whose role sees every
department. The task and recurring forms pick them as chips over a search box (click it empty to list the
department's people); the task page has an **Assignees** card to add or remove one without editing anything else. In
task lists a task with one assignee or none keeps the inline dropdown, and a task with several shows their names. The
task is each assignee's own: any of them can edit it, change its assignees, log their own time on it and is asked
about their own time before setting it Done. Only the people a save adds are checked, so a colleague who has since
been deactivated stays on the task until someone removes them; moving a task to another department re-checks everyone
on it. **Take** is for a task with nobody assigned. Wherever tasks are counted by assignee - the reports, the
dashboard, the Today page - a shared task is under each of its assignees, whole, and once in the total: an estimate
is never split. Each person added is emailed, and the due-soon reminder goes to every assignee.

A task can be created on someone else's behalf (spec §6.2.2). Besides its **assignees**, who do it, a task may have
a **requestee**, whom it is for; its creator is always the person who actually created it. With **Create tasks for
others** above Own, the task form shows a Requestee field under Assignees: anyone in your department at Department,
anyone at All (which badges the role company-wide). The requestee is emailed and the task counts as their own, so
they can open, edit and plan it as its creator can, wherever it is filed. The edit form changes or clears the
requestee, if your reach covers both the old and the new person. The Tasks list's **Requestee** filter (*Me* first)
also finds your tasks that other departments hold. The shipped roles hold the permission at Own, so an admin gives
Department or All to the roles that need it (a helpdesk, a PA, a team lead).

Setting a task assigned to you to **Done** in the web UI (the status control on the task page, a task list or the
sprint board, or the edit form) asks first if you haven't logged any time on it and have no clock running on it
(spec §6.10); Cancel leaves the status as it was. On a shared task each assignee is asked about their own time. It's a
reminder, not a rule: nobody else is asked, and neither is MCP's `update_task`.

The task clock (**Start Clock**) runs per task: each task page has its own, and leaving a page stops only that page's
clock (spec §6.10). On a task page, subtasks and linked tasks (Waiting on, Blocks) open in a new tab, so you can run
a clock on the task and on a subtask at the same time; overlapping time is logged on both. The page checks in every
minute; a clock whose page stops checking in (a crashed tab, a computer asleep) is stopped and logged up to its last
check-in.

A project is owned by one department, but someone whose role may create tasks in every department can file
tasks under it for other departments (unassigned, or assigned to someone in that department). Each such task
belongs to its own department, which sees and works it as usual; a department with tasks on another
department's project sees that project read-only, apart from its comment thread.

A project has its own **Comments** card, like a task's: anyone who can see the project reads the thread and
posts to it, the departments that only have tasks on it included, and an archived project still takes comments.

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
  `list_assets`, `get_asset`, `create_asset`, `update_asset`, `record_asset_check`, `create_assets`, `update_assets`,
  `record_asset_checks`, `list_asset_types`, `list_asset_locations`, `get_asset_type`, `create_asset_type`, `update_asset_type`,
  `get_asset_location`, `create_asset_location`, `update_asset_location`.
- Comments: `add_comment` / `list_comments` take exactly one of `taskId`, `projectId` or `assetId`; `get_task`
  includes the thread, `get_project` and `get_asset` give a `commentCount`.
- Assets (spec §6.19): any `assetId` argument takes the GUID or, when the asset has one, its ERP asset number;
  `create_asset` takes an optional `idempotencyKey` so a retry can't register an asset twice; `create_asset` / `update_asset` take
  the type and location by id or name and property values by name. Types and locations are created and changed with `create_/update_asset_type` and
  `create_/update_asset_location` (Configure assets permission); a type's properties can be added and changed over MCP,
  all or nothing per call, but deleting one (which deletes its values) is web-UI only.
- Batches: `create_assets`, `update_assets` and `record_asset_checks` take up to 100 items per call, each with the
  single tool's arguments, so loading a spreadsheet of assets or a stock-take is one call instead of hundreds. Each item
  is saved or refused on its own, and the reply lists every item by index with `ok` and the asset's summary or the error,
  so only the refused items need sending again. Give each `create_assets` item its own `idempotencyKey` so the whole call
  can be retried safely; checks have no key, so resend only the checks that weren't recorded.
- A task can be about one asset: `create_task` / `update_task` take `assetId` (GUID or ERP asset number; `"none"`
  unlinks it on `update_task`), `list_tasks` filters on it, task results carry `assetId` and `asset`, and `get_asset`
  returns the asset's task history (`tasks`, with `tasksVisible` / `tasksNotVisible`). The key can link only an asset
  it can see that isn't disposed.
- A task has any number of assignees (spec §6.2.3): `create_task` / `update_task` take `assigneeIds`, an array of
  user ids from `list_users`. On `update_task` it is the complete new set - `[]` unassigns everyone, and leaving it out
  keeps the assignees as they are. Task results list `assignees` as `{ id, name, active }`; `list_tasks` filters on one
  `assigneeId` (the tasks that person is on) or on `unassigned`. **This replaced the single `assigneeId`** on
  `create_task` / `update_task` and the `assigneeId` / `assignee` fields in results. A caller that still passes
  `assigneeId` to those two tools gets no error: the argument is ignored and the task is saved without the assignment,
  so update any saved prompt, routine or script that names it.
- A task can be created on someone's behalf (spec §6.2.2): `create_task` / `update_task` take `requesteeId` (a user
  id from `list_users`; `"none"` clears it on `update_task`), `list_tasks` filters on it, and task results carry
  `requesteeId` and `requestee` beside the assignees. The key's **Create tasks for others** scope decides whom it may
  name; a key on the shipped grants (Own) can name nobody.
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
Holders and the location can be changed straight from the asset's page, without opening the Edit form.
The ERP asset number and the serial number, when given, can't be reused: a serial number already on an asset that
isn't disposed is refused, ignoring case, so an item can be re-registered once its predecessor has been disposed of.
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

## Requests

**Requests** (spec §6.20, navbar link on **Log requests**) is where anyone asks a department for something. Pick a
tile (*Report a problem*, *Products*...), then a flow. A **flow** is a sequence of **steps** the department built:

- **Form** - questions, one at a time: text, number, date, a choice, how urgent it is (Low to Critical, each with
  what it means), files, an asset type, or an asset, project or person picked from a list the builder scoped (the
  department's, your own department's, the whole company's, or the assets you hold). An asset question that offers
  ten or fewer shows them all as buttons, each with its type and location, so you don't need to know what yours is
  called; with more, you search by name, number or serial.
- **Approval** - ordered stages of approvers (a person, or everyone with a role in a department or in the requester's
  own), any one or all of them; a decline ends it, and each decision may carry a comment.
- **Task** - creates an Orbit task in a department, with its type, priority (fixed, or from an urgency answer), title
  and description filled from earlier answers, due date (none, the day the task is created, or a date answer),
  assignees set in advance or none, and the forms' files copied to it; done when the task is.
- **Action** - runs a script from the action library with the request's values.
- **Web page** - opens a page in a new tab; done when you open it.

Each step waits for the steps the builder chose (an approval's accept or decline can send the request down different
steps) and may use their values as tokens - `{{details.description}}` - in a task's title, a page's address or an
action's inputs. Logging a request fills in the first form and creates a numbered request (`R-26-00007`) filed with the
department; its page shows the steps as a timeline, what each is waiting for, and what you can do: fill in a form,
open a page, approve or decline. The request is complete when every step is. **Needs your action** on the Requests
page lists the forms, pages and approvals waiting for you (the Dashboard's **My waiting approvals** card repeats the
approvals); **Your requests** what you logged or were addressed in (at Department scope, your whole department's);
**Department requests** (needs **Manage requests**) everything filed with your department, where a manager can also
cancel a request or retry and skip a step that failed. The tasks a flow creates carry the source *Request* and link
back to their request; the Tasks list and `list_tasks` filter on the source.

Each department builds its own flows (**Admin > Request flows**, needs **Configure request flows**): categories with an
icon and colour, flows, and each flow's steps with their fields, stages, dependencies and settings, with a *Not set
up* badge until a flow can be offered. A flow with requests in progress keeps its structure until they finish, and
nothing a request ever used can be deleted - archive it instead.

**Actions** (**Admin > Actions**, needs **Create actions**, which no shipped role has) are C# scripts (Roslyn) with
parameters that a flow's Action step binds to values. A script sees the request (`Request.Number`, the requester),
every value (`Values["step.field"]`), its inputs (`Inputs["key"]`), named database connections
(`Connections.Open("erp")` - Npgsql or SqlClient, from `Actions:Connections:*` on the server plus `orbit` for
Orbit's own database, or from the agent's `actions.json`), an `HttpClient`, `Log(...)` and a cancellation token.
It is compiled when saved. An action runs either on the Orbit server or on an **Orbit Agent** inside the network
(agent 1.2 or later), so a script can update an internal database without that database being reachable from Orbit.
Scripts run unsandboxed as the server or the agent's service account - that is what the permission guards - and the
audit log records a script's hash, never its text. A failed action (an exception, a compile error, the 120-second
limit) leaves the step for a manager to retry or skip.

## Nagios monitoring

Orbit watches **Nagios Core** instances and raises a task for each host or service that stays down longer than the
instance allows (spec §6.21). The instances are inside your network, so an **Orbit Agent** (1.3 or later) reads them
for Orbit; nothing is installed on the Nagios server and Orbit only ever *reads* it.

Under **Admin > Nagios** (needs **Manage Nagios monitoring**, which no shipped role has) an instance has:

- **Where it is and how to sign in**: the address you open Nagios with, as reachable from the agent's machine
  (`http://nagios.example/nagios/`), a username and password (stored encrypted, never shown again), and the one agent
  that can reach it. Give the Nagios user sight of every host and service and no command rights.
- **What raises a task**: the states (host *Down*, *Unreachable*; service *Critical*, *Warning*, *Unknown* - Down and
  Critical are ticked on a new instance), how many minutes a host and a service must have been in one, whether
  scheduled downtime and acknowledged problems are skipped (both on by default), how often to check, and the most
  new tasks one check may raise.
- **Where the tasks go**: a department, a priority and the people each task is assigned to.

**Test connection** reads Nagios through the agent with what is on the form, saved or not, and lists every problem
Nagios reports with what a check would do about it. Use it before switching an instance on: problems already past
their threshold are raised at the first check.

**One down event raises one task.** Each host or service has at most one open *incident* (a unique index), and an
incident raises a task at most once, however many checks follow and whether the task is still open or not:

- Only a state Nagios has finished rechecking counts (a *hard* state), measured on Nagios' own clock.
- A service is never raised while its host is down - the host is - and gets a full threshold again once the host
  is back, so a host outage doesn't end in a burst of service tasks. The same after a downtime or an acknowledgement.
- If it goes down again while the earlier task is **still open**, that task gets a note instead of a second task.
- When Nagios reports it well again Orbit **adds a note and leaves the task open** for someone to close. A host or
  service Nagios no longer lists gets a note saying so.
- A check that can't be trusted - the agent offline, a wrong password, Nagios not updating its status - changes
  nothing and shows why on the instance.

The raised tasks carry the source *Nagios* (the Tasks list and `list_tasks` filter on it), are created by *System*,
and show the problem as Nagios last reported it, with a link to Nagios, to whoever can see the task. That includes
what the check says *now*: if its text changes while the problem lasts (another order joins the list), nothing new is
raised and no note is added, but the task page shows the latest text and when it was read. The task's description keeps
what Nagios said when the task was raised.

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

A task with several assignees (spec §6.2.3) is under each of them in the reports grouped by assignee - closed count,
the two mean times, estimate accuracy and the project report's people - and counts once in the total, so the rows can
add up to more than it.

And one about projects:

- **Project status** - every project that was open at some point in the period or had work on it (tasks created or
  completed, time logged), closed ones included. Each row gives the status at the end of the period and any changes
  during it (read from the audit trail), progress, open, overdue and blocked tasks, tasks created and done in the
  period, time logged in the period and to date, estimated against actual time, and the target date with the project
  buffer from the latest critical path analysis. Expand a project for estimated and logged time by person and the tasks
  that are open or were worked on. A department filter picks the projects the department owns, with the whole
  project's figures. The PDF is landscape. The report's project filter also lists archived projects.

And one about assets:

- **Asset status** - every asset in the register during the period (registered before its end and not disposed of
  before its start), disposed ones included, by type and by location: expand a type for its locations, or a location
  for its types. Each row gives the assets in each status at the end of the period (read from the audit trail; a
  disposal counts from its disposal date), those registered in the period, the value at cost held and disposed of, how
  many were checked in the period with checks overdue and last checks not OK at its end, and the linked tasks open now,
  created and done in the period with the time logged on them. Below, the assets with linked tasks, each expanding to
  its tasks. A department filter picks the assets the department manages; there is no project filter. The PDF is
  landscape.

## Configuration

See `Orbit.Web/appsettings.json` for defaults and `deploy/orbit.env.example` for the production environment
variables (`Database:ApplyMigrations`, `DataProtection:KeyRingPath`, `Jobs:*`, `Security:*`, `Agents:*`, `CriticalPath:*`, `Assets:*`, `Attachments:*`, `Actions:*`, `Nagios:*`, `Seed:Admin:*`, `App:BaseUrl`).
`Jobs:Nagios:*` schedules the look for Nagios instances that are due (every minute; each instance has its own
interval), `Nagios:QueryTimeoutSeconds` caps an agent's read of one instance and `Nagios:StaleAfterSeconds` is how
old Nagios' own status data may be before a check is discarded.
`Actions:TimeoutSeconds` caps a request action's script, and `Actions:Connections:<name>:Provider` / `ConnectionString`
name the databases a script on the server may open; an agent's connections go in its own `actions.json` (deploy/README.md).
`App:BaseUrl` is also the address put into an agent's `configure` command, so it must be the public `https` URL.
Notification emails go through Identity's `IEmailSender`; the shipped implementation only logs them.
