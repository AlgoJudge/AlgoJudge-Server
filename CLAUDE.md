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

- **Where a request came from is recorded, and the address may not be forged**
  (2026-08-23), specified in `docs/specs/ORIGIN_METADATA.md` in the workspace.
  Four things about it are easy to get wrong:
  - **An installation that names no trusted proxy does not start.** The second
    such rule after storage, and for the same reason there is no default:
    trusting every sender of `X-Forwarded-For` lets a visitor state their own
    address, and trusting only loopback silently records the proxy in a container
    network. `Forwarded__KnownProxies=none` is a full answer for a Server reached
    directly.
  - **An address is `inet` and un-mapped before it is stored.** The question
    asked of it is containment in a network, which over text is a comparison of
    spellings — and Kestrel on a dual-stack socket hands back
    `::ffff:10.0.5.17`, which PostgreSQL calls family 6, so `<<=` against any
    IPv4 network is silently `false`. `Services/RequestOrigin` is the one place
    that normalises it.
  - **It is not hashed, and that was decided rather than skipped.** A hash cannot
    answer a subnet question; a keyed one is pseudonymisation and removes no
    obligation; an unkeyed one of an IPv4 address is reversible in seconds.
  - **A submission's origin rides `submission:read:all`** — already scoped per
    activity — and is on the detail, never the list. `Workers/AddressSweeper`
    clears a session's after 30 days and a submission's after 365, keeping the
    row; erasure clears both.

- **Several people may compete as one** (2026-08-23), specified in
  `docs/specs/GROUPS.md` in the workspace. Five things about it are easy to get
  wrong:
  - **A submission stamps its group when it is made and never afterwards.** A
    manager may move somebody at any time; that changes what happens next and
    nothing that already happened, so a board read an hour ago still reconciles
    with the board now. Deriving the group through the grant at read time would
    move points that were already scored.
  - **The stamp comes from the grant, never from the request.** "If the user is
    in a group, sending as the group is compulsory" is a rule about what happens,
    not a default a form is asked to keep.
  - **One allowance per contestant, and the ungrouped half is the subtle one.**
    In a group it counts that group's stamped submissions; outside one it counts
    what the person sent *while not in a group*. `Services/Contestant` owns the
    rule, because the ceiling and the figure on the screen are computed by
    different services and would otherwise disagree.
  - **A group is a contestant and its members are not.** The ranking has always
    been abstract over that; what a member gets is `Me` pointing at the group,
    or their own row never highlights.
  - **A system group still submits and still spends.** It is excluded from
    *results*, the way `Grant.IsSystem` excludes staff — one level up.

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
