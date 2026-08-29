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
  **Since 2026-08-27 there is exactly one `int` key**, `DataProtectionKeys.Id`,
  and it is the framework's table rather than this product's model — the same
  exception as `User.Id`, for the same reason.
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

- **A running series may put the rest out of reach** (2026-08-24), specified in
  `docs/specs/SERIES_LOCKDOWN.md` in the workspace. Two filters, and **neither is
  a permission** — they are applied after authorization, because the model has no
  subtraction in it. Five things are easy to get wrong:
  - **Place and rank are different.** A round with `SeriesAddressRule` rows is
    served inside and **absent** outside; a round below the floor is **locked**
    and names what displaced it. Absent withholds its dates and its count;
    locked says "not now".
  - **The floor is a maximum, so equal ranks survive together** — which is how
    two contests share one room. And it **follows the grant**: only a round
    somebody takes part in can displace anything.
  - **An address the Server cannot read admits nobody and locks nobody.** The
    second half is what keeps a proxy failure from stopping every course at once,
    and nothing is gained by stripping the header.
  - **`Services/FileService` was a live hole.** A statement is authorised through
    *any* activity holding its version, so a locked round's problem was reachable
    through whichever open course also held it. The narrowing is per **round**.
  - **Two switches, either lifting both filters and keeping the configuration**:
    `Series.RestrictionsEnabled` and `Instance.SeriesRestrictionsEnabled`.
  - **Amended the same day**: `Series.ImportanceScope` says how far a rank
    reaches — `activity`, the default, or `installation`. There are two floors
    per reader and the higher applies, the global one winning a tie. An
    activity-scoped round can never lock its own activity, so it produces locked
    **rounds** and never a locked card — which is why `ResultsService`, the
    submission list and `QuestionService` moved from activity granularity to
    round granularity through `UnreachableRoundsAsync`, and why the ranking's
    clause sits **outside** `unfrozen`: that permission lifts a freeze and must
    not lift a lockdown.

- **`ServerFixture` switches the background workers off, and one escaped for a
  while** (2026-08-24). It removes them **by registration shape** — factory
  registrations — because the framework's own HTTP service is registered by type
  and removing every `IHostedService` would take the server down.
  `GradeSyncWorker` uses `AddHostedService<T>()`, so it was missed and swept the
  shared test database from every host a test built; the tests that call it
  themselves then raced with it and failed about one full run in two. It is now
  removed by type as well, with its own count assertion. **A worker added to this
  Server must be switched off here, whichever way it is registered.**

- **One account's work may be carried onto another** (2026-08-24), specified in
  `docs/specs/ACCOUNT_MERGE.md` in the workspace. Four things about it are easy
  to get wrong:
  - **What a person produced moves; what they did to somebody else's thing
    stays.** A manager's exclusion, a grant they handed out, an answer they
    wrote — moving those would say somebody else made that decision.
  - **`File.UploadedByUserId` moves, and looks like it should not.** It is not
    an audit trace: a file nothing references yet is readable only by its
    uploader, which is what makes the two-step publish safe.
  - **A system grant never moves, and an account holding one is refused.**
    Grants move with the work, so otherwise anybody holding `user:merge` merges
    an administrator into their own account and inherits their permissions.
  - **It ends in anonymisation, never a delete.** `AUTHENTICATION.md` settled
    that before this existed and **nothing here hard-deletes a user row** — the
    rows recording what an account once did have to keep resolving.
    `MergeSweeper` empties it a day later, and until then an undo gives it back
    whole.

- **An account past `ExpiresAt` stops working too** (2026-08-24). Computed on
  every request beside the block, and refused at sign-in by
  `Authorization/ExpiringSignInManager`. **Never written as a lockout**: a
  manager and the clock on one field disagree silently both ways — unblocking
  would defeat the expiry, extending the date would leave a stale block. The
  refusal carries `account.expired` rather than `account.blocked`, because the
  manager's screen has told the two apart since before either was enforced.

- **A Runner is reserved by tagging it** (2026-08-24), specified in
  `docs/specs/RUNNER_ROUTING.md` in the workspace. A Runner carries tags, so does
  the work, and they are paired when the two lists **share at least one** —
  unlike GitLab, whose runner must hold every tag a job asks for. Tags are pools
  rather than requirements here because capability is already answered by
  `ProblemTypes` and `External`. Four things are easy to get wrong:
  - **An empty list means `default`, on both sides, and that is the whole of the
    exclusivity.** Tagging a Runner takes it out of the general pool and tagging
    work takes it away from the general Runners — neither half had to be written,
    and neither can be forgotten. It is also why the migration is a no-op.
  - **There are two claim paths.** `TrialService` hands out package measurements
    on its own table, and a reservation that covered only `RunnerService` would
    leave a Runner held for an examination timing somebody's packages while it
    ran. A trial with no activity is general work.
  - **A round overrides its activity, and `null` is not `[]`.** Null inherits;
    a round wanting the general Runners while its course is pinned writes
    `default` out, so one meaning keeps one spelling. Nothing stores an empty
    override.
  - **A Runner seeds its tags at its first registration and never again.** Every
    other field it reports is refreshed on re-registration, which is how a
    restart is reported; this one is not, because a Runner that could re-declare
    its tags would put itself into an examination's pool with nobody approving
    it.
  Also: the pool clause is read at claim time rather than stamped on the job, so
  retagging redirects work already queued; and `ProblemTypes` had driven dispatch
  since it was written with **no test proving it** — that one is now the first
  test in `RunnerRoutingTests`.

- **A blocked account stops working now** (2026-08-24).
  `Authorization/BlockedGate` is a per-request check, because `LockoutEnd` is
  read at sign-in only: a blocked person used to carry on until Identity next
  revalidated their cookie, thirty minutes by default. **A rule about what an
  account may do belongs in the pipeline, not in the sign-in path.**

- **A manager may rule that one submission does not count** (2026-08-24),
  specified in `docs/specs/EXCLUDED_SUBMISSIONS.md` in the workspace. One column
  is the whole fact — `Submission.ExcludedAt`, non-null means excluded — and four
  things about it are easy to get wrong:
  - **It rules on a result and retracts nothing.** The verdict, the attempts, the
    files, the place in every list and **the ceiling it spent** all stay.
    `Services/Contestant` deliberately does not read it: filtering there would be
    a second, invisible way to move a limit.
  - **Five readers stop counting it**, and they are the ones the code already
    enumerates: `Scoring.BestOf` (which covers the problem page, the round list
    and the socket's push), `ResultsService` twice, and `GradeSyncService`.
  - **A board already open is not repaired by silence.** The Client merges an
    arriving result by id and no merge removes a row, so the write sends
    `rankingChanged` with `change: "excluded"` and no result, and every reader
    refetches.
  - **The gradebook needed more than a filter.** Dropping the submission left the
    contestant out of the computation, and a row nobody computes is a row nobody
    corrects — so a contestant who stops earning is carried back in at **zero**.
    The reason is cleared on erasure while `ExcludedAt` stays, and it travels in
    the participant's own data export.

- **The keys that encrypt a session cookie live in the database** (2026-08-27),
  specified in `docs/specs/AUTHENTICATION.md` §10 and decided in
  `AlgoJudge-Design/adr/DATA_PROTECTION_KEY_RING_2026-08-27.md`. Nothing called
  `AddDataProtection()` before, so the framework built a ring local to the
  process: every restart signed everybody out, and a second instance could not
  read the first's cookie. `Authorization/KeyRing.cs` is the whole of it —
  `DataProtection:Kind`, `database` by default, `ephemeral` refused outside
  Development, Redis refused by name. Four things are easy to get wrong:
  - **`SetApplicationName` is load-bearing and looks decorative.** The
    discriminator falls back to the content root, so two containers built from
    different paths silently do not share a ring while sharing everything else.
    It is fixed in code because changing it signs everybody out.
  - **A cookie test on one machine does not prove the store.** The framework's
    default ring persists to a directory under the profile, which two hosts in
    one test process share exactly as they would share a table — the sabotage
    caught this. `The_ring_follows_the_database_and_not_the_machine` is the test
    that discriminates, by giving one host a database of its own.
  - **`/identity/manage/info` is not a "am I signed in" endpoint here.** It
    throws for an account with no address, and this product has those on purpose
    — the seeded administrator is one. `GET /api/v1/account` is the product's
    own answer.
  - **The certificate list rotates by prepending.** The first encrypts, all of
    them decrypt; dropping the old one makes existing keys unreadable, which
    looks exactly like having no ring at all.

- **An installation can be stood up from files on disk** (2026-08-28), specified
  in `docs/specs/PRECONFIGURATION.md` in the workspace. `Preconfiguration/` is
  the whole of it — a YAML file, `pages/*.md` and a mark, read from
  `AJ_Preconfiguration__Path`. Five things are easy to get wrong:
  - **It applies at the first start and never again on a boot.** "Fresh" is
    checked **before the seeder runs**, because the seeder creates both halves
    of the test itself; and it is two conditions, no `Instance` row **and** no
    user, because a dump older than that table has no row either. A file
    re-read on every start would silently undo the panel.
  - **It adds and never withdraws.** An absent key means *leave alone*, not
    *reset*, and a document the directory does not carry stays published. The
    same reading `InstanceSettingsInputDto` already gives an absent field.
  - **The comparison is SHA-256 against what is published**, and there is no
    state row and no migration. Publishing *adds* a revision, so an apply that
    republished what it found would grow a privacy policy's history on every
    run — which is the history the versioning exists for.
  - **`aj-admin` cannot do this work.** The image has no `curl`, no `wget` and
    no `jq`, so the directory is read by the Server from a mount and the command
    is a trigger. That is why it is an endpoint rather than a subcommand.
  - **YamlDotNet is the first third-party parser here**, on one path that never
    touches a request body. YAML rather than TOML because the product had
    already chosen it twice, for `config.yml` and for statement front matter.

- **The concurrency tokens are called `RowVersion`, and there are eight**
  (2026-08-28). Four were renamed from `Version`, which collided with two
  genuine version *numbers* in the same model — `ProblemVersion.Version` and
  `Runner.Version` — and four are new. All eight map to PostgreSQL's `xmin`
  system column. Five things are easy to get wrong:
  - **`Runners` deliberately has none, and must not gain one.** `LastSeenAt` is
    written on every claim, renewal and report, so a row-level token there makes
    a Runner collide with itself on ordinary traffic — the first attempt at this
    reddened nineteen tests. Approving against a revocation is closed by a
    **compare-and-set** in `ManagerReadService.ApproveRunnerAsync` instead:
    the condition rides the `UPDATE`, so there is no window and no cost.
  - **A token only earns its place on a read-decide-write.** EF writes just the
    columns it tracked as modified, so two writers on *different* columns never
    erased one another. What needed guarding was the state machines:
    `AccountMerge`, `AccountDeletionRequest`, `StorageMigration`, and `Instance`
    — which gained a second writer on 2026-08-28 when pre-configuration landed.
  - **A conflict answers 409 with the path's own code**, never a 500.
    `Utils/Concurrency.SaveAsync` re-reads and runs the guard again, so the loser
    gets `deletion.notPending` or `merge.window.closed` — what it would have got
    had it read a moment later. An unhandled one is a 500, which is what
    `RunnerService.ExtendAsync` was written to stop.
  - **Both sweepers were already atomic, and that is why they needed no
    reordering.** `AnonymiseAsync` writes nothing of its own — it moves tracked
    entities — so the emptying and its marker land in one `SaveChanges`. An undo
    or a halt that committed first therefore stops the account being emptied
    rather than merely losing a marker.
  - **The migration runs no SQL.** `xmin` is a system column that every table
    already has, so Npgsql drops the `AddColumn` operations and writes only the
    history row. Verified with `dotnet ef migrations script` before it was
    applied anywhere; the rename contributes nothing at all, because only the
    property name changed.

- **The schema is one migration per context** (2026-08-28), squashed before
  0.1.0 while no installation had a database to carry forward. Thirty-one
  migrations became `InitialCreate`, seven became `LtiInitialCreate`.
  - **One block is hand-written, and a regeneration loses it.** `FileContents`,
    at the end of `InitialCreate`: it is not an EF entity — the postgres blob
    store reads and writes it with raw SQL — so `dotnet ef migrations add` does
    not produce it, and neither the table nor its `SET STORAGE EXTERNAL` comes
    back on its own. `FileStorageSchemaTests` is the guard; proved by deleting
    that one `ALTER TABLE` line and watching `attstorage` go from `e` to `x`.
  - **Everything else the old chain carried was backfill** — rewriting rows a new
    database does not have — or shaped a column into what the model already
    declares, such as the `inet` conversion of `UserSessions.IpAddress`.
  - **Eleven database defaults were dropped on purpose, and must not be put
    back.** They were scaffolding from `AddColumn(defaultValue: …)`, never in
    the model, and each is matched by a CLR initializer. Declaring them with
    `HasDefaultValue` would be worse than losing them: EF omits a property whose
    value equals the CLR default, so an explicit `ShowLocalSignIn = false` would
    be stored as `true`.
  - **A database from before the squash cannot cross it**: its history names
    migrations that no longer exist, so the next start tries to create tables
    that are already there. Development stacks are disposable — `down -v`.
  - **Verified by diffing two schemas**, not by reading the generated file: the
    full old chain and the squashed pair were applied to two databases and
    `pg_dump --schema-only` compared. Once column order is normalised the only
    differences are the eleven defaults above.
  - **It found a stale snapshot.** `ApplicationDbContextModelSnapshot.cs` still
    declared `Runner.RowVersion` — the token that was tried and taken off the
    same day — because removing a property does not regenerate the snapshot.
    Nothing read it at run time, but the next `migrations add` would have opened
    with a drop of a system column. **Regenerate the snapshot after removing a
    mapped property**, or the next migration carries the ghost.
    `MigrationsDescribeTheModelTests` has asserted this since 2026-08-29.

- **.NET 10 since 2026-08-29**, `net10.0` with `aspnet:10.0` and `sdk:10.0`.
  .NET 8 leaves support on 2026-11-10; 10 is the LTS.
  - **Two framework behaviours changed under the product, and tests caught both
    — nobody read a release note.**
    - **`IPNetwork.TryParse` accepts host bits now**, and silently normalises:
      .NET 8 refused `10.0.5.17/24`, .NET 10 makes it `10.0.5.0/24`. A typo
      meaning one machine becomes a laboratory, so `SeriesService` compares the
      written address against `BaseAddress` itself. The rule is unchanged; what
      enforced it left the framework.
    - **Revoking the key ring stopped signing anybody out of the running
      process.** .NET 10 refreshes the ring in the background, so the instance
      the operator typed into keeps serving the cached one. `KeyRing.Add` sets
      `DisableAsyncKeyRingUpdate`; the cost is that a refresh is synchronous
      again, and one happens on revoke, on rotate and once a day.
  - **Two package moves were forced, not chosen.** Swashbuckle 6.5.0 → 10.2.3,
    the first release with a `net10.0` group, which also meant porting
    `Api/MultipartFormDocumentation.cs` to OpenAPI.NET v2. And
    `Microsoft.IdentityModel.*` 8.0.2 → 8.19.2, because ASP.NET Core 10's
    OpenIdConnect depends on 8.19.2 and anything lower is an NU1605 downgrade.
  - **`openapi.json` grew 749 leaves without the contract changing**: 157 paths
    and 189 schemas before and after, nothing removed. Swashbuckle 6 dropped the
    C# `required` modifier and 10 honours it, so the document is more faithful
    rather than different.
  - **Warnings went 5 → 15, and none was fixed here** — the owner asked for them
    measured rather than cleaned. CI and a local build report the **same fifteen
    lines**; a local MSBuild summary says sixteen only because it counts the
    restore advisory once per project. Every new one is a deprecation:
    `ASPDEPR005`
    ×6 for `KnownNetworks` and `IPNetwork`, `CS0618` ×2 for `NpgsqlCidr`,
    `SYSLIB0057` for the `X509Certificate2` constructor, and **`NU1903` for
    SSH.NET 2023.0.0** — which nothing pulled in that day: the .NET 10 SDK
    audits transitive packages where .NET 8's audited direct ones only, so
    `Testcontainers.PostgreSql` 3.10.0 had been carrying it unreported. **The
    `NpgsqlCidr` converter is the one this upgrade was written to delete**; that
    is a model change and wants its own step.

- **The suite runs in 2 m 10 s, and it took 4 m 49 s until 2026-08-29.** Nothing
  was deleted and nothing was skipped: 640 tests before and after.
  - **A collection is xUnit's unit of serialisation, and fifty classes sat in
    one.** `[Collection("server")]` shared one fixture, so 476 tests — 192 s of
    work — ran strictly one at a time. They are now three collections with a
    database each; inside a group the old rule is untouched.
  - **`DisableParallelization` means more than it reads**, and it was the larger
    half. It does not serialise a collection internally — being one collection
    already does that — it takes the **whole runner**. Measured on a timeline:
    the storage suites did not start until **113 s** into a 199 s run and added
    **86 s to the end**. Removing it was a one-word change worth more than the
    split.
  - **One test genuinely cannot share a process**: `MemoryTests` reads
    `GC.GetTotalAllocatedBytes`, which is process-wide, so three other Server
    hosts allocate inside its measurement. It failed exactly once when the
    storage collection was freed — 45 MiB against a ceiling of 32 — and now has
    a collection of its own with the exclusivity it needs. **11 s bought where
    it is needed, instead of 86 s bought everywhere.**
  - **Measured, not guessed, at every step.** The groups are bins filled from a
    per-class timing run; `fsync=off` on the test database was tried and
    **reverted because it changed nothing** — 3 m 19 s either way, so the
    bottleneck was never durability. Four consecutive green runs before this was
    committed, because the storage collection's history is a flake.

- **Every dependency swept to its latest stable on 2026-08-29**, except where a
  measurement said otherwise. The restore is clean again: **NU1903 is gone**,
  because `Testcontainers.PostgreSql` 4.14.0 carries a fixed SSH.NET — the
  advisory and the upgrade were the same piece of work.
  - **Already current, and checked rather than skipped**: `Microsoft.AspNetCore.*`
    and EF Tools at 10.0.11, Npgsql 10.0.3, Swashbuckle 10.2.3, YamlDotNet
    18.1.0. Everything above them on NuGet is an `11.0.0-preview`.
  - **`Microsoft.IdentityModel.*` 8.19.2 → 8.22.0 reverses yesterday's rule.**
    The `.csproj` said "never below what the handler resolves" and meant it as a
    ceiling too; the owner made it a floor only. ASP.NET Core 10.0.11 asks for
    8.19.2, so this is deliberately three minors above it, and what covers the
    sign-in path is `FederatedSignInTests` plus the LTI suites.
  - **`coverlet.collector` was removed, not upgraded.** Nothing ever collected
    coverage — no collector argument in CI or in any script.
  - **Testcontainers 4 moved two things.** `UntilPortIsAvailable` split into an
    internal and an external half (this waits on the container's own port, so
    the internal one), and the parameterless builders are obsolete — the image
    now goes in the constructor, which puts the tag next to the builder that
    uses it.
  - **The new test runner reordered classes and found a real bug.** Nine tests
    in `TrialTests` and `TrialRunTests` failed with *relation does not exist*:
    they open a `DbContext` before anything starts the host, and **the host is
    what migrates**. It had been survivable by accident, because some other
    class in the collection made a request first. `ServerFixture` now migrates
    once during `InitializeAsync`, which removed the ordering assumption from
    every suite and let two duplicated warm-up helpers be deleted.
  - **`chrislusf/seaweedfs` is pinned to 4.43, and 4.44 is newer.** On 4.44 the
    S3 gateway answers "We encountered an internal error" and three tests fail;
    4.43 passes all sixteen, and the 4.41 it replaced failed the control test.
    **Newest is not the same as working**, and the reference-implementation
    suite is what says which — it is gated behind `ALGOJUDGE_S3=seaweedfs` and
    skips by default, in CI included, so this needs running by hand.
  - `rustfs` went `1.0.0-rc.1` → `rc.4`; there is still **no stable 1.0.0**.
    `postgres:18` is unchanged: there is no 19, and the major pin is deliberate.
  - **Warnings 15 → 14.** The nine the bump introduced were fixed because the
    bump introduced them; the fourteen that predate it are still measured and
    not fixed, per the standing decision.

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
