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
- Authenticate human users via ASP.NET Core Identity ("Individual Accounts" — local username/password accounts stored in the app's own Postgres database, no external identity provider).
- Authenticate API/machine callers (Claude) separately from interactive users.

## 3. Non-Goals (v1)

- No Gantt charts or billing.
- No mobile app — web only, responsive is a nice-to-have not a requirement.
- No multi-tenant support — this is a single organization's internal tool.

## 4. Tech Stack

| Layer | Choice |
|---|---|
| Backend/UI | ASP.NET Core Razor Pages (.NET 8 LTS recommended) |
| Database | PostgreSQL |
| ORM | Entity Framework Core (Npgsql provider) |
| Auth (human users) | ASP.NET Core Identity — Individual Accounts (local accounts, `IdentityDbContext` in Postgres) |
| Auth (MCP/Claude) | API key, mapped to a `Role` claim (see §8) |
| Claude integration | MCP server via the `ModelContextProtocol` .NET SDK, Streamable HTTP transport, hosted in `Orbit.Web` (see §7) |
| Notifications | ASP.NET Core Identity's built-in `IEmailSender` interface |
| Background jobs | Quartz.NET (`Quartz.Extensions.Hosting`), cron-scheduled jobs resolved from DI (see §6.4, §6.7) |
| Reporting/PDF | QuestPDF (see §12) |

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
- `AssigneeId` (FK → User, nullable — unassigned allowed)
- `CreatedById` (FK → User, nullable — null when created by the API/Claude)
- `Source` (`Manual`, `Api`) — where the task originated, so the team can see what Claude generated vs what a human typed
- `DueDate` (nullable)
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

## 6. Functional Requirements — Web UI (Razor Pages)

### 6.1 Projects
- List all projects with status, task counts (open/total), owner. `SystemAdmin` sees every department's projects; `DepartmentAdmin` and `Member` see only their own department's, filtered server-side (not just hidden in the UI) — plus, read-only and marked *shared*, any other department's project that has tasks filed for their department (§6.2.1).
- Create/edit a project, scoped to the creator's own department (a new project's `DepartmentId` defaults to the creator's department for `DepartmentAdmin`/`Member`, or is chosen explicitly by `SystemAdmin`). Editing rights follow the same Member-vs-Admin split as tasks (§6.5): `Member` edits projects they own, `DepartmentAdmin` edits any project in their department, `SystemAdmin` edits any project anywhere. Moving a project to another department (`SystemAdmin`-only) carries along the tasks and recurring definitions that sat in its previous department; cross-department ones (§6.2.1) keep theirs.
- Project detail page: shows its tasks (filterable by status/assignee, and by department when the project spans several), its recurring task definitions, basic progress (e.g. `12/20 tasks done`) and, on a cross-department project, a per-department open/total breakdown.
- Archive a project (soft — doesn't delete tasks).

### 6.2 Tasks
- List/filter tasks by project, status, assignee, priority, due date, **department**, and **source** (Manual vs Api — useful for reviewing what Claude proposed). `SystemAdmin` sees all departments; `DepartmentAdmin` and `Member` are scoped to their own department by default.
- Create/edit a task, assign to self or team member within the same department (a task can't be assigned to a user outside its own department — see §6.5 assumption flag).
- Change status (ideally a quick inline control, not a full edit form) — see §6.5 for who can close a task.
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
- The project page lists every task on the project regardless of department (as the sprint views already do); tasks from another department are shown without a link and without the inline status control, since the per-task rules still apply. It also shows a per-department open/total breakdown and lets the tasks be filtered by department.
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
| Manage users (create, promote/demote, deactivate, reset password) | ❌ | ❌ | ✅ |
| Manage departments (create/edit departments, assign a user's department) | ❌ | ❌ | ✅ |
| View Reports (§12) | ❌ | ❌ | ✅ |

> **Assumption flagged:** I've kept user management and Reports as `SystemAdmin`-only, matching what you asked for `DepartmentAdmin` ("manage tasks and projects for their own departments" — nothing about users or reports). If you actually want `DepartmentAdmin` to manage users within their own department (create members, reset their passwords) or see department-scoped reports, that's a straightforward extension of this table, just say which.

The `Done`/`Cancelled` status options are hidden or disabled in the UI for Members, and the same rule is enforced server-side in `TaskService` (not just hidden client-side) so a direct request can't bypass it. Department scoping is enforced the same way — every task/project query and write is filtered by the caller's `DepartmentId` server-side, not just hidden in the UI. This same rule applies to the Claude API: each API key carries a `Role` and, where relevant, a `DepartmentId` (§5.1, §8), and is bound by exactly the same rules as a human user of that role.

- Sign in via ASP.NET Core Identity's standard Individual Accounts flow (register/login/manage account pages, scaffolded from the default Identity UI or customized as Razor Pages).
- New accounts default to `Member`, assigned to a department at creation time; a `SystemAdmin` promotes/demotes a user's role and can change their department via a simple admin page.
- **Delete a user account:** `SystemAdmin`-only. Implemented as a soft delete/deactivation (`IsActive = false` or Identity's `LockoutEnd` set far in the future) rather than a hard row delete — a hard delete would orphan the user's `AssigneeId`/`CreatedById`/`AuthorId`/`ActorId` references on existing tasks, comments, and audit log entries. A deactivated user can't sign in, disappears from the assignee picker for new tasks, but their historical task/comment/audit attribution stays intact.

  > **Assumption flagged:** "delete" is implemented as deactivation for the reasons above, not a literal row removal. Flag if you actually want a hard delete (which would mean deciding what happens to that user's existing tasks/comments — reassign, orphan, or block the delete until reassigned).
- **Reset a user's password:** `SystemAdmin`-only. Triggers ASP.NET Core Identity's standard password-reset flow (generates a reset token, either emailed via `IEmailSender` per §6.7 or surfaced as a one-time link/temporary password the admin hands to the user directly) — same mechanism as the existing "Create User" first-login flow below, reused here.
- **Self-registration is disabled.** The scaffolded Identity `Register` page/endpoint is removed (or locked behind `[Authorize(Roles = "SystemAdmin")]`) so the public can't create their own accounts. Instead:
  - `SystemAdmin` has a "Create User" page that creates the `AspNetUsers` row directly — setting `DepartmentId` and `Role`, and setting a temporary password or triggering Identity's password-reset/email-confirmation flow so the new user sets their own password on first login.
  - Login page remains open; only account creation is gated.

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

### 6.10 Time Tracking
- **Log time on a task:** from the task detail page, a user logs a time entry — date, duration, and an optional note. A task can have many entries (e.g. logged across several days).
- **Time logged per task:** task detail page shows a running total and the individual entries.
- **Time logged per project:** project detail page shows total time logged across its tasks, for a quick "how much effort has this actually taken" view.
- **My time:** a simple page (or dashboard widget) showing the current user's logged time, filterable by date range — useful for a weekly personal check rather than a full report.
- Anyone can log time against a task they're assigned to; `DepartmentAdmin` can log or edit time entries for anyone in their own department, `SystemAdmin` for anyone anywhere (e.g. correcting an entry on someone's behalf).

> **Assumption flagged:** kept deliberately simple — a duration + note per entry, not a start/stop timer or billable-rate tracking (billing is a non-goal per §3). Say if you want a running timer (start now / stop now) instead of manual duration entry.

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
| `list_tasks` | List tasks. Args: `projectId`, `departmentId`, `status`, `assigneeId`, `source`, `dueBefore`/`dueAfter`. Paginated. A `DepartmentAdmin`/`Member` key is scoped to its own department regardless of the `departmentId` arg; a `SystemAdmin` key can query any department or omit the filter for all. |
| `update_task` | Full edit of a task — title, description, status, priority, assignee, due date, project link, sprint, and department (`SystemAdmin` keys only; a department other than the project's makes it a cross-department project task, §6.2.1). Setting status to `Done`/`Cancelled` requires a `DepartmentAdmin`- or `SystemAdmin`-role API key (§6.5, §8); a `Member`-role key gets rejected on that field. A `DepartmentAdmin` key is rejected if the task isn't in its own department. |
| `add_comment` | Add a comment to a task (e.g. Claude explaining why it proposed or updated a task). |
| `list_comments` | List a task's comments. |
| `create_project` | Create a project. Args include `departmentId` with the same default/required rule as `create_task`. |
| `get_project` | Get project detail including its tasks, each with its department (`crossDepartment` flags a project that spans several). Visible to the project's department, a `SystemAdmin` key, and any department with tasks filed under it (§6.2.1). |
| `get_project_status` | Lightweight status summary only — task counts by status, overdue, progress, time logged and a per-department breakdown — good for a quick "how's project X doing" check without pulling every task. |
| `list_projects` | List projects with summary status (name, status, open/total task counts). Same department scoping as `list_tasks`, plus other departments' projects that have tasks filed for the key's department (§6.2.1). |
| `update_project` | Edit a project — name, description, status, owner, target date. Same field-level rules as the Razor Pages UI (§6.1); no separate close-restriction applies here since project status isn't gated like task closing is. A `DepartmentAdmin` key is rejected if the project isn't in its own department. |
| `list_activity` | Read-only view of the `AuditLog` (§5.1) — every task/project/comment write, who or what made it (`ActorType`/`ActorId`), and when. Args: `from`, `to` (both optional date/time bounds defining the window — e.g. Claude computes `from = now - 24h` for "show activity from the last 24 hours"; if `from`/`to` are omitted, defaults to the most recent 24 hours), `entityType`, `entityId` (optional, to scope to one task/project). Scoped to the key's own department like `list_tasks`, unless the key is `SystemAdmin`. Lets Claude (or a person asking Claude) answer "what's changed on this task/project recently" without pulling raw DB access. |
| `list_users` | List all users (`id`, `displayName`, `email`, `role`, `departmentId`). Args: optional `query` to filter by name/email fragment (e.g. "Bob"), optional `departmentId`. A `DepartmentAdmin`/`Member` key sees only its own department's users by default (so "assign it to Bob" naturally resolves to the Bob in the caller's own department); a `SystemAdmin` key can query any department. |
| `list_departments` | List all departments (`id`, `name`, `description`). Read-only, available to every role — needed so a `SystemAdmin`-role key can pick a `departmentId` when creating a task/project outside its own scope-free context. |

All of these are read-only except `create_task`, `update_task`, `add_comment`, `create_project`, and `update_project`. `list_users`, `list_departments`, and `list_activity` are available to every role's API key (read-only lookups carry the same risk level as `list_tasks`, not the closing/admin actions gated in §6.5). `update_project` follows the same department rule as `update_task` rather than being open to every role unconditionally.

### 7.2 MCP Authentication

The MCP connection authenticates with the same API key scheme as §8 — Streamable HTTP supports custom headers, so the API key travels the same way (`Authorization: Bearer <key>`) as it would on a REST call. The key's `Role` and `DepartmentId` (Member/DepartmentAdmin/SystemAdmin) still govern what its tool calls are allowed to do, exactly as in §6.5.

## 8. Authentication (Interactive Users vs MCP)

Two different populations hit this system, so two different auth schemes make sense:

- **Interactive users (Razor Pages UI):** ASP.NET Core Identity, cookie-based session, standard Individual Accounts login flow.
- **Claude / machine callers (MCP server, §7):** since Individual Accounts has no external directory to issue machine tokens from, the natural fit is a long-lived **API key** — issued per integration, sent as `Authorization: Bearer <key>` on the MCP connection, validated against a hashed value stored in the `ApiKeys` table (§5.1). This is also the easiest option to wire into a Claude Project's connector config.

  Each key carries a `Role` (`SystemAdmin`, `DepartmentAdmin`, or `Member`) and, for the latter two, a `DepartmentId`, assigned when the key is issued. The same authorization checks that gate role and department scoping for human users (§6.5) apply to the key — e.g. a `Member`-role key can create and edit tasks in its own department but is rejected if it tries to close one or touch another department's data; a `DepartmentAdmin`-role key can close tasks within its own department; a `SystemAdmin`-role key can do both across any department. This means the same policy/handler code path enforces the rule regardless of whether the caller is a signed-in user, an API key on the MCP server, or (if a plain REST surface is ever added later) a REST caller — no separate "is this the API" special case for authorization.

  Implement it as a separate ASP.NET Core authentication scheme (e.g. a custom `AuthenticationHandler` for the API key) alongside the Identity cookie scheme, mapping the key's `Role` and `DepartmentId` into the request's claims so the existing `[Authorize(Roles = "...")]`/policy checks work unchanged for MCP callers.

Either way, the MCP identity should map to a synthetic `User` row (e.g. "Claude Agent") so `CreatedById`/audit trails have something to point at, distinct from a null value.

## 9. Non-Functional Requirements

- **Auditability:** every MCP-originated write is logged (§5.1 AuditLog) — this matters more here than in a typical CRUD app, since an AI agent is writing data unsupervised.
- **Idempotency:** `create_task` should accept an optional client-supplied `IdempotencyKey` argument so a retried Claude call doesn't create duplicate tasks.
- **Performance:** trivial at expected team scale (tens of users, hundreds of tasks) — no special work needed beyond normal EF Core indexing on `ProjectId`, `AssigneeId`, `Status`.
- **Hosting:** production runs on Ubuntu Linux (§10); dev/local runs wherever a developer runs `dotnet run`.

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
- **Optional feature toggles:** background-job settings (whether recurring-task generation or due-date notifications are enabled, their Quartz cron expressions, run-on-startup, notification lead time: `Jobs__RecurringTasks__Cron`, `Jobs__DueDateNotifications__Cron`, etc.) are also environment-variable-overridable, with sane defaults baked into `appsettings.json` so they don't need to be set explicitly.

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
11. Time Tracking: TimeEntry logging on the task detail page, per-task/per-project totals, "My time" view.
12. Dashboard: three-tier (Member / Department Admin / System Admin) post-login landing pages pulling from Task/Project/Sprint/Comment/TimeEntry queries, per §6.9.
13. Configuration & deployment: `appsettings.Development.json` for dev; production env-file/systemd setup, Data Protection key ring path, least-privilege DB role script, first-run admin seed (§10).
