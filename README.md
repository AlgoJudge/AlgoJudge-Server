# AlgoJudge Server

Persistent state, REST API, WebSocket, authorization, activities, problems,
submissions and results for [AlgoJudge](https://github.com/AlgoJudge).

The Server is deliberately simple. It stores files, metadata, grants and
results, and moves data between the Client and the Runners. It never compiles or
executes submitted code, and it holds no knowledge of what a particular problem
type means.

The domain term is **`Problem`**, never `Task` — renamed 2026-08-03, and the code
always used `Problem`; only the documentation lagged.

## Status

> **Rewritten 2026-08-10.** Everything this section said had stopped being true:
> it described three endpoints, one migration named `20240130140424_InitialCreate`
> that no longer exists, and listed as "not implemented" the WebSocket, the
> Runner registry, job reservation, file upload and result payloads — all of
> which ship. It also said authorization only checks that a request is
> authenticated, which is the most misleading thing a README can be wrong about.
> The state below was read off the code and the test run on 2026-08-10.

`Verified fact` — `main`, inspected 2026-08-10.

| Area | State |
|---|---|
| API | **118 controller actions**, all under `/api/v1` (`UsePathBase`), plus what `MapIdentityApi` adds under `/identity` |
| WebSocket | served at `/ws`; the event catalogue is committed as `events.json`, so both sides can diff their names against it |
| Authorization | a real permission model: **48 keys**, grants scoped system-wide or to one activity, templates, and `system:administrator` as a bypass |
| Evaluation | Runner registration, Ed25519 challenge–response, atomic job claiming, leases, heartbeats, idempotent reporting, trials |
| Files | upload, download, metadata, and a collector for orphans. The SHA-256 the caller declares is **recomputed before storing** and the upload is refused if it disagrees |
| Background work | four hosted services: the maintenance drainer, the lease reaper, the series scheduler, the file collector |
| Operations | maintenance levels `open`/`draining`/`closed`, and `aj-admin` in the image |
| Schema | **four migrations**, the earliest `20260807222825_InitialCreate` |
| OpenAPI | `openapi.json` is committed and CI fails if it stops matching what is served |

**Twenty-one `DbSet`s** on top of `IdentityDbContext<User>`. The main ones:
`Activity`, `Series`, `SeriesProblem`, `Problem`, `ProblemVersion`,
`Submission`, `EvaluationJob`, `Trial`, `Result`, `Runner`, `Question`, `File`,
`Grant`, `PermissionTemplate`, `Instance`, `MaintenanceState`, `UserSession`.

Every identifier is a **UUIDv7**, except `User`, which keeps Identity's string
key. The reason for version 7 is under *Decisions in force*.

`Result` carries `Score`, `MaxScore`, `Verdict`, `RunnerVersion` and an opaque
`Extra`; the per-test table and the compiler log are **attachments**, reached by
id like every other stored document, rather than fields on the row.

### What is genuinely not here

- **Identity phase 2.** No OIDC providers yet: every account is local, and
  `SessionDto.IsLocal` is still a hard-coded `true`. Specified and accepted —
  `AlgoJudge-Design/adr/IDENTITY_PHASE_2_DECISIONS_2026-08-09.md` — not built.
- **Mail.** There is no sender, so password reset and confirmation resend do not
  exist. They are **refused rather than absent**: `MapIdentityApi` maps them
  unconditionally and middleware answers them, because an endpoint that exists
  and cannot work invites a screen to promise something nothing will deliver.
- **2FA.** The endpoints `MapIdentityApi` brings are unused rather than
  half-wired, by decision.
- **LTI and grade export.** A later direction, deliberately outside the
  evaluation path.

## Decisions in force

- **Identity stays here for the MVP** — and, for administrator, local and
  temporary accounts, permanently. ASP.NET Identity and password storage remain.
  What arrives in phase 2 is not a move but an *addition*: several OIDC providers
  registered at once, from the database, with a claim-to-permission mapping the
  installation configures. Specified 2026-08-09,
  `AlgoJudge-Design/adr/IDENTITY_PHASE_2_DECISIONS_2026-08-09.md`; **not yet
  implemented**.
- ~~**`EvaluationJob` is deferred as an entity.** The Runner linkage will live on
  `Result`, which is created at claim time and doubles as the job record.~~
  **Reversed — it is an entity.** `EvaluationJob` carries the attempt number, the
  Runner, the state, the lease and its token, and `Result` hangs off *it* rather
  than the other way round. Struck rather than deleted, because the reasoning
  behind the deferral — something has to name a Runner while evaluation is still
  running — is exactly what the job turned out to be.
- ~~**All identifiers become string UUIDs.** The entities still use `int` keys;
  that migration is outstanding.~~ **Done.** Every entity carries a `Guid` from
  `Utils/Uuid.cs`, and specifically a **version 7** one: time-ordered, so inserts
  append to the index instead of fragmenting it. The layout is written out by
  hand because `Guid.CreateVersion7()` arrived in .NET 9 and this targets .NET 8
  — delete it and call the framework method after an upgrade, the values are
  compatible. `User` keeps Identity's own string key.
- `Activity.Type` is the type discriminator, formatted `name@version`. Adding a
  problem or activity type must not require a change here.

## Requirements

- .NET 8 SDK
- PostgreSQL, or Docker for the supplied Compose file

## Build

```bash
dotnet restore AlgoJudge.sln
dotnet build AlgoJudge.sln -c Release
```

## Database configuration

The connection string is read from `ConnectionStrings:DbConnectionString`.
Three sources are available, later ones overriding earlier ones:

1. `appsettings.json` — the committed default
2. `appsettings.Development.json` — the local development default
3. `AJ_`-prefixed environment variables, or .NET user secrets

### Setting your local password

`appsettings.Development.json` ships with a placeholder:

```json
"DbConnectionString": "Host=localhost;Database=algojudge;Username=postgres;Password=X"
```

**This repository is public and both `appsettings` files are tracked by Git.**
Do not replace `X` with a real password in that file — it would be committed and
published. Put the real value in one of the two untracked locations instead.

User secrets, stored outside the repository (the project already has a
`UserSecretsId`):

```bash
dotnet user-secrets set "ConnectionStrings:DbConnectionString" \
  "Host=localhost;Database=algojudge;Username=postgres;Password=your-password" \
  --project AlgoJudge.Server
```

PowerShell:

```powershell
dotnet user-secrets set "ConnectionStrings:DbConnectionString" "Host=localhost;Database=algojudge;Username=postgres;Password=your-password" --project AlgoJudge.Server
```

Or an environment variable, which is what the Compose file uses:

```bash
AJ_ConnectionStrings__DbConnectionString="Host=localhost;Database=algojudge;Username=postgres;Password=your-password"
```

## The published image

Released images are pushed to GitHub's container registry when a `v*` tag is
pushed:

```bash
docker pull ghcr.io/algojudge/algojudge-server:1.2.3
```

`1.2.3`, `1.2`, `1` and `latest` all point at the same image. **A prerelease
(`v1.2.3-rc.1`) publishes only its own tag** — nothing moving follows it, so
`latest` is never a release candidate. `linux/amd64` only, which is what the
Runner requires anyway.

## Running with Docker Compose

Brings up PostgreSQL and the Server, both bound to `127.0.0.1`:

```bash
docker compose -f example-server-development-docker-compose.yaml up
```

## Signing in for the first time

**Set `AJ_ADMIN_TOKEN` before starting a deployment for the first time.** Copy
`.env.example` to `.env` beside the compose file and fill it in. Skipping this
costs an evening, and here is why.

Every installation is seeded with **one account, `admin`** — no name, no address,
because it is not a person; it is the account somebody makes people with. Its
password is **twenty random characters that are never logged, never returned and
never derivable**. That is deliberate: a default administrator password is a
password an attacker also knows, and the alternative to a default is a password
nobody knows plus a documented way to set one.

That way is `POST /api/v1/admin/password`, which answers only to a caller on the
**loopback interface** carrying the **`X-AlgoJudge-Admin-Token`** header. An
unset or empty `Admin:Token` closes `/admin/**` entirely — so no token plus a
password nobody knows means no way in at all.

**Use `aj-admin`.** The image ships it on `PATH` inside the container, it is the
supported way to operate this Server, and it is what CI exercises against the
built image. Writing the HTTP request by hand is a fallback for an image older
than the tool — `docs/specs/MAINTENANCE.md` keeps that form, along with the three
ways it goes wrong.

```bash
docker compose exec -it algojudge aj-admin password       # prompts, no echo
docker compose exec -T  algojudge aj-admin status
docker compose exec -T  algojudge aj-admin maintenance on "nightly backup"
```

It reads the token from the Server's own environment — so the token is never
typed at a shell, never enters history and never appears in `ps` — and it exits
non-zero when the Server refuses, so it can be used in a script.

| Key | Environment | Default |
|---|---|---|
| `Admin:Token` | `AJ_ADMIN_TOKEN` → `AJ_Admin__Token` | empty — `/admin` closed |

Development needs none of this: the compose file falls back to a token named
`admin-token-development-only`, and the Development seed puts the well-known
password `admin-development-only` on the account. Both are in a public repository
and neither is a secret; the Server warns on every start where that token is in
force outside Development.

`docs/specs/MAINTENANCE.md` in the workspace has the exact `docker exec` recipes
— the shipped image has no `curl` and no `wget`, so they are written with bash's
`/dev/tcp` — along with the maintenance switch, which lives on the same surface.

**`admin` is a reserved login.** No other account may be created with it, nobody
may rename themselves to it, and the administrator may not rename itself away:
the password endpoint resets *the account named `admin`*, so the name is what the
endpoint points at.

## Migrations

In the Development environment the application applies pending migrations on
startup. Outside Development it refuses to start while migrations are pending.

```bash
dotnet ef database update --project AlgoJudge.Server
```

## Contributing

`main` is the integration and default branch; changes arrive through pull
requests. ~~There is no CI and no test project yet, so `dotnet build` is the
whole gate.~~ **Both exist**, and the gate is three CI jobs:

```bash
dotnet build AlgoJudge.sln -c Release
dotnet test  AlgoJudge.sln -c Release --no-build
```

`AlgoJudge.Server.Tests` runs against a **real PostgreSQL** started by
Testcontainers, so Docker has to be running — an in-memory provider would not
exercise the guarantees being relied on, several of which are the database's.
**109 tests, 24 seconds** on the machine this was last run on.

CI adds two jobs beside that one: the container image is built, and the
development stack is brought up and asserted against — that the API answers under
`/api/v1` and *not* at the root, that the migrations created the schema, that the
instance table really is a singleton, that the committed `openapi.json` still
matches what is served, that registration is closed by default, and that
`aj-admin` works inside the shipped image.

Architecture rules that apply here: the Server does not compile or execute code,
does not implement a sandbox or a checker, and must not depend on one Runner
implementation. Adding an activity or problem type must not require a change to
this repository — no type-specific controller, table or conditional.

## Related repositories

- [AlgoJudge-Client](https://github.com/AlgoJudge/AlgoJudge-Client) — the web frontend
- [AlgoJudge-Runner](https://github.com/AlgoJudge/AlgoJudge-Runner) — isolated execution and evaluation

### The frontend that used to live here

A copy of the frontend sat in `algojudge-client/` in this repository until
2026-08-02. It was verified as an outdated duplicate and removed, and its
`AlgoJudge.sln` entry went with it, so `dotnet build` no longer runs
`npm install`.

**Look for that code and its history in `AlgoJudge-Client`, not here.** The
frontend was migrated there with its commits: that repository's history starts
in December 2023, well before the repository itself was created, and it carries
the contributors who worked on the copy that used to be in this one.

Commits touching `algojudge-client/` do still exist in this repository's own
history, but they end on 2026-08-02 and are not where the work continued.

## License

See [LICENSE](LICENSE). Contributors are listed in [AUTHORS.txt](AUTHORS.txt).
