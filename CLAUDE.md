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
- **Identity phase 2 was specified 2026-08-09 and accepted 2026-08-10** —
  `AlgoJudge-Design/adr/IDENTITY_PHASE_2_DECISIONS_2026-08-09.md`, indexed in the
  workspace under *Identity phase 2*. Embedded Identity **stays permanently** for
  administrator, local and temporary accounts; the Server *gains* several OIDC
  providers registered from the database. Four things about it change code that
  already exists here, so they are worth knowing before touching any of it:
  - a **`UserIdentity` is unique on `(providerId, subject)`** and never keyed on
    an email address;
  - **system-scope permissions become a union of contributions** — one manual,
    one per linked provider — so the unique index that allowed exactly one system
    grant per user has to go;
  - an activity grant gains an **override flag**, and **nothing subtracts
    anywhere in the model**;
  - `SessionDto.IsLocal` stops being a hard-coded `true`, and the rule "an SSO
    account may change none of its own fields" moves out of the Client's disabled
    inputs and into this API, where it was always described as living.
- **`EvaluationJob` is deferred as an entity.** The Runner linkage lives on
  `Result`, which names the Runner that is evaluating or has evaluated a
  submission. Because it must name a Runner while evaluation is in progress,
  `Result` is created at claim time and doubles as the job record. Atomic
  reservation, leases and idempotency still apply to it.
- **All identifiers are UUIDs, and the entities now hold them.** `Guid` keys
  throughout `Database/Models`, defaulted from `Uuid.New()`; the only `string`
  key is ASP.NET Identity's `User.Id`, which is a UUID in a string column
  because the framework declares it that way. **This line used to say the
  migration was outstanding** — it was done, and the note outlived it. Checked
  2026-08-10: 41 `Guid` and 16 `Guid?` key or foreign-key properties, no `int`.
- `Activity.Type` is the type discriminator, formatted `name@version`. No
  separate `typeId` and `typeVersion` columns.
- `main` is the integration and default branch. `devel` no longer exists.
- **File storage is a choice an installation makes** (2026-08-13), specified in
  `docs/specs/FILE_STORAGE.md` in the workspace. Bytes left `Files.Content` and
  live behind `IBlobStore`, with three backends — `postgres`, `filesystem`,
  `s3` — and a deployment may configure several stores, including several of one
  kind. Six things about it are easy to get wrong later:
  - **An installation that configures no storage does not start.** The default
    store id is `objects`; there is no synthesized fallback any more.
  - **`File.StorageId` names a store, not a kind**, and is permanent once a row
    holds it. A read follows its own row, so there is never a global switch-over.
  - **Nothing materializes a whole file.** Uploads are read with
    `MultipartReader` and hashed in one pass; downloads stream. `MemoryTests`
    measures this and a regression to buffering trips it.
  - **The blob is placed by the checksum the Server computed**, never by the one
    a caller declared — which is why `IBlobStore.WriteAsync` takes an id rather
    than a `BlobKey`, unlike §4 of the specification.
  - **`FileContents` has no foreign key to `Files`**, deliberately: bytes are
    written before the row that names them, and a key would forbid that. §6 of
    the specification draws one; §6.1 concedes the invariant cannot be a
    constraint.
  - **No public answer names a store, backend, bucket or path.** `/health` says
    one word; `/admin/storage` carries the detail, behind loopback and a token.

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
