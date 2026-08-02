# AlgoJudge-Server

## Scope

Central persistent state, REST, WebSocket, domain authorization,
activities, tasks, submissions, `EvaluationJob`, files, and results.

## Expected technology direction

- .NET / ASP.NET Core
- Entity Framework Core
- PostgreSQL
- OpenAPI
- Docker for the development environment if confirmed by the repository

After cloning, inspect the solution and project files, then document:

- restore,
- build,
- format,
- unit tests,
- integration tests,
- migrations,
- dependency startup.

## Rules

- The Server does not compile or execute code.
- The Server does not implement a sandbox or checker.
- The Server does not require one concrete execution engine.
- Task semantics must not require Server changes.
- Job reservation must be atomic.
- Runner result submission must be idempotent.
- REST is the persistent source of state.
- WebSocket publishes events and notifications.
- LMS grade export must be separated from persistent result storage.

## Decisions in force (2026-08-02)

- **Identity stays in the Server for the MVP.** ASP.NET Identity and the
  `/identity/*` endpoints remain, including password storage. "The Server does
  not store user passwords" is the **target**, deliberately suspended for now,
  not a rule to enforce against the current code.
- **`EvaluationJob` is deferred as an entity.** The Runner linkage lives on
  `Result`, which names the Runner that is evaluating or has evaluated a
  submission. Because it must name a Runner while evaluation is in progress,
  `Result` is created at claim time and doubles as the job record. Atomic
  reservation, leases and idempotency still apply to it.
- **All identifiers are string UUIDs.** The entities still use `int` keys; the
  migration is outstanding.
- `Activity.Type` is the type discriminator, formatted `name@version`. No
  separate `typeId` and `typeVersion` columns.
- `main` is the integration and default branch. `devel` no longer exists.

## Layout

The frontend is in
[AlgoJudge-Client](https://github.com/AlgoJudge/AlgoJudge-Client). A duplicate
copy sat under `algojudge-client/` here until 2026-08-02, when it was verified
as outdated and removed.

**Do not go looking for that code in this repository's history.** It was
migrated to `AlgoJudge-Client` with its commits — that history reaches back to
December 2023 and carries the contributors who worked on it. The commits here
stop on 2026-08-02 and are not where the work continued.

## Working here

Build and run instructions are in `README.md`. When this repository is checked
out inside the AlgoJudge workspace, `../PROJECT_CONTEXT.md` is the primary
architecture context and takes precedence over this file.
