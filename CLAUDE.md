# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Orbit is a task/project tracker and asset register: ASP.NET Core Razor Pages on .NET 10, EF Core + PostgreSQL,
ASP.NET Core Identity, an MCP server for Claude, Quartz.NET jobs and QuestPDF reports. `README.md` covers the
feature set, the permission table and configuration. `orbit-spec.md` is the full specification and the source of truth.

## Commands

```
dotnet build Orbit.slnx
dotnet run --project Orbit.Web                        # needs Orbit.Web/appsettings.Development.json (gitignored) with the Postgres connection string
dotnet test Orbit.slnx
dotnet test Orbit.Tests --filter "FullyQualifiedName~CriticalPathEngineTests"   # one class; use ~ClassName.MethodName for one test
dotnet tool restore                                   # once, pins dotnet-ef 10.0.12
dotnet ef migrations add <Name> --project Orbit.Web   # run from the repo root
dotnet run --project Orbit.Agent -- help              # the on-premises agent's CLI (configure / run / remove)
```

- Startup applies pending migrations automatically (`Database:ApplyMigrations`, default true) and seeds roles, the
  synthetic Claude user and the first admin (`DbInitializer`). No separate `database update` step is needed in dev.
- If an `Orbit.Web` process is running (e.g. from the IDE on port 5016/7179), it locks `bin/Debug` and `dotnet build`
  fails with MSB3027. Build and test with `-c Release` instead of killing it.
- `Orbit.Agent` reads and writes its credential in `agent.json` beside the executable. Never run `configure` in
  someone's `bin/` folder; set `ORBIT_AGENT_HOME` to a scratch directory for tests. The credential can't be recovered.
- Tests are xunit and cover pure code only (access rules, role rules, scoping, scheduling, asset rules). There are
  no database or integration tests. Verify DB-touching changes by running the app.

## Solution layout and namespaces

`Orbit.Web` is one project whose folders stand in for layers (`Data/`, `Application/`, `Auth/`, `Mcp/`, `Jobs/`,
`Agents/`, `Pages/`). `RootNamespace` is `Orbit`, so namespaces are `Orbit.Application.Services`, `Orbit.Data.Entities`
and so on, **not** `Orbit.Web.*`. Match this in new files. `Orbit.Agents.Contracts` is deliberately plural: a
namespace `Orbit.Agent` visible to the web app would shadow the `Agent` entity.

## How a request is handled

Razor Pages and MCP tools are thin. Both call the same scoped services in `Orbit.Web/Application/Services/`, and
**all authorization lives in the services**:

- Each service gets the caller from `IActorProvider.GetAsync()`, which returns an `Actor` (user, API key or system)
  holding a role's permissions, each granted at a `PermissionScope` (None < Own < Department < All). `HttpActorProvider`
  re-reads grants from the DB on every request. Background jobs pass `Actor.System` explicitly.
- Object checks go through `AccessPolicy` (`AccessPolicy.Require(AccessPolicy.CanEditTask(actor, task), "...")`).
  List queries go through `Scoping.Tasks(q, actor)` etc. so they are filtered in SQL.
- The page-door policies in `Program.cs` (`Policies.Permission(...)` on folders/pages) only check that the permission
  is granted at *any* scope. They are not a substitute for the service check.
- Services throw `NotFoundException`, `ForbiddenException` or `ValidationException` (all `OrbitException`).
  `OrbitExceptionPageFilter` turns these into a 404, a Forbid, or a TempData flash plus redirect. `OrbitTools.Run`
  turns them into `McpException` so Claude sees the message. Pages derive from `OrbitPageModel` and use
  `Success(...)`/`Error(...)` for flash messages.
- Every write is audited in the same unit of work: `audit.Add(actor, AuditEntity.X, id, AuditAction.Y, departmentId,
  summary, details)` queues the row and the caller's `SaveChangesAsync` persists it. `ChangeSet` builds field-level
  `{from,to}` diffs for the details.

## Adding or changing things

- **A permission** touches `Permission.cs` (key), `PermissionCatalog` (label, group, allowed scopes), `DefaultRoles`
  (Member / Department Admin grants), `AccessPolicy` and/or `Scoping`, page conventions in `Program.cs`, and
  `Orbit.Tests/Access`. Shipped roles are seeded once and never re-applied, so existing databases get the new default
  grants only through a data step in the migration (see `AddAssets`). The built-in System Administrator gets every
  catalogue entry automatically.
- **An MCP tool** is a method on `Orbit.Web/Mcp/OrbitTools.cs` wrapped in `Run(async () => ...)`. Arguments are
  mostly strings parsed with the file's `ParseGuid`/`ParseDate`/`ParseEnum`/`TaskIdAsync` helpers, so a bad value
  gives a readable error (a schema or binding failure only reaches the client as a generic "An error occurred").
  Conventions: ids also accept human numbers (`T-26-00012`, ERP asset numbers), `"none"` clears a field, writes
  pass `TaskSource.Api`, and `create_task`/`create_asset` take an `idempotencyKey`. API keys act as the synthetic Claude user
  (`WellKnownIds.ClaudeAgentUserId`). When the tool set changes, also update `ServerInstructions` in `Program.cs`
  and the tool list in `README.md`.
- **Entities/migrations:** every enum is stored as its name (a convention at the end of
  `ApplicationDbContext.OnModelCreating`), so raw SQL uses `'Todo'`, not `0`. Recent migrations were tested on an
  empty DB and on a DB at the previous migration, forward, back (`Down`) and forward again (spec §13 item 37).
- **Pure rules** (`Application/Scheduling/*`, `Application/Assets/*Rules`, `RoleRules`, `DependencyRules`) have no
  DB access so they can be unit-tested. Put new domain logic there when it can be separated. Tests cite the spec's
  acceptance IDs (e.g. `AST-005`) in their doc comments.
- The critical path analysis is stored and marked stale by a hash of its inputs (`ScheduleFingerprint`), not by
  write hooks. A new field that affects scheduling must be added to the fingerprint.

## Spec and documentation conventions

- Code comments and docs cite spec sections (`§6.5` roles and access, `§6.15` subtasks/dependencies, `§6.17` critical
  path, `§6.19` assets, `§7` MCP, `§8` auth). Read the relevant section before changing behaviour.
- A feature change updates `orbit-spec.md` along with the code: the functional section, a numbered entry in
  **§13 Decisions Log** (what was decided and why) and a step in **§14 Suggested Build Order**. User-visible changes
  also go in `README.md`.
- Prose and identifiers use British spelling (organisation, catalogue, colour, authorise).
- Line endings are mixed on disk (`core.autocrlf=true`: LF in the index, CRLF in files an IDE has touched). Keep each
  file's existing endings when editing. Check with `git ls-files --eol <file>`.

  Always keep the orbit-spec.md specification file up to date after implementing changes.