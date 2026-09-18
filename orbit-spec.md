# Orbit — Specification

## 1. Purpose

A task/project tracker for the whole business — organized into departments, each managing its own tasks and projects with clear boundaries between them — that:
1. Gives every department a real place to see and manage its tasks and projects.
2. Exposes an API so Claude (or future automations) can create tasks and read status programmatically.

## 2. Goals

- Track **Tasks**, grouped into **Projects**, including **recurring tasks**.
- Organize the business into **Departments**, each managing its own tasks and projects with server-side enforced boundaries between them — a department's data isn't visible to or editable by another department's users (§6.5).
- Support a lightweight agile workflow: a **backlog** of unplanned tasks, organized into **Sprints** during planning — sprints are shared company-wide, cutting across departments (§6.3).
- Provide a Razor Pages web UI for every department's team to view, create, and update tasks/projects, landing on a dashboard summarizing their work after sign-in.
- Provide a documented API that an external AI agent (Claude) can call to create tasks and query status.
- Authenticate human users via ASP.NET Core Identity ("Individual Accounts" — accounts stored in the app's own Postgres database). Each user proves their identity in one of two ways, chosen per user: a **local password** held by Orbit, or their **company directory (LDAP / Active Directory) password** (§6.13).
- Reach a directory that sits behind the corporate firewall **without opening any inbound firewall port**: a small on-premises **Orbit Agent** connects *out* to Orbit and performs the directory check on Orbit's behalf (§6.14, §8.2). The agent carries practically no configuration of its own — it is registered with a one-time command, GitHub-runner style, and everything else is managed in Orbit. The agent is a general connector: checking directory passwords is its first capability, not its last.
- Authenticate API/machine callers (Claude) separately from interactive users.

## 3. Non-Goals (v1)

- No Gantt charts or billing.
- No mobile app — web only, responsive is a nice-to-have not a requirement.
- No multi-tenant support — this is a single organization's internal tool.
- No single sign-on (Kerberos/SAML/OIDC), no automatic account provisioning from the directory, and no directory sync. Directory sign-in (§6.13) checks a password and nothing more: accounts, roles and departments are still managed in Orbit.

## 4. Tech Stack

| Layer | Choice |
|---|---|
| Backend/UI | ASP.NET Core Razor Pages (.NET 8 LTS recommended) |
| Database | PostgreSQL |
| ORM | Entity Framework Core (Npgsql provider) |
| Auth (human users) | ASP.NET Core Identity — Individual Accounts (`IdentityDbContext` in Postgres). Per user: a local password, or the company directory (§6.13) via a custom `SignInManager` |
| Directory sign-in | LDAP / Active Directory, reached through the on-premises **Orbit Agent** (§8.2). LDAP client: `Novell.Directory.Ldap.NETStandard` (cross-platform, managed), in the agent only — Orbit itself never speaks LDAP |
| Orbit Agent | .NET Worker Service (the `Orbit.Agent` project), runs as a Windows service or systemd unit. Transport: ASP.NET Core **SignalR** over an outbound HTTPS/WebSocket connection, using SignalR *client results* for request/response |
| Auth (MCP/Claude) | API key, mapped to a `Role` claim (see §8) |
| Auth (Orbit Agent) | Agent secret (bearer), its own authentication scheme, no role or department (see §8.2) |
| Claude integration | MCP server via the `ModelContextProtocol` .NET SDK, Streamable HTTP transport, hosted in `Orbit.Web` (see §7) |
| Notifications | ASP.NET Core Identity's built-in `IEmailSender` interface |
| Background jobs | Quartz.NET (`Quartz.Extensions.Hosting`), cron-scheduled jobs resolved from DI (see §6.4, §6.7) |
| Reporting/PDF | QuestPDF (see §12) |
| UI theme | Bootstrap 5.3 colour modes (`data-bs-theme` on `<html>`), light and dark, switched by a navbar toggle and remembered per browser in `localStorage` (see §6.11) |

## 5. Domain Model

### 5.1 Entities

**Department**
- `Id` (Guid)
- `Name` (e.g. "IT", "Warehouse Ops")
- `Description` (optional)
- `CreatedAt`

**Project**
- `Id` (Guid)
- `DepartmentId` (FK → Department) — the department that owns this project
- `Name`
- `Description`
- `Status` (`Active`, `OnHold`, `Completed`, `Archived`)
- `OwnerId` (FK → User) — the person accountable for the project
- `CreatedAt`, `UpdatedAt`
- `TargetDate` (nullable) — optional target completion date

**Task**
- `Id` (Guid)
- `DepartmentId` (FK → Department) — the department that owns and works this task. If `ProjectId` is set it defaults to `Project.DepartmentId`, and for a `Member`/`DepartmentAdmin` it must match it (enforced in `TaskService`, not left for the UI to get right); a `SystemAdmin` may set a different department to file a **cross-department project task** (§6.2.1). If the task is standalone, it's chosen directly at creation (defaulting to the creator's own department).
- `ProjectId` (FK → Project, nullable — a task can be standalone, not every task needs a project)
- `Title`
- `Description` (markdown-capable, free text)
- `Status` (`Todo`, `InProgress`, `Blocked`, `Done`, `Cancelled`)
- `Priority` (`Low`, `Medium`, `High`, `Critical`)
- `Type` (`Meeting`, `Planning`, `Task`, `Training`, `Audit`) — what kind of work it is; default `Task`. Editable on the create and edit pages and through the MCP tools
- `AssigneeId` (FK → User, nullable — unassigned allowed)
- `CreatedById` (FK → User, nullable — null when created by the API/Claude)
- `Source` (`Manual`, `Api`) — where the task originated, so the team can see what Claude generated vs what a human typed
- `DueDate` (nullable)
- `PlannedFor` (nullable date) — the day the task is on the team's day plan (§6.12). Set by the "Today" tick; never reset by a job — it simply stops being today. Kept when the task closes, so "done today" stays visible.
- `CreatedAt`, `UpdatedAt`, `CompletedAt` (nullable)
- `FirstRespondedAt` (nullable) — timestamp of the first time the task moves out of `Todo` (e.g. to `InProgress`); needed for the "mean time to respond" report
- `RecurringTaskDefinitionId` (FK, nullable) — set if this task instance was spawned from a recurring definition
- `SprintId` (FK → Sprint, nullable) — null means the task sits in the **backlog**; set once it's planned into a sprint (§6.3)

**Sprint**
- `Id` (Guid)
- `Name` (e.g. "Sprint 14")
- `Goal` (free text, optional — the sprint goal agreed in planning)
- `StartDate`, `EndDate`
- `Status` (`Planned`, `Active`, `Completed`)
- `CreatedAt`

A Sprint has no `ProjectId` **and no `DepartmentId`** — it's a company-wide planning unit that pulls tasks from any department or project (or standalone), not scoped to either. The link runs the other way: `Task.SprintId` (below) is what ties a task to a sprint, and each of those tasks can belong to a different department and project.

**RecurringTaskDefinition**
- `Id` (Guid)
- `ProjectId` (FK → Project, nullable)
- `DepartmentId` (FK → Department) — same rule as `Task.DepartmentId`: defaults to `Project.DepartmentId` when a project is set (a `SystemAdmin` may choose another department, §6.2.1), otherwise chosen directly; generated tasks inherit it
- `Title`, `Description`, `Priority`, `AssigneeId` — template fields copied onto each generated Task
- `RecurrenceRule` — stored as an iCal RRULE string (e.g. `FREQ=WEEKLY;BYDAY=MO`), which is the standard, well-tested way to express "every Monday", "first of the month", etc. without hand-rolling recurrence logic
- `NextRunDate`
- `Active` (bool) — allows pausing a recurring task without deleting its history
- `LeadTimeDays` (int) — how many days before `NextRunDate` the actual Task should be created, so it doesn't appear the same day it's due

**User**
- `Id` (Guid) — the `IdentityUser`'s own Id (ASP.NET Core Identity's standard primary key); the app's domain `User`/profile fields can live directly on a class deriving from `IdentityUser`, so there's no separate join needed
- `DisplayName`, `Email`
- `DepartmentId` (FK → Department, nullable) — required for `Member` and `DepartmentAdmin`; nullable for `SystemAdmin`, who isn't scoped to one department. A `SystemAdmin` may still be given a home department (e.g. Bob in Project Management); it only serves as the default department for the tasks and projects they create and never limits what they can see or do
- `Role` (`SystemAdmin`, `DepartmentAdmin`, `Member`) — managed via ASP.NET Core Identity's built-in roles (`AspNetRoles`/`AspNetUserRoles`). See §6.5 for what each role can do.
- `AuthSource` (`Local`, `Ldap`) — how this user signs in (§6.13). Defaults to `Local`. An `Ldap` user has **no `PasswordHash`**: Orbit holds nothing that could authenticate them, so a stale local password can never stand in for the directory.

**Comment**
- `Id` (Guid)
- `TaskId` (FK → Task)
- `AuthorId` (FK → User, nullable — null if posted by API)
- `Body`
- `CreatedAt`

**TimeEntry**
- `Id` (Guid)
- `TaskId` (FK → Task)
- `UserId` (FK → User) — who logged the time
- `Date` — the day the work was done (may differ from `CreatedAt`, e.g. logging yesterday's work)
- `DurationMinutes`
- `Note` (optional free text)
- `CreatedAt`

**RunningClock** *(the "Start Clock" timer — see §6.10)*
- `Id` (Guid)
- `UserId` (FK → User) — unique: a user has at most one running clock
- `TaskId` (FK → Task)
- `StartedAt` (UTC)
- Deleted when the clock stops; the elapsed time becomes a `TimeEntry`.

**AuditLog** *(recommended for the API surface — see §9)*
- `Id` (Guid)
- `EntityType`, `EntityId` — what was changed
- `Action` (e.g. `Created`, `Updated`, `StatusChanged`)
- `ActorType` (`User`, `Api`)
- `ActorId` — the `User` or `ApiKey` that made the change
- `Timestamp`
- `Details` (jsonb — the changed fields/values)

**ApiKey**
- `Id` (Guid)
- `Name` (label for the integration, e.g. "Claude Project")
- `HashedKey`
- `Role` (`SystemAdmin`, `DepartmentAdmin`, `Member`) — the same three roles as human users, so an API key is bound to the same permission rules as §6.5
- `DepartmentId` (FK → Department, nullable) — required when `Role = DepartmentAdmin` or `Member` (scopes the key to that department, same as a human user); nullable when `Role = SystemAdmin`
- `CreatedAt`
- `RevokedAt` (nullable)

**Agent** *(an on-premises Orbit Agent — see §6.14, §8.2. Not to be confused with the synthetic "Claude Agent" user of §8.)*
- `Id` (Guid)
- `Name` (label chosen by the admin, e.g. "Head office - DC01")
- `Status` (`Pending` — created, waiting for `configure`; `Active`; `Revoked`)
- `RegistrationTokenHash`, `RegistrationExpiresAt` (nullable) — the one-time token, hashed; cleared the moment it is redeemed
- `HashedSecret` (nullable), `SecretPrefix` — the agent's long-lived credential, hashed exactly like an `ApiKey`; the raw value exists only on the agent's machine
- `MachineName`, `OsDescription`, `Version` — reported by the agent, shown to the admin, never used to authorise anything
- `LastConnectedAt`, `LastSeenAt`, `LastIpAddress`
- `CreatedAt`, `CreatedById`, `RegisteredAt`, `RevokedAt`

Whether an agent is *online* is not stored: it is whether Orbit currently holds a live connection from it.

**LdapSettings** *(a single row — the directory sign-in settings, edited under Admin > Directory, §6.13)*
- `Enabled`
- `Server`, `Port` (default 636), `UseSsl` (default true), `ValidateCertificate` (default true)
- `BindDn`, `BindPasswordProtected` — the service account used to look users up; the password is **encrypted at rest** with ASP.NET Core Data Protection (the key ring of §10.2) and is write-only in the UI
- `SearchBase`, `UserFilter` (default `(mail={0})`, where `{0}` is the user's Orbit email)
- `UpdatedAt`, `UpdatedById`

These live in Orbit, not on the agent, so that the agent has nothing to configure.

### 5.2 Relationships

- Department 1—* User
- Department 1—* Project
- Department 1—* Task, Department 1—* RecurringTaskDefinition
- Project 1—* Task
- Project 1—* RecurringTaskDefinition
- Sprint 1—* Task (tasks planned into that sprint, which may span multiple departments and projects; a task with `SprintId = null` is in the backlog)
- RecurringTaskDefinition 1—* Task (generated instances)
- User 1—* Task (as assignee), User 1—* Task (as creator)
- User 1—* Project (as owner)
- Task 1—* Comment
- Task 1—* TimeEntry, User 1—* TimeEntry (as logger)
- User 1—0..1 RunningClock, Task 1—* RunningClock (one per user currently timing that task)

## 6. Functional Requirements — Web UI (Razor Pages)

### 6.1 Projects
- List all projects with status, task counts (open/total), owner. `SystemAdmin` sees every department's projects; `DepartmentAdmin` and `Member` see only their own department's, filtered server-side (not just hidden in the UI) — plus, read-only and marked *shared*, any other department's project that has tasks filed for their department (§6.2.1).
- Create/edit a project, scoped to the creator's own department (a new project's `DepartmentId` defaults to the creator's department for `DepartmentAdmin`/`Member`, or is chosen explicitly by `SystemAdmin`). Editing rights follow the same Member-vs-Admin split as tasks (§6.5): `Member` edits projects they own, `DepartmentAdmin` edits any project in their department, `SystemAdmin` edits any project anywhere. Moving a project to another department (`SystemAdmin`-only) carries along the tasks and recurring definitions that sat in its previous department; cross-department ones (§6.2.1) keep theirs.
- Project detail page: shows its tasks (filterable by status/assignee, and by department when the project spans several), its recurring task definitions, basic progress (e.g. `12/20 tasks done`) and a **By department** breakdown table like the dashboard's (§6.9): one row per department with tasks on the project, giving To do / In progress / Blocked / Done / Overdue counts, open/total and a done-% progress bar, with an "All departments" totals row when the project spans more than one. The project's own department is tagged *Owner* on a cross-department project. Each count links to the page's own task list filtered to that department and status, and the department name filters to just that department.
- Archive a project (soft — doesn't delete tasks).

### 6.2 Tasks
- List/filter tasks by project, status, assignee, priority, due date, **department**, **source** (Manual vs Api — useful for reviewing what Claude proposed), and **planned today** (the day plan, §6.12). `SystemAdmin` sees all departments; `DepartmentAdmin` and `Member` are scoped to their own department by default.
- Create/edit a task, assign to self or team member within the same department (a task can't be assigned to a user outside its own department — see §6.5 assumption flag).
- Change status (ideally a quick inline control, not a full edit form) — see §6.5 for who can close a task.
- **Quick edit from task lists:** on the Tasks list page, the sprint detail page (§6.3) and the project detail page (§6.1), the Status, Assignee and Due columns of the task table are inline controls that save as soon as a value is picked, without opening the task. Status follows the rule above (shown to anyone who can view the task, with only the statuses they may choose). Assignee (a dropdown of the task's department members plus System Admins, or *Unassigned*) and Due date (a date picker; clearing it removes the due date) are shown only on rows the user could edit in full (§6.5: `Member` — tasks they created or are assigned to; `DepartmentAdmin` — any in their department; `SystemAdmin` — any), otherwise the cells are plain text. Each inline change goes through the same validation, audit trail (`Updated` with the changed field) and assignment notification as the edit form; the date control waits for a pause in typing so keyboard entry isn't cut off mid-date. Because a sprint (and a cross-department project) mixes departments, the assignee candidates on those pages are everyone the viewer could assign to anywhere they can see, filtered per row to the task's own department plus System Admins. My Tasks and the Backlog keep the inline status control only. Every one of these tables also carries the "Today" tick box of the day planner (§6.12).
- "My Tasks" view — tasks assigned to the current logged-in user.
- Standalone tasks (no project) supported.
- Task detail page includes a comment thread — team members can discuss a task, and comments posted via the API (e.g. Claude explaining why it proposed a task) are shown with a distinct "Claude" attribution rather than a blank author.

#### 6.2.1 Cross-department project tasks

A project is owned by one department (`Project.DepartmentId`), but the work it needs can come from several. A `SystemAdmin` can therefore file tasks under a project **for departments other than the project's own**. Each such task keeps its own `DepartmentId`, and that department sees and works it like any other of its tasks.

Example: Bob is a `SystemAdmin` whose home department is Project Management. He creates the project "Demo Project" (owned by Project Management) and adds to it a task for IT ("Provision the servers", assigned to Alice in IT), a task for Marketing ("Launch announcement", left unassigned) and a Project Management task of his own. IT's task list and backlog show Alice's task; Marketing's show the unassigned one; each department can open, edit, plan and (its admins) close only its own. Bob sees the whole project.

Rules (all enforced server-side in `TaskService`/`RecurrenceService`, for the UI and the MCP tools alike):
- A task on a project **defaults** to the project's department. When the task's department differs from the project's, it is a cross-department project task.
- **Only a `SystemAdmin` may introduce that pairing** — on create, or by changing an existing task's department or project. A `DepartmentAdmin`/`Member` can only file tasks under projects in their own department, and always in that department.
- Once filed, a cross-department task belongs to its own department: visibility, editing, status changes, closing, sprint planning, comments and time logging all follow the task's `DepartmentId` under the normal §6.5 rules, not the project's. The assignee must belong to the task's department (or be a `SystemAdmin`), as for any task; leaving it unassigned is fine.
- Editing a cross-department task without changing its project or department never moves it — a `Member` in IT saving Alice's task keeps it in IT even though the project is Project Management's.
- **Project visibility follows the tasks:** a department that has tasks filed under another department's project can see that project — in its project list (marked *shared*) and on the project page, read-only, with the project's aggregate progress. Only the project's own department (and `SystemAdmin`) can edit or archive it, or add tasks to it.
- The project page lists every task on the project regardless of department (as the sprint views already do); tasks from another department are shown without a link and without the inline status control, since the per-task rules still apply. It also shows the per-department breakdown table (§6.1) and lets the tasks be filtered by department.
- Recurring task definitions follow the same rule (§6.4): a `SystemAdmin` may file a definition under another department's project, and its generated tasks belong to the definition's department.
- Audit log entries for such a task are recorded against the task's department, so each department's activity feed shows its own work.
- In the task form, choosing a project pre-fills the department with the project's; a `SystemAdmin` can then change it, and the assignee picker narrows to that department's people.

> **Assumption flagged:** cross-department filing is deliberately `SystemAdmin`-only, per the request. A `DepartmentAdmin` cannot add their own department's tasks to another department's project, even once that project is shared with them; and the project's own department cannot open another department's tasks on its project (it sees them listed, with aggregate progress). Both are easy to relax if wanted.

### 6.3 Backlog & Sprints

Sprints are **shared company-wide**, cutting across departments — a single sprint's task list can include tasks from any department. This is why Sprint has no `DepartmentId` (§5.1): it's a level above department scoping, not a peer to it.

- **Backlog view:** all tasks with `SprintId = null` (not yet planned), filterable by project, department, priority, assignee — the pool the team plans from. `DepartmentAdmin`/`Member` see their own department's backlog by default (with the option to widen the filter, since sprint planning is company-wide); `SystemAdmin` sees everything.
- **Sprint list:** all sprints with status (`Planned`/`Active`/`Completed`), dates, and task counts across whichever departments and projects are represented in each sprint.
- **Create/edit a sprint:** name, goal, start/end dates. No project or department field — a sprint is a company-wide planning window, not scoped to either.
- **Sprint planning:** from the backlog view, select one or more tasks — regardless of which department or project they belong to, or none — and assign them into a chosen sprint (moves `SprintId`). Tasks can also be pulled back out of a sprint into the backlog. A user can only plan tasks they're allowed to edit (§6.5) into a sprint — a `DepartmentAdmin`/`Member` can plan their own department's tasks; only `SystemAdmin` can freely plan any department's tasks in.
- **Sprint board:** once a sprint is `Active`, a board view of its tasks grouped by `Status` (Todo / In Progress / Blocked / Done), showing each task's department and project as tags since a sprint's tasks can span several — a simple kanban view scoped to that sprint.
- **Only one `Active` sprint at a time**, company-wide.
- **Sprint management (create/start/complete) is `SystemAdmin`-only**, since a sprint sits above any single department — a `DepartmentAdmin`'s authority stops at their own department's tasks/projects, and a sprint isn't one.
- **Start a sprint:** `Planned → Active`. If another sprint is currently `Active`, starting the new one automatically:
  1. Sets the currently active sprint's status to `Completed`.
  2. Moves any of its still-open tasks (`Status` not `Done`/`Cancelled`) onto the newly activated sprint (`SprintId` updated to point at the new sprint) — they don't fall back to the backlog, they roll straight forward, regardless of which department they belong to.
  3. Sets the new sprint's status to `Active`.
  All three steps happen in one transaction so a sprint is never left partially closed.
- **Complete a sprint manually** (without starting a new one): `Active → Completed`. Any tasks still open at that point move back to the backlog (`SprintId = null`), since there's no "next sprint" to roll them into.

> **Assumption flagged:** I've made sprint management (create/start/complete) `SystemAdmin`-only rather than also giving `DepartmentAdmin` a role in it, on the reasoning that a sprint spans departments so no single department's admin has full authority over it. If you'd rather `DepartmentAdmin`s be able to start/complete sprints too (just not restricted to their own department's tasks when doing so), say so.

### 6.4 Recurring Tasks
- Create/edit a recurring task definition (title, project, department, recurrence rule, assignee, lead time) — same department scoping as tasks: `Member`/`DepartmentAdmin` create within their own department, `SystemAdmin` anywhere. A `SystemAdmin` may also file a definition under another department's project (§6.2.1); its generated tasks belong to the definition's department, not the project's.
- View upcoming generations for a definition (next N occurrences, computed from the RRULE).
- Pause/resume a recurring definition.
- A **Quartz.NET job** (`RecurringTaskGenerationJob`, `[DisallowConcurrentExecution]`) runs on a cron schedule (`Jobs:RecurringTasks:Cron`, default `0 0 2 * * ?` = daily at 02:00; a second trigger fires once shortly after startup when `Jobs:RecurringTasks:RunOnStartup` is true, so a restart never skips a day), finds definitions due within their lead time, and creates the corresponding Task rows, inheriting `DepartmentId` from the definition. Generated tasks land in the backlog (`SprintId = null`) like any other new task. Quartz's misfire handling is set to fire-and-proceed, so a run missed while the app was down executes once on the next start rather than being dropped. The scheduler uses Quartz's in-memory job store; jobs are resolved from a DI scope per execution, so they use the same `RecurrenceService`/`NotificationService` as the rest of the app.

### 6.5 Roles & Access

Three roles, enforced via ASP.NET Core Identity's role-based authorization (`[Authorize(Roles = "...")]` / policy-based checks) plus a department-scoping check on top for `DepartmentAdmin`/`Member`:

| Capability | Member | Department Admin | System Admin |
|---|---|---|---|
| Create tasks/projects (own department) | ✅ | ✅ | ✅ (any department) |
| Edit tasks/projects | ✅ (own/assigned, own department) | ✅ (any, own department) | ✅ (any, any department) |
| Move a task between `Todo` / `InProgress` / `Blocked` | ✅ (own department) | ✅ (own department) | ✅ (any department) |
| Move tasks between backlog and a sprint | ✅ (own department's tasks) | ✅ (own department's tasks) | ✅ (any department's tasks) |
| **File a task under a project owned by another department** (§6.2.1) | ❌ | ❌ | ✅ |
| **Close a task** (set `Done` or `Cancelled`) | ❌ | ✅ (own department) | ✅ (any department) |
| **Create/start/complete a sprint** | ❌ | ❌ | ✅ |
| Manage users (create, promote/demote, deactivate, reset password, unlock) | ❌ | ❌ | ✅ |
| Manage departments (create/edit departments, assign a user's department) | ❌ | ❌ | ✅ |
| Set a user's sign-in method; manage directory (LDAP) settings and Orbit Agents (§6.13, §6.14) | ❌ | ❌ | ✅ |
| View Reports (§12) | ❌ | ❌ | ✅ |

> **Assumption flagged:** I've kept user management and Reports as `SystemAdmin`-only, matching what you asked for `DepartmentAdmin` ("manage tasks and projects for their own departments" — nothing about users or reports). If you actually want `DepartmentAdmin` to manage users within their own department (create members, reset their passwords) or see department-scoped reports, that's a straightforward extension of this table, just say which.

The `Done`/`Cancelled` status options are hidden or disabled in the UI for Members, and the same rule is enforced server-side in `TaskService` (not just hidden client-side) so a direct request can't bypass it. Department scoping is enforced the same way — every task/project query and write is filtered by the caller's `DepartmentId` server-side, not just hidden in the UI. This same rule applies to the Claude API: each API key carries a `Role` and, where relevant, a `DepartmentId` (§5.1, §8), and is bound by exactly the same rules as a human user of that role.

- Sign in via ASP.NET Core Identity's standard Individual Accounts flow (register/login/manage account pages, scaffolded from the default Identity UI or customized as Razor Pages). The one login form serves every user; whether the password typed is checked against Orbit's own hash or against the company directory depends on that user's **sign-in method** (§6.13).
- **Repeated wrong passwords lock the account**, for both sign-in methods: by default **3 failures lock it for 30 minutes** (`Security:Lockout:MaxFailedAttempts` / `LockoutMinutes`). That is deliberately stricter than Identity's stock 5-and-5, and stricter than a typical Active Directory policy, for a reason specific to directory users: each wrong guess at Orbit is a real failed bind in the directory and counts towards the **directory's own** lockout. If Orbit tolerated as many attempts as the directory does, anyone on the internet who knew a colleague's email could lock that person's *Windows* account, again and again. Because a locked Orbit account is refused **without the directory being contacted** (§6.13), the directory sees at most `MaxFailedAttempts` bad binds per `LockoutMinutes` — so the rule is: *MaxFailedAttempts below the directory's lockout threshold, LockoutMinutes at least the directory's counter-reset interval* (§8.3, `deploy/README.md` §8).
- **Unlock:** since the lockout is long on purpose, a `SystemAdmin` can end one early. The Users list flags a locked-out account and the user's page has an **Unlock** button (audited as `Unlocked`). For a directory user this clears Orbit's lock only; a lock in the directory is lifted in the directory.
- **Failed sign-ins are also limited per client address** (default 20 failures per 15 minutes, `Security:LoginThrottle:*`), which is what notices one common password being tried across many accounts — something per-account lockout cannot see. Over the limit, that address gets "Too many failed sign-in attempts" (HTTP 429) and nothing is sent to the directory. Detail and limits in §8.3.
- New accounts default to `Member`, assigned to a department at creation time; a `SystemAdmin` promotes/demotes a user's role and can change their department and sign-in method via a simple admin page.
- **Break-glass rule:** at least one active `SystemAdmin` must always have a **local** password. If every System Admin signed in through the directory, an outage of the directory or its agent would lock out the only people able to fix it. Orbit refuses any change (switching sign-in method, demoting, deactivating) that would leave none. This supersedes the earlier, weaker rule that merely one active `SystemAdmin` must remain.
- **Delete a user account:** `SystemAdmin`-only. Implemented as a soft delete/deactivation (`IsActive = false` or Identity's `LockoutEnd` set far in the future) rather than a hard row delete — a hard delete would orphan the user's `AssigneeId`/`CreatedById`/`AuthorId`/`ActorId` references on existing tasks, comments, and audit log entries. A deactivated user can't sign in, disappears from the assignee picker for new tasks, but their historical task/comment/audit attribution stays intact.

  > **Assumption flagged:** "delete" is implemented as deactivation for the reasons above, not a literal row removal. Flag if you actually want a hard delete (which would mean deciding what happens to that user's existing tasks/comments — reassign, orphan, or block the delete until reassigned).
- **Reset a user's password:** `SystemAdmin`-only, **local users only**. Triggers ASP.NET Core Identity's standard password-reset flow (generates a reset token, either emailed via `IEmailSender` per §6.7 or surfaced as a one-time link/temporary password the admin hands to the user directly) — same mechanism as the existing "Create User" first-login flow below, reused here. A directory user's password is changed and reset in the directory; Orbit rejects the attempt and the user's page says so instead of offering the controls.
- **Self-registration is disabled.** The scaffolded Identity `Register` page/endpoint is removed (or locked behind `[Authorize(Roles = "SystemAdmin")]`) so the public can't create their own accounts. Instead:
  - `SystemAdmin` has a "Create User" page that creates the `AspNetUsers` row directly — setting `DepartmentId`, `Role` and the **sign-in method**. For a local user it also sets a temporary password (or triggers Identity's password-reset/email-confirmation flow) so the new user sets their own password on first login; for a directory user there is no password to set.
  - Login page remains open; only account creation is gated. This holds for directory users too: having an account in the directory does **not** create or grant an Orbit account (§6.13).

### 6.6 Departments
- **Manage departments:** `SystemAdmin`-only — create, rename/edit, and (soft-)archive a department. Archiving a department doesn't cascade-delete its projects/tasks/users; it just stops it from being offered as a choice for new projects/tasks/users going forward.
- **Department list page:** every department with a headline count (users, active projects, open tasks) — a quick org-wide overview for `SystemAdmin`.
- **Department detail page:** the department's users, projects, and open task count, with a way to jump into that department's task/project lists pre-filtered.
- Assigning a user to a department (or moving them between departments) happens from the user's own edit page (§6.5), not a separate department-membership screen — one place to manage a user's role and department together.

### 6.7 Notifications
- Email notifications on task assignment (sent inline when the assignment happens) and on due date approaching (a Quartz.NET job, `DueDateNotificationJob`, runs on `Jobs:DueDateNotifications:Cron`, default daily at 07:00, and notifies assignees of open tasks due within `Jobs:DueDateNotifications:LeadDays` days; each task is notified once per due date).
- Implemented against ASP.NET Core Identity's `IEmailSender` interface, which the app already depends on for account emails (password reset, etc.) — task notification emails reuse the same abstraction.
- **v1 note:** only the default `IEmailSender` interface is wired up for now (i.e. the interface and call sites exist); a concrete sending implementation (SMTP, SendGrid, etc.) is deferred to a later stage. Until a real implementation is plugged in, the default no-op/logging sender can stand in.

### 6.8 Reporting
- A Reports page (`SystemAdmin`-only for now — see §6.5 assumption flag on department-scoped reports for `DepartmentAdmin`) listing the available reports, each with a date-range filter and an optional project/department filter.
- Reports render on-screen (HTML table/chart) and can be exported to PDF via QuestPDF (§12).

### 6.9 Dashboard (post-login landing page)
- Replaces a generic landing page — after sign-in, the user lands on the dashboard for their role instead of a blank home page.

**Member dashboard** — focused on their own work:
- **My Tasks widget:** count and quick list of the current user's open tasks, broken out by status (Todo/In Progress/Blocked), with overdue ones flagged.
- **Active sprint widget:** current sprint's name, goal, and the Member's own tasks within it, if a sprint is active.
- **Projects at a glance:** projects the Member has tasks on (within their own department), with a progress indicator (e.g. `12/20 done`).
- **Due soon:** the Member's own tasks due in the next few days.
- Quick links into "My Tasks," "Backlog," and "All Projects" (no Reports link — Reports stays `SystemAdmin`-only per §6.5).

**Department Admin dashboard** — adds department-wide visibility on top:
- Everything in the Member dashboard, but the widgets default to their department's scope rather than "my" scope (e.g. all open tasks in the department by status, not just the Department Admin's own).
- **Department overview widget:** open task count by assignee within the department.
- **Department activity feed:** last N tasks created/updated/completed within the department, including a visible tag for API-created ones (`Source = Api`).
- **Department projects at a glance:** every active project in the department with its progress indicator, not just ones the Department Admin is personally on.
- Still no Reports link — Reports stays `SystemAdmin`-only (§6.5 assumption flag).

**System Admin dashboard** — company-wide visibility across all departments:
- Everything in the Department Admin dashboard, but scoped company-wide rather than to one department, plus a **by-department breakdown** (open task count and active project count per department, so a System Admin can spot which department is under the most load at a glance).
- **Active sprint widget:** shows the current sprint's overall progress across all departments, with a per-department task-count breakdown, since sprints are shared company-wide (§6.3).
- Quick links additionally include the Reports and Departments (§6.6) pages.

**All dashboards** also show a **Today's plan** card — planned and done counts for today's day plan (§6.12) within the viewer's scope (own tasks for a Member, department for a Department Admin, company-wide for a System Admin) — linking to the Today page, plus a "Today" quick link beside "My Tasks".

### 6.10 Time Tracking
- **Log time on a task:** from the task detail page, a user logs a time entry — date, duration, and an optional note. A task can have many entries (e.g. logged across several days).
- **Time logged per task:** task detail page shows a running total and the individual entries.
- **Time logged per project:** project detail page shows total time logged across its tasks, for a quick "how much effort has this actually taken" view.
- **My time:** a simple page (or dashboard widget) showing the current user's logged time, filterable by date range — useful for a weekly personal check rather than a full report.
- Anyone can log time against a task they're assigned to; `DepartmentAdmin` can log or edit time entries for anyone in their own department, `SystemAdmin` for anyone anywhere (e.g. correcting an entry on someone's behalf).
- **Start / Stop clock:** alongside manual entry, the task detail page has a **Start Clock** button for anyone who may log their own time on the task (same rule as above: assignee, or an admin for the task's department). While the clock runs the button becomes **Stop Clock** next to a live `hh:mm:ss` counter. The clock stops — and the elapsed time is logged as a normal time entry for the current user — when the user clicks **Stop Clock** *or leaves the task page* (any navigation, closing the tab, reloading). Details:
  - **One clock per user, stored server-side** (`RunningClock`: user, task, started-at; unique per user), so it survives across requests and is enforced at the database level. Starting a clock while one is already running (on this or another task) stops and logs that one first.
  - **Leaving the page:** the browser sends `navigator.sendBeacon` to a stop handler on `pagehide`. Form posts that return to the same page (comment, manual time entry, status change, delete entry) are not "leaving" and keep the clock running. If the beacon never arrives (crashed browser, killed tab), the clock is still running when the user next opens a *different* task, and that page stops and logs it then, with a message saying so.
  - **What gets logged:** the entry's date is the day the clock started, its duration is the elapsed time rounded to the nearest minute, and its note is `Clock hh:mm-hh:mm`. Under 30 seconds nothing is logged (an accidental click doesn't create a 1-minute entry); over 24 hours is capped at 24 hours (the normal per-entry limit) — the entry stays editable afterwards like any other.
  - **Audit:** `ClockStarted` and `ClockStopped` are recorded on the task, and the resulting entry is a normal `TimeLogged` (flagged `clock: true`), so the task history shows the full sequence.
  - The clock is personal: admins can't run it on someone else's behalf (use manual entry for that).

> **Assumption flagged:** the timer is "wall clock while on the page", not a pause/resume stopwatch, and there is no billable-rate tracking (billing is a non-goal per §3). Reloading the task page counts as leaving it — say if you'd rather a reload kept the clock running.

### 6.11 Appearance — light and dark theme
- The UI ships with two themes, **light** (the original look) and **dark**, built on Bootstrap 5.3's colour modes: the active theme is the `data-bs-theme` attribute on the `<html>` element, and every page — including the Identity pages (login, account management), which share the same layout — follows it.
- **Toggle:** an icon button sits at the right-hand end of the navbar, immediately to the left of the signed-in user's name (or of the Login link when signed out). It shows a moon in light mode ("Switch to dark theme") and a sun in dark mode ("Switch to light theme"); clicking it flips the theme instantly without a page reload. The button carries a matching `title`/`aria-label`, so it is usable by keyboard and screen reader.
- **Persistence:** the choice is stored per browser in `localStorage` (key `orbit-theme`), not against the user account — it is a device preference rather than profile data, needs no round-trip or schema change, and works on the login page before anyone is signed in.
- **Initial theme:** on each page load an inline script in the layout's `<head>` applies the saved choice before first paint (no light-to-dark flash). If nothing is saved, the OS/browser `prefers-color-scheme` setting decides, falling back to light. If `localStorage` is unavailable the toggle still works for the current page.
- **Coverage:** Orbit's custom surfaces (page background, stat tiles, cards, kanban columns/cards, activity feed, overdue highlight, the "Claude" badge) are defined as CSS variables with a light and a dark value, and Bootstrap's `text-bg-light` badges and `table-light` headers get a dark-theme equivalent so no near-white blocks remain on a dark page. The navbar is dark in both themes.

### 6.12 Day planner (Today)
- **Purpose:** in the morning scrum the team picks what each person will work on today. Orbit records that choice on the task and shows it in one place, so the plan is visible all day and the next morning's "what did you do yesterday / what will you do today" is a page rather than memory.
- **Planned-for date:** a task carries `PlannedFor` (§5.1), the day it is on the plan. Ticking **Today** sets it to today's date; unticking clears it. Because it is a date it simply stops being "today" tomorrow — nothing is reset by a job — and done tasks keep their date so "done today" stays visible on the plan.
- **Today tick box:** a "Today" checkbox column on every task table except the dashboard's — the Tasks list, My Tasks, Backlog, sprint detail and project detail — plus a "Plan for today / Remove from today" button and a Today badge on the task detail page. It saves as soon as it is ticked, like the other inline controls (§6.2). Shown only on open rows the user may plan, which is the same rule as sprint planning: anyone in the task's department (`SystemAdmin` anywhere), including unassigned tasks a Member picks up. Closed tasks can't be put on a plan (they can be taken off).
- **Filter:** the Tasks list has a "Planned today" filter; the API takes any planned-for date.
- **Today page** (nav link "Today"): today's plan grouped by person — one card per assignee, unassigned tasks last — with the same inline status/assignee/due/Today controls as the Tasks list, and summary tiles (planned, done, still open, overdue). Department-scoped like every list: `DepartmentAdmin`/`Member` see their own department, `SystemAdmin` sees all with a department filter.
- **Not finished last time:** above the groups, the page lists open tasks still sitting on the most recent earlier plan (labelled "yesterday" when that was yesterday, otherwise the date, so a Monday shows Friday's leftovers) with three ways to carry over: tick Today on one row, select several and "Carry selected over", or "Carry all over". Carrying over re-dates the task to today; nothing is copied.
- **Audit:** `Planned` / `Unplanned` entries on the task (a carry-over is `Planned` with `carriedOver: true`), shown in the task history.
- **Dashboard:** a "Today's plan" card and a Today quick link (§6.9).
- **MCP:** `list_tasks` takes `plannedFor` (a date or `"today"`); `update_task` takes `plannedFor` (a date, `"today"`, or `"none"` to take the task off the plan); task payloads include `plannedFor` (§7.1).

> **Assumptions flagged:** "today" is the UTC date, the same convention as due dates and everything else in Orbit — fine for a morning scrum in any timezone, but a plan made close to midnight UTC lands on the next day. v1 plans today only; the service already accepts any date, so "plan for tomorrow" is a small follow-up if wanted. Nothing clears a plan automatically: a task planned but never touched simply shows up under "Not finished" next time.

### 6.13 Directory sign-in (LDAP / Active Directory)

- **Purpose:** let people sign in to Orbit with the password they already have in the company directory, as an alternative to a local Orbit password. It is **optional** and **per user** — both methods coexist indefinitely.
- **Sign-in method is a property of the user** (`AuthSource`, §5.1), set by a `SystemAdmin` on the Create/Edit user page: *Local password* or *Directory (LDAP)*. It is not a fallback chain: a local user's password never goes near the directory, and a directory user has no local password to fall back to. This keeps exactly one thing able to vouch for each user, and keeps local accounts working when the directory is unreachable.
- **Accounts are still created in Orbit first.** A directory account on its own grants nothing: if no Orbit user has that email, sign-in fails exactly as it does for any unknown user. Role and department are Orbit's to decide, and self-registration stays disabled (§6.5). There is no provisioning from, or sync with, the directory (§3).
- **Matching:** the person signs in with their Orbit **email**, as now. Orbit looks up the Orbit user by that email and, for a directory user, asks the directory for the entry matching the **user filter** (default `(mail={0})`; `{0}` is the email, escaped). The filter must match **exactly one** entry — none, or more than one, is a failed sign-in. The filter is also how access can be narrowed to a group, e.g. `(&(mail={0})(memberOf=CN=Orbit Users,...))`.
- **Login page:** one form for everyone. Outcomes for a directory user:
  - *Correct password* → signed in; from here on the session is an ordinary Orbit session (same cookie, same two-factor step if the user has enabled it, same role/department rules).
  - *Wrong password, unknown in the directory, ambiguous match, or an account the directory won't accept* (disabled, locked, expired, must-change-password) → the same generic "Invalid login attempt." a local user gets. The specific reason (e.g. "account disabled (AD 533)") is written to Orbit's log for the administrator and never shown to the person signing in. The failure counts towards lockout (§6.5).
  - *The check couldn't be made at all* (no agent online, the agent timed out, the directory is unreachable, the service account's bind failed) → "Sign-in with your company directory account is temporarily unavailable…". This is **not** counted as a failed attempt: an outage must never lock people out.
  - *Locked out or deactivated in Orbit* → refused **without contacting the directory**.
- **Passwords:** Orbit never stores a directory user's password and never logs any password. A directory user who opens *Manage account > Password* is sent back with a note that their password is managed by the directory; a `SystemAdmin` cannot set or reset one for them (§6.5). Switching a user's sign-in method in either direction **discards any password hash Orbit holds** and signs the user out: switching to the directory must not leave a usable local password behind, and switching back must not revive an old one — the admin sets a new temporary password or reset link.
- **Admin > Directory** (`SystemAdmin`-only): the directory settings of §5.1 — enable/disable, server, port, SSL, certificate validation, service account (bind DN + password), search base, user filter.
  - The bind password is **write-only**: never rendered back, left blank to keep the stored one, stored encrypted.
  - **Test connection** asks a connected agent to connect, bind as the service account and optionally look up a sample email, and reports each step ("Connected… Bound as… Found CN=…") or the step that failed. It tests **the values in the form, saved or not**, so settings can be proven before they go live. No user password is involved.
  - The page shows whether any agent is connected, and warns when none is.
  - Directory sign-in must be **enabled** before a user can be created as, or switched to, a directory user. Disabling it later doesn't convert anyone: directory users simply can't sign in until it is re-enabled.
  - Changes are audited (`LdapSettings`: `Created`/`Updated` with the changed fields). The audit entry records *that* the bind password changed, never its value.
- **Secure defaults:** LDAPS on 636 with the server's certificate validated. Both can be switched off for a directory that can't do better (the test result says so in words: "no TLS - passwords cross the network in the clear"), but they are never off by default.

> **Assumptions flagged:**
> - **Disabling someone in the directory stops new sign-ins but does not end an Orbit session that is already open** (the cookie lasts up to 7 days, sliding — §8). Deactivate the user in Orbit as well to end it at once. Having the agent report disabled accounts so Orbit can do this by itself is a natural follow-up, deliberately not in v1.
> - The "temporarily unavailable" message differs from "Invalid login attempt", so *during an outage* someone could learn that a given email is a directory user. That was judged a fair price for telling staff the truth rather than implying they mistyped their password.
> - The email is the join between an Orbit user and a directory entry. If someone's email changes in the directory, update it in Orbit too (or use a filter on an attribute that doesn't change).

### 6.14 Orbit Agents

- **Purpose:** the directory is behind the corporate firewall and Orbit is outside it. Rather than open a port inwards, an **Orbit Agent** — a small service installed on a machine inside the network — connects **outwards** to Orbit and carries out, on Orbit's behalf, the things that can only be done from inside. Today that is checking directory sign-ins (§6.13) and testing the directory settings; the agent is built as a general connector so further capabilities can be added without changing how it is installed or registered.
- **Practically no settings on the agent.** The agent knows two things: Orbit's URL and the credential Orbit issued it. Both are written by one command. Directory servers, service accounts, filters — everything else — live in Orbit (§5.1 `LdapSettings`) and are sent to the agent with each request, so changing them never involves touching the agent.
- **Registration, GitHub-runner style** (`SystemAdmin`-only, **Admin > Agents**):
  1. *New agent* → the admin names it. Orbit creates it as `Pending` and shows, **once**, a ready-to-paste command: `Orbit.Agent configure --url https://<orbit> --token <one-time token>`, with the URL filled in from `App:BaseUrl`, plus the run/install-as-a-service commands.
  2. The token is single-use and expires after an hour (`Agents:RegistrationTokenLifetimeMinutes`). *New token* on a still-pending agent issues another.
  3. Running the command on the agent machine redeems the token for the agent's long-lived credential and saves it locally (§8.2). The agent becomes `Active`.
  4. `Orbit.Agent run` — directly, or as a Windows service / systemd unit — connects, and the agent shows as **Online**.
- **Agents list:** name, status (**Online** / **Offline** / *Waiting for configure* / *Token expired* / **Revoked**), machine name and OS, agent version, last seen, source address, created date.
- **Revoke:** invalidates the credential and **disconnects the agent immediately**. **Delete** removes a pending or revoked row from the list. `Orbit.Agent remove`, run on the agent's machine, de-registers it from that side and deletes its local configuration.
- **More than one agent** may be registered, for redundancy: Orbit asks each connected agent in turn until one can complete the request.
- **Audit:** `Agent` `Created`, `Registered` (with the machine, OS, version and address it registered from), `Revoked`, `Deleted`.
- The agent's operational side — credentials, connection, protocol, security — is specified in §8.2; installing it in §10.3.

## 7. Functional Requirements — Claude Integration (MCP Server)

Orbit exposes an **MCP (Model Context Protocol) server** rather than a plain REST API, so Claude can connect to it directly as a tool provider (e.g. from a Claude Project's connector config) instead of needing a hand-rolled integration layer in between.

- Hosted in-process inside `Orbit.Web` using the official `ModelContextProtocol` .NET SDK, over the **Streamable HTTP** transport (the current recommended MCP transport for a server that isn't a local process), exposed at a route like `/mcp`.
- Underneath, each MCP tool calls the same `Orbit.Application` services (`TaskService`, `ProjectService`, `CommentService`) used by the Razor Pages UI — no duplicated business logic between the human UI and the Claude-facing tools.
- Every write is still stamped `Source = Api` and logged to `AuditLog`, and Claude-created tasks still require no approval step — they land directly in the active task list, relying on the `Source = Api` tag for visibility rather than a gate.

### 7.1 MCP Tools

| Tool | Purpose |
|---|---|
| `create_task` | Create a task. Args: title, description, projectId (optional), departmentId (optional — with a projectId it defaults to the project's department; otherwise to the key's own department for a `DepartmentAdmin`/`Member` key, and is required for a `SystemAdmin` key with no department, since the task needs one), priority, dueDate, assigneeId (optional), idempotencyKey (optional). A `SystemAdmin` key may pass a departmentId that differs from the project's to file a cross-department project task (§6.2.1); any other key is rejected for that. `Source` is forced to `Api` server-side. |
| `get_task` | Get a single task's full detail. |
| `list_tasks` | List tasks. Args: `projectId`, `departmentId`, `status`, `assigneeId`, `source`, `dueBefore`/`dueAfter`, `plannedFor` (a date or `today` — the day plan, §6.12). Paginated. A `DepartmentAdmin`/`Member` key is scoped to its own department regardless of the `departmentId` arg; a `SystemAdmin` key can query any department or omit the filter for all. |
| `update_task` | Full edit of a task — title, description, status, priority, assignee, due date, project link, sprint, department (`SystemAdmin` keys only; a department other than the project's makes it a cross-department project task, §6.2.1), and the day-plan date (`plannedFor`: a date, `today`, or `none` to take it off the plan; closed tasks can't be planned, §6.12). Setting status to `Done`/`Cancelled` requires a `DepartmentAdmin`- or `SystemAdmin`-role API key (§6.5, §8); a `Member`-role key gets rejected on that field. A `DepartmentAdmin` key is rejected if the task isn't in its own department. |
| `add_comment` | Add a comment to a task (e.g. Claude explaining why it proposed or updated a task). |
| `list_comments` | List a task's comments. |
| `create_project` | Create a project. Args include `departmentId` with the same default/required rule as `create_task`. |
| `get_project` | Get project detail including its tasks, each with its department (`crossDepartment` flags a project that spans several). Visible to the project's department, a `SystemAdmin` key, and any department with tasks filed under it (§6.2.1). |
| `get_project_status` | Lightweight status summary only — task counts by status, overdue, progress, time logged and a per-department breakdown (each department's counts by status, overdue, open/total and done-%, matching the project page's table) — good for a quick "how's project X doing" check without pulling every task. |
| `list_projects` | List projects with summary status (name, status, open/total task counts). Same department scoping as `list_tasks`, plus other departments' projects that have tasks filed for the key's department (§6.2.1). |
| `update_project` | Edit a project — name, description, status, owner, target date. Same field-level rules as the Razor Pages UI (§6.1); no separate close-restriction applies here since project status isn't gated like task closing is. A `DepartmentAdmin` key is rejected if the project isn't in its own department. |
| `list_activity` | Read-only view of the `AuditLog` (§5.1) — every task/project/comment write, who or what made it (`ActorType`/`ActorId`), and when. Args: `from`, `to` (both optional date/time bounds defining the window — e.g. Claude computes `from = now - 24h` for "show activity from the last 24 hours"; if `from`/`to` are omitted, defaults to the most recent 24 hours), `entityType`, `entityId` (optional, to scope to one task/project). Scoped to the key's own department like `list_tasks`, unless the key is `SystemAdmin`. Lets Claude (or a person asking Claude) answer "what's changed on this task/project recently" without pulling raw DB access. |
| `list_users` | List all users (`id`, `displayName`, `email`, `role`, `departmentId`). Args: optional `query` to filter by name/email fragment (e.g. "Bob"), optional `departmentId`. A `DepartmentAdmin`/`Member` key sees only its own department's users by default (so "assign it to Bob" naturally resolves to the Bob in the caller's own department); a `SystemAdmin` key can query any department. |
| `list_departments` | List all departments (`id`, `name`, `description`). Read-only, available to every role — needed so a `SystemAdmin`-role key can pick a `departmentId` when creating a task/project outside its own scope-free context. |

All of these are read-only except `create_task`, `update_task`, `add_comment`, `create_project`, and `update_project`. `list_users`, `list_departments`, and `list_activity` are available to every role's API key (read-only lookups carry the same risk level as `list_tasks`, not the closing/admin actions gated in §6.5). `update_project` follows the same department rule as `update_task` rather than being open to every role unconditionally.

### 7.2 MCP Authentication

The MCP connection authenticates with the same API key scheme as §8 — Streamable HTTP supports custom headers, so the API key travels the same way (`Authorization: Bearer <key>`) as it would on a REST call. The key's `Role` and `DepartmentId` (Member/DepartmentAdmin/SystemAdmin) still govern what its tool calls are allowed to do, exactly as in §6.5.

## 8. Authentication (Interactive Users, MCP, Orbit Agents)

Three different populations hit this system, so three different auth schemes make sense:

- **Interactive users (Razor Pages UI):** ASP.NET Core Identity, cookie-based session, standard Individual Accounts login flow. How the password is *checked* depends on the user's sign-in method — Orbit's own hash, or the company directory via an Orbit Agent (§8.1). Everything after that check is identical.
- **Orbit Agents (§8.2):** a long-lived agent secret sent as `Authorization: Bearer <secret>`, handled by its own authentication scheme. An agent principal identifies the agent and carries **no role and no department**, so it can reach the agent hub and nothing else — not a page, not the MCP server.
- **Claude / machine callers (MCP server, §7):** since Individual Accounts has no external directory to issue machine tokens from, the natural fit is a long-lived **API key** — issued per integration, sent as `Authorization: Bearer <key>` on the MCP connection, validated against a hashed value stored in the `ApiKeys` table (§5.1). This is also the easiest option to wire into a Claude Project's connector config.

  Each key carries a `Role` (`SystemAdmin`, `DepartmentAdmin`, or `Member`) and, for the latter two, a `DepartmentId`, assigned when the key is issued. The same authorization checks that gate role and department scoping for human users (§6.5) apply to the key — e.g. a `Member`-role key can create and edit tasks in its own department but is rejected if it tries to close one or touch another department's data; a `DepartmentAdmin`-role key can close tasks within its own department; a `SystemAdmin`-role key can do both across any department. This means the same policy/handler code path enforces the rule regardless of whether the caller is a signed-in user, an API key on the MCP server, or (if a plain REST surface is ever added later) a REST caller — no separate "is this the API" special case for authorization.

  Implement it as a separate ASP.NET Core authentication scheme (e.g. a custom `AuthenticationHandler` for the API key) alongside the Identity cookie scheme, mapping the key's `Role` and `DepartmentId` into the request's claims so the existing `[Authorize(Roles = "...")]`/policy checks work unchanged for MCP callers.

Either way, the MCP identity should map to a synthetic `User` row (e.g. "Claude Agent") so `CreatedById`/audit trails have something to point at, distinct from a null value.

### 8.1 Directory sign-in flow

```
Browser ──login form (TLS)──▶ Orbit ──command, down the agent's own connection──▶ Orbit Agent ──LDAPS──▶ Directory
                                 ▲                                                    │
                                 └──────────────── result ◀───────────────────────────┘
```

1. The login page calls Identity's `PasswordSignInAsync` as it always has. Orbit substitutes its own `SignInManager` whose **only** override is the password check (`CheckPasswordSignInAsync`): for a `Local` user it defers to Identity unchanged; for an `Ldap` user it asks the directory instead of comparing a hash.
2. Because the override sits *beneath* `PasswordSignInAsync`, everything around the password check is stock Identity and therefore identical for both kinds of user: the deactivation/lockout pre-check (which runs **before** the directory is contacted), failure counting and lockout, the two-factor step, the cookie, the security-stamp revalidation.
3. For a directory user Orbit loads the directory settings, picks a connected agent that advertises the `ldap.authenticate` capability, and invokes `Authenticate` on it with `{ settings, username, password }`, waiting up to `Agents:CommandTimeoutSeconds` (default 15). If that agent doesn't answer, or reports it couldn't reach the directory, the next connected agent is tried.
4. The agent (a) binds as the service account, (b) searches for the entry matching the user filter — requiring exactly one, (c) binds **as that entry** with the supplied password on a fresh connection, and returns `Success`, `InvalidCredentials`, `UserNotFound`, `Ambiguous`, `Unavailable` or `Error`, with a diagnostic detail for Orbit's log.
5. Orbit maps that to the three outcomes of §6.13: success → sign in; invalid/not-found/ambiguous → generic failure, counted; unavailable/error/no agent/timeout → "temporarily unavailable", **not** counted.

Rules the implementation must keep, each of which closes a specific hole:
- **An empty password is refused before it reaches the directory** — by Orbit and again by the agent. An LDAP simple bind with an empty password is an "unauthenticated bind", which Active Directory answers with *success* without checking anything; passing one through would sign anyone in as anyone.
- **The sign-in name is escaped (RFC 4515) before it is placed in the search filter**, so input such as `*)(objectClass=*` is searched for literally instead of rewriting the filter.
- **Exactly one match.** Taking "the first" of several entries would let whichever entry the server happened to list first decide who is signing in.
- **The server certificate is validated by default**; accepting any certificate would let anything on the network path pose as the directory and harvest passwords. It can be switched off, explicitly, per §6.13.
- **Failures before the user's own bind are never the user's fault** (unreachable server, service account rejected, timeout) and must be reported as `Unavailable`, not as a wrong password — otherwise an outage would lock out everyone who tried to sign in during it.

### 8.2 Orbit Agent: registration, credential, connection, commands

**Registration.** `POST /agent/register` with `{ token, machineName, osDescription, version }`. It is anonymous by necessity — the token *is* the credential — so the token is 256 bits of randomness, stored only as a SHA-256 hash, single-use and short-lived, and the endpoint is rate-limited. Redemption is a single conditional update (*pending, this token, not expired* → *active, this secret*), so two racing `configure` runs cannot both succeed. Any failure returns the same 401 without saying which check failed. On success Orbit returns `{ agentId, name, secret }` — the only time the secret exists outside the agent's machine.

**Credential.** The secret (`orbitagent_…`) is stored in Orbit only as a SHA-256 hash, like an API key (§5.1); its prefix differs from an API key's (`orbit_…`) so neither is ever looked up as the other. On the agent it is saved in `agent.json` beside the executable — encrypted with DPAPI (machine scope, so the service account can read what the installing admin wrote) on Windows, file mode `600` elsewhere. The agent refuses a non-`https` Orbit URL (loopback excepted, for development), because users' passwords travel over this connection.

**Connection.** The agent opens a SignalR connection to `/agent/hub` — **outbound**, HTTPS, WebSocket where the path allows and Server-Sent Events/long polling where it doesn't, through the machine's configured proxy if it has one. Nothing listens on the agent's side. It reconnects forever with capped back-off, and re-establishes the connection itself if Orbit closes it deliberately (a server-initiated close forbids SignalR's automatic reconnect, so the agent must not rely on it). After each (re)connect it sends `Hello { machineName, osDescription, version, capabilities[] }`; **until then Orbit sends it nothing**. One connection per agent: a second connection with the same credential replaces the first.

**Commands.** Orbit → agent, as SignalR *client results* (Orbit invokes a method on one specific connection and awaits its return value): `Authenticate(LdapAuthRequest) → LdapAuthResult` and `TestDirectory(LdapTestRequest) → LdapTestResult`. **Every command carries everything it needs** — the directory settings travel with each request — so the agent is stateless: nothing to cache, nothing to invalidate, and a settings change in Orbit applies to the very next sign-in. Orbit only sends a command to an agent whose `Hello` listed the matching **capability** (`ldap.authenticate`, `ldap.test`); that is the extension point for future agent features — a new command is a new capability name, and agents that predate it are simply never asked.

**Shared contract.** The message types and method/route names live in one small library (`Orbit.Agents.Contracts`) referenced by both Orbit and the agent, so the two cannot drift. Types that carry a secret are classes rather than records, because a record's generated `ToString()` prints every property.

> **Assumption flagged:** the registry of connected agents is in memory, which assumes a **single Orbit instance** — the same assumption the in-memory Quartz store already makes (§6.4). Running several instances would need a SignalR backplane and a shared registry.

### 8.3 Security properties

- **No inbound exposure.** The corporate firewall is untouched; the agent needs outbound HTTPS to Orbit and LDAP(S) to the directory.
- **Where a user's directory password goes:** browser → Orbit (TLS) → agent (TLS, over the agent's authenticated connection) → directory (LDAPS). It is held in memory for the duration of the request and is **never stored and never logged** by Orbit or the agent. Orbit necessarily sees it — the login form posts to Orbit — so relaying it to the agent adds no party that didn't already have to be trusted, other than the agent itself.
- **What Orbit stores about the directory:** the settings, with the service-account password encrypted under the Data Protection key ring. Use a **read-only, least-privilege service account**: Orbit is internet-facing, and this is the one directory credential it holds.
- **The agent's credential is the crown jewel.** Whoever holds a valid `agent.json` can connect as that agent, and would then be *sent* directory users' passwords to check, along with the service-account password. Hence: stored hashed in Orbit and protected on disk on the agent; shown to nobody; the agents list shows each agent's machine and source address so an unexpected one stands out; registration is audited; and **Revoke** takes effect immediately, dropping the live connection.
- **Orbit cannot be used to attack the directory from the internet.** Three layers, each covering what the others can't:
  - *Only known users reach the directory.* An email with no Orbit account fails inside Orbit; the agent is never asked.
  - *Per-account lockout, set stricter than the directory's own* (§6.5; default 3 failures / 30 minutes, configurable). This bounds guessing at one account **and** — the less obvious half — keeps Orbit from becoming a way to lock people's directory accounts from outside: a locked or deactivated Orbit account is refused without the directory being contacted, so the directory never sees enough failures from Orbit to trip its own lockout, provided Orbit's threshold is kept below it.
  - *Per-address limit on failed sign-ins* (default 20 per 15 minutes), for the attack lockout can't see: one likely password tried once against many accounts. It counts **failures, not requests** — a whole office shares one public address, and fifty people signing in successfully at nine o'clock must cost nothing — which is why it lives in the login page rather than in rate-limiting middleware, which has to decide before the outcome is known. A directory *outage* is not counted (it isn't a guess); a correct password awaiting its second factor is not counted; a lockout is. Over the limit the address is refused with HTTP 429 before the sign-in manager runs, so nothing reaches the directory and no account's failure count moves. IPv6 is counted per /64, since one subscriber holds a whole /64. Counting reuses the framework's sliding-window limiter, one per address, created and evicted on demand.
  - *Limits of that last layer, stated plainly:* it is a speed bump (on the order of 2,000 guesses a day per address rather than unlimited), not a wall — an attacker with many addresses is slowed per address only. And it depends on the reverse proxy sending `X-Forwarded-For` (§10.2): without it every user appears to be `127.0.0.1` and would share one budget, so that twenty bad guesses from anyone would stop the whole company signing in. Orbit therefore **does not throttle loopback addresses** and logs a warning instead — failing open to the behaviour without a throttle, rather than handing out a denial-of-service lever.
- **Every cookie is `Secure` outside development** — session, two-factor, antiforgery and TempData (which carries one-time password-reset links) — set unconditionally by cookie policy rather than inferred from the request scheme, so a reverse proxy that fails to forward the scheme can't cause session cookies to travel over plain HTTP. Orbit must be served over HTTPS, which the agent requires anyway.
- **No second factor is enforced — a known gap, accepted for now.** A directory sign-in is a password-only LDAP bind: it does not pass through whatever MFA or conditional access protects the directory elsewhere, so a phished or reused directory password is enough to get into Orbit. Users can enable Orbit's own authenticator-app two-factor (Identity's standard feature, and it applies to directory users exactly as to local ones, §8.1); making it mandatory, for directory users or for System Admins, is the natural next hardening step (§13, item 29).
- **Transport encryption is hop by hop, not end to end.** TLS protects browser → Orbit, Orbit → agent and agent → directory separately; the password is in the clear in Orbit's memory and the agent's, which is unavoidable since both must handle it, and on the loopback hop between the reverse proxy and Orbit. A TLS-*inspecting* corporate proxy on the agent's outbound path could read what the agent relays. Such a proxy already sees every other site the organisation's staff sign in to, so it is inside the trust boundary; encrypting each password to a key held by the agent would remove it, and was considered and deferred (§13, item 29).
- **An agent cannot be used to attack Orbit:** its principal has no role or department and is accepted by the agent hub only. The agent never initiates anything but its connection and its `Hello`.
- **Break-glass:** at least one `SystemAdmin` always has a local password (§6.5), so Orbit stays administrable when the directory or the agent is down.

## 9. Non-Functional Requirements

- **Auditability:** every MCP-originated write is logged (§5.1 AuditLog) — this matters more here than in a typical CRUD app, since an AI agent is writing data unsupervised.
- **Idempotency:** `create_task` should accept an optional client-supplied `IdempotencyKey` argument so a retried Claude call doesn't create duplicate tasks.
- **Performance:** trivial at expected team scale (tens of users, hundreds of tasks) — no special work needed beyond normal EF Core indexing on `ProjectId`, `AssigneeId`, `Status`.
- **Hosting:** production runs on Ubuntu Linux (§10); dev/local runs wherever a developer runs `dotnet run`. The Orbit Agent runs wherever it can reach the directory — typically a Windows server inside the corporate network — and is cross-platform (§10.3).
- **Credentials at rest:** nothing that authenticates anyone is stored in the clear. API keys, agent secrets and agent registration tokens are stored as SHA-256 hashes; the directory service-account password is encrypted with Data Protection; directory users' passwords are not stored at all (§8.3).
- **Secrets never reach a log:** no password — a user's or the service account's — is logged by Orbit or by the agent; sign-in log lines carry the username, the outcome and the directory's reason.
- **Abuse resistance at the sign-in page:** per-account lockout tuned below the directory's own, a per-address limit on failed sign-ins, and unknown emails never reaching the directory — so an internet-facing Orbit can be used neither to guess directory passwords at leisure nor to lock directory accounts from outside (§8.3). A second factor is available to every user but not enforced (§13, item 29).
- **Availability of directory sign-in** depends on Orbit, an agent and the directory all being up. It degrades safely: an outage produces a clear "temporarily unavailable" rather than a misleading failure, never counts towards lockout, never affects local accounts, and can be mitigated by registering a second agent (§6.14). The break-glass rule (§6.5) keeps Orbit administrable throughout.

## 10. Configuration & Deployment

Orbit relies on ASP.NET Core's standard layered configuration (`appsettings.json` → environment-specific file → environment variables → anything set at the host level), so dev and production get different sources for the same settings without any app code branching on environment.

### 10.1 Development

- Connection string and any other local overrides live in `appsettings.Development.json`, loaded automatically when `ASPNETCORE_ENVIRONMENT=Development` (the default for `dotnet run`/Visual Studio/Rider debug profiles).
- `appsettings.Development.json` is **not committed with real secrets** — it points at a local or shared dev Postgres instance, which is a much lower-stakes credential than production, but it still shouldn't carry a production password. Standard practice: gitignore it, or use `dotnet user-secrets` for anything sensitive even in dev.

### 10.2 Production (Ubuntu Linux)

- Secrets and environment-specific settings are supplied as **environment variables**, loaded from an env file (e.g. `/etc/orbit/orbit.env`) referenced by the systemd unit's `EnvironmentFile=` directive — not baked into `appsettings.json` and not committed anywhere.
- **Key mapping:** ASP.NET Core's configuration nesting separator (`:`) isn't valid in a Linux environment variable name, so nested keys use a double underscore instead — e.g. `ConnectionStrings:DefaultConnection` becomes `ConnectionStrings__DefaultConnection`. Anything set this way overrides the equivalent `appsettings.json` value at startup.
- **File permissions:** the env file is owned `root:root` and `chmod 600`, and systemd reads it as root before dropping to the service's run-as user — so the plaintext credentials are never world-readable on disk.
- **Least privilege:** the database connection string uses a dedicated `orbit` role scoped to just the `orbit` database (created by a setup script, not the Postgres superuser) — so a compromised app process can't reach other databases on the same server or alter roles/permissions.
- **Data Protection key ring:** persisted to a stable directory outside the app's deployment folder (so a redeploy doesn't wipe it) and `chmod 700`. This is a real, easy-to-miss requirement: ASP.NET Core's Data Protection keys sign auth cookies and antiforgery tokens; without a stable, persisted key ring, every app restart regenerates the keys and silently signs out every logged-in user. The directory should be included in backups for the same reason.
- **First-run admin bootstrap:** initial `SystemAdmin` account details (email, display name, a temporary password) are supplied via environment variables and only consumed if no such account exists yet — i.e. it's a one-time seed, not something the app re-applies on every start. Operationally, the person deploying signs in with the seeded account, changes the password immediately, then removes the seed variables from the env file and restarts — leaving a real credential sitting in a config file indefinitely is exactly the kind of thing this pattern is meant to avoid.
- **Optional feature toggles:** background-job settings (whether recurring-task generation or due-date notifications are enabled, their Quartz cron expressions, run-on-startup, notification lead time: `Jobs__RecurringTasks__Cron`, `Jobs__DueDateNotifications__Cron`, etc.) are also environment-variable-overridable, with sane defaults baked into `appsettings.json` so they don't need to be set explicitly. The same goes for the two agent timings, `Agents__CommandTimeoutSeconds` and `Agents__RegistrationTokenLifetimeMinutes`. Directory settings are deliberately **not** configuration: they are edited in the UI (§6.13) so that neither Orbit's env file nor the agent has to be touched to change them.
- **Sign-in hardening** (`Security__Lockout__MaxFailedAttempts`, `Security__Lockout__LockoutMinutes`, `Security__LoginThrottle__Enabled` / `MaxFailures` / `WindowMinutes`; §6.5, §8.3). The lockout pair is the one setting that must be chosen against the organisation's **Active Directory password policy** rather than left to taste: attempts below AD's lockout threshold, minutes at least AD's counter-reset interval. The defaults (3 / 30) suit the common policies.
- **Behind the reverse proxy:** Orbit honours `X-Forwarded-For`/`X-Forwarded-Proto`, from a proxy on the same host only (the framework's loopback-only default), so the request scheme and the source addresses it sees — an agent's, and every sign-in attempt's — are the real ones rather than `127.0.0.1` over `http`. `X-Forwarded-For` is therefore a **security setting**: the failed-sign-in throttle counts by client address and switches itself off, with a warning in the log, if all it ever sees is loopback (§8.3). Caddy sends it by default; nginx must be told to. The proxy must allow WebSocket upgrade and long-lived connections on `/agent/hub`; if it doesn't, the agent falls back to Server-Sent Events or long polling by itself. `App__BaseUrl` must be the public `https` address: it is what the agent's `configure` command is built from.
- **Data Protection key ring, again:** it now also encrypts the directory service-account password. Losing it means re-entering that password under Admin > Directory; nothing else about directory sign-in is affected.

### 10.3 Orbit Agent (inside the corporate network)

- **What it is:** a self-contained .NET Worker Service (the `Orbit.Agent` project), published separately from the web app (`dotnet publish Orbit.Agent -r win-x64|linux-x64 --self-contained -p:PublishSingleFile=true`) so the target machine needs no .NET installed. It is not part of the web app's (`Orbit.Web`) publish output.
- **Where it runs:** any machine inside the network that can reach the directory server. **Network needs: outbound HTTPS to Orbit, LDAP(S) to the directory. Nothing inbound.** It uses the machine's proxy settings (`HTTPS_PROXY`, or the Windows system proxy) automatically.
- **Configuration: one command.** `Orbit.Agent configure --url <orbit> --token <token>`, copied from Admin > Agents (§6.14), writes `agent.json` beside the executable. There is no other configuration file to maintain, and no directory setting on the agent.
- **Commands:** `configure` (register; `--replace` to overwrite an existing registration), `run` (connect and serve — what the service runs), `remove` (de-register and delete the local configuration).
- **As a service:** Windows — `sc.exe create OrbitAgent binPath= "…\Orbit.Agent.exe run" start= auto`; Linux — the supplied `deploy/orbit-agent.service` unit, under a dedicated unprivileged user that owns `agent.json`. Logs go to the console, the Windows Event Log or the journal.
- **Protecting `agent.json`:** treat it as a password (§8.3). DPAPI-encrypted on Windows; mode `600` and owned by the service user on Linux. If it may have been copied, **Revoke** the agent in Orbit and register a new one.
- **Redundancy / upgrades:** register a second agent on another machine; Orbit uses whichever is connected. Replacing the executable needs no re-registration — `agent.json` stays.

> Note: the config file you pasted contains real database and admin credentials. I haven't reproduced any of those values here — the section above describes the pattern (env file + systemd `EnvironmentFile`, double-underscore nesting, `chmod 600`, first-run-only admin seed) rather than your actual secrets. Worth treating that password as already-exposed and rotating it, since it's now in this chat's history.

## 11. Suggested Solution Structure

```
Orbit.sln
 ├─ Orbit.Web/          (Razor Pages + API controllers, Identity)
 ├─ Orbit.Data/         (EF Core DbContext, entities, migrations)
 ├─ Orbit.Application/  (services: TaskService, ProjectService, RecurrenceService)
 └─ Orbit.Tests/
```

Keeping `Application` separate from `Web` means the same `TaskService.CreateTask(...)` call is used by both a Razor Page handler and the API controller — no duplicated business logic between the two entry points.

The Orbit Agent adds two projects alongside, as siblings at the solution root:

```
 ├─ Orbit.Agents.Contracts/   (messages + method/route names shared by Orbit and the agent — no dependencies)
 └─ Orbit.Agent/              (the on-premises Worker Service: SignalR client + LDAP client)
```

`Orbit.Web` references only the contracts; the LDAP library is a dependency of the agent alone.

**As built**, the solution (`Orbit.slnx`) is those three projects — `Orbit.Web`, `Orbit.Agent`, `Orbit.Agents.Contracts` — each in its own folder at the root, with `deploy/`, this spec and the README beside them. `Orbit.Data` and `Orbit.Application` have not been split out: inside `Orbit.Web` the `Data/` and `Application/` folders stand in for them, and the **namespaces already follow the structure above** (`Orbit.Data`, `Orbit.Application`, `Orbit.Auth`… — the project sets `RootNamespace` to `Orbit`, not `Orbit.Web`). Splitting them into real projects later is therefore a matter of moving folders, with no namespace churn and no change to the EF migrations' namespace. Every project living in its own folder matters for a practical reason too: a project at the repository root globs everything beneath it, and would compile its siblings' sources as its own.

> **Naming note:** the contracts namespace is `Orbit.Agents.Contracts` — plural — on purpose. A namespace `Orbit.Agent` visible to the web app would shadow the `Agent` entity throughout `Orbit.*` ("'Agent' is a namespace but is used like a type"). The agent executable's own namespace *is* `Orbit.Agent`, which is harmless because the web app never references that project.

## 12. Reporting

Basic operational reports, all filterable by date range and (optionally) project, with PDF export via **QuestPDF** (free Community license for most use — see licensing note below).

| Report | Definition |
|---|---|
| **Closed count by person** | Count of tasks where `Status = Done`, `CompletedAt` within the selected range, grouped by `AssigneeId` — i.e. who the task landed on and was carried to close by. |
| **Created count by person** | Count of tasks where `CreatedAt` is within the selected range, grouped by `CreatedById` — i.e. who authored the task. API-created tasks (`CreatedById` null) roll up under a "Claude" row rather than being dropped. |
| **Mean time to respond (MTTR-R)** | Average of `FirstRespondedAt − CreatedAt` across tasks in range that have a `FirstRespondedAt`, grouped by assignee and/or overall. |
| **Mean time to repair/resolve (MTTR)** | Average of `CompletedAt − CreatedAt` across tasks completed in range, grouped by assignee and/or overall. |

**Grouping confirmed:** "created count by person" groups by `CreatedById` (who authored the task); "closed count by person" groups by `AssigneeId` (who it was assigned to and closed by). Different grouping keys for each report, matching who actually performs each action.

**Implementation approach:**
- A `ReportingService` in `Orbit.Application` computes the aggregates via EF Core queries (grouping/averaging in SQL, not in memory) and returns plain DTOs.
- A Razor Page renders the DTOs as an HTML table/chart for on-screen viewing.
- A separate PDF endpoint takes the same DTOs and renders them through QuestPDF's fluent document API into a downloadable PDF.

**QuestPDF licensing:** confirmed under the $1M USD annual gross revenue threshold, so the free Community license applies.

## 13. Decisions Log

Earlier open questions, now resolved:

1. **Task edits via Claude:** the `update_task` MCP tool allows full edit of a task — any field, not just status/priority.
2. **Approval for Claude-created tasks:** not required. Tasks created via the MCP server land directly in the active list, distinguished by `Source = Api`.
3. **Comments:** included (§5.1, §6.2, §7) — a comment thread on each task, postable by both users and Claude via MCP.
4. **Notifications:** included (§6.7) — email on assignment and due date, built against `IEmailSender`. Only the interface/call sites ship in v1; a concrete sender is a later-stage addition.
5. **Reporting:** included (§12) — closed/created counts by person, MTTR-respond, MTTR-repair, exportable to PDF via QuestPDF.
6. **Roles:** originally Member and Admin, split on the ability to close a task; superseded by item 17 below — now Member, `DepartmentAdmin`, and `SystemAdmin`.
7. **Agile backlog/sprints:** included (§5.1, §6.3) — tasks with no `SprintId` sit in a backlog; sprint management is now `SystemAdmin`-only (§17); every role can move tasks they're allowed to edit between backlog and a sprint during planning. Only one sprint is `Active` at a time: activating a new sprint auto-completes the current one and rolls its still-open tasks forward onto the new sprint (not the backlog). Manually completing a sprint with no successor sends its open tasks back to the backlog instead.
8. **Claude integration transport:** Claude connects to Orbit as an MCP server (§7) rather than a plain REST API — same underlying service layer, exposed as MCP tools instead of HTTP verbs/routes.
9. **Non-goals confirmed:** no Gantt charts, no billing, web only, single-tenant (§3).
10. **Time tracking:** included (§5.1, §6.10) — manual duration + note entries logged against a task by whoever's assigned (`DepartmentAdmin`/`SystemAdmin` can log/edit on others' behalf, scoped per §17), with totals rolled up per task and per project. Not a start/stop timer, and not tied to billing.
11. **Dashboard:** originally separate Member and Admin dashboards; superseded by item 17 below — now three tiers (Member, Department Admin, System Admin), see §6.9.
12. **QuestPDF licensing:** confirmed under the $1M USD revenue threshold — the free Community license applies (§12).
13. **Report grouping:** confirmed — "created count by person" groups by `CreatedById`, "closed count by person" groups by `AssigneeId` (§12).
14. **Full MCP tool set confirmed:** `create_task`, `get_task`, `list_tasks`, `update_task`, `add_comment`, `list_comments`, `create_project`, `get_project`, `get_project_status`, `list_projects`, `update_project`, `list_activity`, `list_users` (§7.1) — since extended with `list_departments`, see item 17. `list_users` supersedes the earlier `find_user` — same name/email lookup, now as one tool covering both "list everyone" and "find by fragment."
15. **Admin user management extended:** `SystemAdmin` can also deactivate ("delete") a user account and reset a user's password (§6.5) — deactivation is a soft delete to preserve existing task/comment/audit attribution, and password reset reuses Identity's standard reset-token flow.
16. **`list_activity` time window:** takes explicit `from`/`to` args so Claude can scope a query like "show activity for the last 24 hours" (§7.1); defaults to the last 24 hours if omitted.
17. **Departments added:** a new `Department` entity (§5.1) that `User`, `Project`, and `Task` link to. The two-role model is replaced by three: `Member`, `DepartmentAdmin` (manages tasks/projects within their own department, including closing tasks), and `SystemAdmin` (renamed from `Admin` — manages everything across every department, plus departments themselves). Sprints stay company-wide and un-scoped to any department (§6.3) — sprint management (create/start/complete) is `SystemAdmin`-only, since no single `DepartmentAdmin` has authority over a company-wide construct. See §6.5, §6.6, §7.1, §8 for the full detail, and the assumption flags in §6.3 (whether `DepartmentAdmin` should have a role in sprint management) and §6.5 (whether `DepartmentAdmin` should manage users/see reports within their own department).
18. **Purpose reframed:** confirmed this is a business-wide tracker across all departments, not IT-specific — §1/§2 updated accordingly, with department boundaries enforced server-side (§6.5) and sprints as the one thing that deliberately crosses them (§6.3).
20. **Background jobs on Quartz.NET:** the recurring-task generator and the due-date notifier are Quartz.NET jobs (`Quartz.Extensions.Hosting`, in-memory store, DI-scoped job instances, `[DisallowConcurrentExecution]`) driven by cron expressions in configuration, with an optional run-once-on-startup trigger and fire-and-proceed misfire handling. This replaces the earlier "hosted service or Quartz" option in §6.4 with a firm choice.
19. **Configuration & deployment confirmed:** dev uses `appsettings.Development.json` for the connection string; production (Ubuntu Linux) uses environment variables loaded from an env file via systemd's `EnvironmentFile=`, with double-underscore key nesting, `chmod 600` file permissions, a least-privilege DB role, a persisted Data Protection key ring, and a one-time first-run admin seed that gets deleted after first login. See §10.
21. **Cross-department project tasks (§6.2.1):** a project stays owned by one department, but a `SystemAdmin` can file tasks (and recurring definitions) under it for other departments — unassigned or assigned to a user in that department. The earlier "a task's department must match its project's" rule now applies only to `Member`/`DepartmentAdmin`; a task's own `DepartmentId` decides who sees and works it, a department with tasks on another department's project sees that project read-only, and moving a project between departments carries only the tasks that sat in its previous department. No schema change: `Task.DepartmentId` already existed independently of `Project.DepartmentId`.
22. **Dark theme (§6.11):** a light/dark toggle in the navbar (icon to the left of the user name), implemented with Bootstrap 5.3 colour modes rather than a second stylesheet. The preference is per browser (`localStorage`), not per account — no schema or service change, and it applies on the login page too. With no saved preference the OS colour scheme is used.
23. **Start / Stop clock (§6.10):** the earlier "manual duration only" assumption is superseded — the task page now also has a Start Clock / Stop Clock timer that logs the elapsed time as an ordinary `TimeEntry` when stopped or when the user leaves the page. It is a wall-clock timer (no pause/resume), one per user, stored server-side in a new `RunningClock` table (§5.1) so it is enforced across tabs and survives requests. Leaving the page is detected with `navigator.sendBeacon`; a clock the browser failed to stop is closed off the next time the user opens another task. Sub-30-second runs are discarded, runs over 24 hours are capped at 24 hours. Manual entry stays as it was, including admins logging on others' behalf.
24. **Inline assignee and due date on task lists (§6.2):** the quick status control is joined by inline Assignee and Due date controls, shown only on rows the user could edit in full and saved through dedicated service methods (`ChangeAssigneeAsync`, `ChangeDueDateAsync`) with the same validation, audit and notification as the edit form. Enabled on the Tasks list, the sprint detail page and the project detail page; the shared task table switches it on per page, and My Tasks / Backlog stay status-only. Candidate assignees come from one lookup (`GetQuickEditCandidatesAsync`: all active users for a System Admin, own department plus System Admins otherwise), filtered per row to the task's department.
25. **Day planner (§6.12):** the morning-scrum plan is a `PlannedFor` date on the task rather than a boolean or a separate table — it expires by itself, needs no reset job, and gives "not finished last time" for free. Ticking "Today" follows the sprint-planning permission (anyone in the department), not the edit permission, so a Member can pick up an unassigned task. `PlannedFor` is deliberately outside `TaskInput`/`update_task`'s full-state edit and is set only through `SetPlannedForAsync`, so an ordinary edit can never wipe the plan. The Today page groups by assignee (not a kanban) so it can reuse the shared task table and its inline controls. "Today" is the UTC date like every other date in Orbit; v1 plans today only.

26. **Directory (LDAP / Active Directory) sign-in via an on-premises Orbit Agent (§6.13, §6.14, §8.1–§8.3, §10.3).** The directory is behind the corporate firewall and no inbound port may be opened, so a small agent inside the network connects *out* to Orbit and checks passwords on its behalf. Decisions taken:
    - **Sign-in method is per user** (`AuthSource`: `Local` | `Ldap`), chosen by a `SystemAdmin` — not "try the directory, fall back to local". One thing vouches for each user; a directory user has no `PasswordHash`; local accounts keep working when the directory doesn't.
    - **No auto-provisioning.** A directory account without an Orbit account is rejected, consistent with self-registration being disabled and every user needing a role and department (§6.5).
    - **Directory settings, including the service-account password, live in Orbit** (encrypted with Data Protection), not on the agent — the agent was to have "practically no settings". They are sent with every command, which also makes the agent stateless. The trade-off, accepted knowingly: internet-facing Orbit holds one directory credential, so it should be a read-only, least-privilege account.
    - **Registration copies the GitHub Actions runner:** a one-time, short-lived token and a paste-ready `configure --url … --token …` command; the agent ends up holding only Orbit's URL and its own credential.
    - **Transport is SignalR with client results** over the agent's outbound connection — built into ASP.NET Core, request/response without polling, degrades to SSE/long polling behind awkward proxies. Assumes a single Orbit instance (flagged in §8.2).
    - **Capabilities** announced in the agent's `Hello` make it a general connector: directory sign-in is its first job, and later ones need no change to installation or registration.
    - **Implemented as one override** — `SignInManager.CheckPasswordSignInAsync` — so lockout, two-factor, deactivation and the cookie are stock Identity and identical for both kinds of user.
    - The LDAP code was ported from an existing `LdapService` with five defects fixed rather than copied: unescaped filter input (LDAP injection), certificate validation unconditionally disabled, empty passwords passed to the directory (an unauthenticated bind succeeds on Active Directory), first-match-wins on an ambiguous search, and directory outages reported as wrong passwords (§8.1).
27. **Two behaviour changes that came with item 26, called out because they affect existing users and deployments:**
    - **Account lockout now actually applies.** The stock login page passes `lockoutOnFailure: false`, so failed sign-ins were never counted. Orbit's login page now passes `true`, so wrong passwords lock an account, local or directory. It was needed so Orbit can't be used to guess directory passwords, and it is the right behaviour for local accounts too. (The thresholds — originally Identity's 5 attempts / 5 minutes — were tightened and made configurable in item 29.)
    - **The "one active System Admin must remain" rule became "one active System Admin *with a local password* must remain"** (§6.5) — the break-glass account for when the directory or agent is down.
    - Also: Orbit now honours `X-Forwarded-*` headers from a same-host reverse proxy (§10.2), so the agent's source address and the request scheme are real. As a side effect the MCP URL shown on the API Keys page is now `https://` behind the proxy.
28. **Solution restructured into three sibling projects (§11):** the web app moved from the repository root into `Orbit.Web/` (project and assembly renamed `Orbit` → `Orbit.Web`), and `Orbit.Agent` / `Orbit.Agents.Contracts` came out of the `agent/` folder to sit beside it. The root-level web project had been globbing the agent's sources and needed explicit exclusions; with every project in its own folder those are gone. **Namespaces did not change** — `RootNamespace` stays `Orbit`, so code is still `Orbit.Data`, `Orbit.Application`, etc., matching §11, and the migrations' namespace is untouched. **Deployment consequence:** the entry point is now `Orbit.Web.dll`. An existing server must have its app folder emptied before the new output is copied in, and its systemd `ExecStart` updated — otherwise the old `Orbit.dll` is still there and the service carries on running the previous build without complaint (`deploy/README.md` §2). Data Protection is unaffected because the application name is pinned to `"Orbit"` in code rather than derived from the assembly or path, so production sessions and the encrypted directory bind password survive the rename.
29. **Sign-in hardening after a security review of directory sign-in (§6.5, §8.3, §10.2).** The review found transport and storage sound — TLS on every hop, nothing that authenticates anyone stored in the clear, no password in any log — and the real exposure elsewhere: Orbit is an internet-facing door onto Active Directory. Done:
    - **Lockout made configurable and stricter by default (3 attempts / 30 minutes).** Not primarily to slow guessing, but because every wrong guess at Orbit is a failed bind in AD: with Identity's stock 5-and-5, anyone who knew a colleague's email could have locked their Windows account from the internet. Orbit now locks first, and a locked account never reaches the directory. It applies to local users too (Identity's lockout is global), and because 30 minutes is long, a `SystemAdmin` **Unlock** action was added alongside it.
    - **Per-address throttle on *failed* sign-ins (20 / 15 minutes)**, against password spraying, which per-account lockout cannot see. Built into the login page rather than as rate-limiting middleware so that it can count failures only — successful sign-ins from a shared office address are free, and a directory outage costs nobody their budget. It fails open for loopback addresses, because behind a proxy that doesn't forward the client address a shared budget would let anyone lock the whole company out.
    - **Cookies are always `Secure` outside development**, instead of depending on the proxy forwarding the scheme.
    - **Considered and deferred, with the reasoning kept:** *mandatory two-factor* — a directory bind is password-only and bypasses any MFA on AD, so this is the most valuable next step, but it puts a one-time enrolment and a lost-phone process on every user; it stays opt-in for now. *End-to-end encryption of passwords to the agent* — would defeat a TLS-inspecting proxy on the agent's path, but such a proxy is already inside the organisation's trust boundary, and it adds key management and forces existing agents to re-register.

## 14. Suggested Build Order

1. Data layer: entities, DbContext, initial EF migration against Postgres — including `Department` and the `DepartmentId` FKs on `User`/`Project`/`Task`/`RecurringTaskDefinition`/`ApiKey`.
2. ASP.NET Core Identity setup (Individual Accounts) + roles (`Member`/`DepartmentAdmin`/`SystemAdmin`) + Department CRUD (§6.6) + self-registration disabled + `SystemAdmin` user-creation/deactivation/password-reset flows, including department assignment.
3. Projects CRUD (Razor Pages), department-scoped per §6.1.
4. Tasks CRUD (Razor Pages), standalone + project-linked, department-scoped per §6.2, with comment thread and the Member/DepartmentAdmin/SystemAdmin close restriction enforced server-side.
5. MCP server: `create_task`/`get_task`/`list_tasks`/`update_task`/`add_comment`/`list_comments`/`create_project`/`get_project`/`get_project_status`/`list_projects`/`update_project`/`list_activity`/`list_users`/`list_departments` tools, API key auth with `Role` + `DepartmentId` claim mapping, and the same close/scoping restrictions enforced server-side for `Member`- and `DepartmentAdmin`-role keys.
6. Recurring task definitions + Quartz.NET job to generate instances, department-scoped.
7. Audit log + idempotency on API writes.
8. Notifications: `IEmailSender` call sites for assignment/due-date emails (concrete sender implementation deferred).
9. Reporting: `ReportingService` aggregate queries, Reports Razor Page, QuestPDF export.
10. Backlog & Sprints: Sprint CRUD, backlog view, plan-into-sprint action, sprint board, start/complete workflow with auto-close-and-roll-forward transaction (`SystemAdmin`-only, company-wide).
11. Time Tracking: TimeEntry logging on the task detail page, Start/Stop clock (`RunningClock` + page-leave beacon), per-task/per-project totals, "My time" view.
12. Dashboard: three-tier (Member / Department Admin / System Admin) post-login landing pages pulling from Task/Project/Sprint/Comment/TimeEntry queries, per §6.9.
13. Configuration & deployment: `appsettings.Development.json` for dev; production env-file/systemd setup, Data Protection key ring path, least-privilege DB role script, first-run admin seed (§10).
14. Appearance: light/dark theme on Bootstrap colour modes, navbar toggle, per-browser persistence (§6.11).
15. Day planner: `PlannedFor` migration, Today tick box in the shared task table + Quick handler, "Planned today" filter, Today page with carry-over, dashboard card, `plannedFor` MCP args (§6.12).
16. Directory sign-in and the Orbit Agent (§6.13, §6.14, §8.1–§8.3, §10.3), in this order so each step can be tested before the next: shared contracts project → `User.AuthSource` + `Agent` + `LdapSettings` migration (backfilling existing users as `Local`) → agent authentication scheme, hub, connection registry and the register/de-register endpoints → Admin > Agents with the one-time `configure` command → the agent itself (`configure` / `run` / `remove`, reconnect loop, `Hello`) → Admin > Directory with *Test connection* (proves the whole path before any user depends on it) → the `SignInManager` override, the login page, per-user sign-in method and the break-glass rule → deployment assets (systemd unit, reverse-proxy WebSocket settings).
17. Sign-in hardening (§6.5, §8.3): configurable lockout set against the directory's own policy, the admin Unlock action that a long lockout makes necessary, the per-address failed-sign-in throttle (and the forwarded-header dependency it brings), always-`Secure` cookies. Do this **before** directory sign-in is exposed to the internet, not after.
