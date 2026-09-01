# AlgoJudge Server

AlgoJudge is open-source, self-hosted software for programming contests and
courses, with automatic evaluation of submitted solutions.

Persistent state, REST API, WebSocket, authorization, activities, problems,
submissions and results for [AlgoJudge](https://github.com/AlgoJudge).

The Server is deliberately simple. It stores files, metadata, grants and
results, and moves data between the Client and the Runners. It never compiles or
executes submitted code, and it holds no knowledge of what a particular problem
type means.

The domain term is **`Problem`**, never `Task`.

## Documentation

**[docs.algojudge.pl](https://docs.algojudge.pl/en/server/)** is written for
somebody who does not have this source open. This README is the other half:
what the repository is, and how to build, run and change it.

| | |
|---|---|
| [`/en/server/`](https://docs.algojudge.pl/en/server/) | the domain model, permissions and grants, identity and LTI, the event catalogue, and every configuration key |
| [`/en/server/rest/`](https://docs.algojudge.pl/en/server/rest/) | the REST reference, generated from the `openapi.json` committed here — pinned by commit and verified by checksum, so it describes one known version rather than whatever `main` says today |
| [`/en/protocol/`](https://docs.algojudge.pl/en/protocol/) | the contract this Server and a Runner share |

The site is English here. Polish covers `/client/` and `/install/` — the
participant's and the administrator's paths.

## What it does

| Area | |
|---|---|
| API | REST, **all of it under `/api/v1`** (`UsePathBase`), identity included: `/api/v1/identity/register`, never `/identity/register` |
| WebSocket | served at `/api/v1/ws` — it is mapped as `/ws` under the same path base as everything else; the event catalogue is committed as `events.json`, so both sides can diff their names against it |
| Authorization | a real permission model: grants scoped system-wide or to one activity, templates, and `system:administrator` as a bypass — **refused outright in an activity grant**, so a manager of one course never becomes an administrator of the installation |
| Evaluation | Runner registration, Ed25519 challenge–response, atomic job claiming, leases, heartbeats, idempotent reporting, trials. `EvaluationJob` is an entity of its own and `Result` hangs off it, because something has to name a Runner while an evaluation is still running |
| Files | upload, download, metadata, and a collector for orphans. The SHA-256 the caller declares is **recomputed before storing** and the upload is refused if it disagrees. Where the bytes live is configuration — `postgres`, `filesystem` or `s3`, several stores at once — and a worker moves them between stores on request |
| Accounts | local accounts, and several OIDC providers registered at once from the database, with first-sign-in provisioning and a claim-to-permission mapping the installation configures |
| LTI | grade synchronisation, roster, deep linking, and **dynamic registration** — a platform registers itself against a single-use invitation |
| Operations | maintenance levels `open`/`draining`/`closed`, `aj-admin` in the image, and `/admin/storage`, `/admin/keyring` and `/admin/config` behind loopback and a token |
| OpenAPI | `openapi.json` is committed and CI fails if it stops matching what is served |

Every identifier is a **UUIDv7** — time-ordered, so inserts append to the index
instead of fragmenting it — from `Utils/Uuid.cs`. The exception is `User`, which
keeps ASP.NET Identity's own string key.

`Result` carries `Score`, `MaxScore`, `Verdict`, `RunnerVersion` and an opaque
`Extra`; the per-test table and the compiler log are **attachments**, reached by
id like every other stored document, rather than fields on the row.

## Requirements

- .NET 10 SDK
- PostgreSQL, or Docker for the supplied Compose file

**What the suites and CI actually run**, rather than what is believed to work.
Anything outside this table is *unverified*, which is not the same as
unsupported: the S3 suite is the way to check any other object store, and needs
no code change — point it at an endpoint with `ALGOJUDGE_S3_ENDPOINT`.

| | version | how it is exercised |
|---|---|---|
| .NET | **10.0** — SDK `10.0.400` here, `10.0.x` on CI | `net10.0`; the images are `aspnet:10.0` and `sdk:10.0` |
| PostgreSQL | **18** | every test suite and the Compose stack. The major is pinned on purpose: 18 moved where the data directory lives |
| RustFS | **1.0.0-rc.4** | the S3 suite's default endpoint, and the Compose stack. There is still no stable `1.0.0` |
| SeaweedFS | **4.43** | the S3 suite with `ALGOJUDGE_S3=seaweedfs`, **run by hand** — it skips by default, on CI included |

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

Or an environment variable, which is what the Compose file uses:

```bash
AJ_ConnectionStrings__DbConnectionString="Host=localhost;Database=algojudge;Username=postgres;Password=your-password"
```

## Where the files go

**An installation that configures no storage does not start.** That is
deliberate: where a product puts its files is not something to inherit from a
default nobody read. The Server says what is missing and exits.

A **store** is one configured place where bytes may live. A deployment may have
several, including several of the same kind, and every stored file remembers
which one holds it — for ever, which is why a store id may never be reused for
another location. Three kinds: `postgres`, `filesystem` and `s3`.

```bash
# An object store, which is what `Storage__Default` means when nobody says.
AJ_Storage__Stores__objects__Kind=s3
AJ_Storage__Stores__objects__Endpoint=https://s3.example
AJ_Storage__Stores__objects__Bucket=algojudge
AJ_Storage__Stores__objects__AccessKey=…
AJ_Storage__Stores__objects__SecretKey=…
```

**The Server never creates a bucket** outside the development stack, and no
public answer names a store, backend, bucket or path: `/health` says one word
and `/admin/storage` carries the detail, behind loopback and a token.

## Running with Docker Compose

**This file is for development, and there is a separate one for installing.**
[`AlgoJudge-Ops`](https://github.com/AlgoJudge/AlgoJudge-Ops) — the self-hosted
Compose stack 0.1.0 targets — assembles the Server, the Client and a Runner
behind nginx, and carries the update, backup and restore scripts an installation
needs. It builds nothing: every image is pulled from GHCR by tag. **An
administrator standing an installation up wants that repository, not this file.**

Brings up PostgreSQL, an S3 endpoint and the Server:

```bash
docker compose -f example-server-development-docker-compose.yaml up
```

PostgreSQL and the Server are bound to `127.0.0.1`. The object store — RustFS,
pinned, the local development endpoint and nothing more — is reachable only from
inside the Compose network.

**The service is called `algojudge` here and `server` in `AlgoJudge-Ops`.** Read
the one that matches the stack in front of you; nothing else about the commands
differs.

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
built image:

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

**`admin` is a reserved login.** No other account may be created with it, nobody
may rename themselves to it, and the administrator may not rename itself away:
the password endpoint resets *the account named `admin`*, so the name is what the
endpoint points at.

### Everybody else

**ASP.NET Core Identity is here for the administrator, and for local and
temporary accounts.** It keeps their passwords, and it is not going away.

**For everybody else, register an OIDC provider — that is the recommended way to
sign people in.** Several may be registered at once, from the database; an
unknown subject provisions an account on first sign-in, and a claim-to-permission
mapping the installation configures decides what it may do. A federated identity
is keyed on the **provider and the subject**, never on an email address.

Self-registration is the third door and is **off by default**
(`Instance.LocalRegistrationEnabled`); while it is off,
`POST /api/v1/identity/register` answers `403 registration.closed`.

**There is no mail sender**, so password reset and confirmation resend do not
exist — they are *refused* rather than absent, because an endpoint that exists
and cannot work invites a screen to promise something nothing will deliver.

### In Development

Development needs none of the above: the compose file falls back to a token named
`admin-token-development-only`, and the Development seed puts the well-known
password `admin-development-only` on the account. Both are in a public repository
and neither is a secret; the Server warns on every start where that token is in
force outside Development.

**The Development seed adds somebody to compete against it**, which
`preconfig.example/pages/home.md` points at this file for:

| | login | password | what it is |
|---|---|---|---|
| administrator | `admin` | `admin-development-only` | seeded everywhere; only the password is Development's |
| participant | `student` | `student-development-only` | Development only |

They meet in `DEV-2026`, a seeded `contest@1` activity. `Database/Seeder.cs` is
the whole of it, and none of the three exists outside Development.

## The published image

Released images are pushed to GitHub's container registry when a `v*` tag is
pushed:

```bash
docker pull ghcr.io/algojudge/algojudge-server:0.1.0
```

`0.1.0`, `0.1`, `0` and `latest` all point at the same image. **A prerelease
(`v0.1.0-rc.1`) publishes only its own tag** — nothing moving follows it, so
`latest` is never a release candidate. `linux/amd64` only, which is what the
Runner requires anyway.

[docs/RELEASE.md](docs/RELEASE.md) is what to do before pushing that tag.

## Migrations

In the Development environment the application applies pending migrations on
startup. Outside Development it refuses to start while migrations are pending —
**unless the operator has asked it to apply them**, with
`AJ_Database__MigrateOnStart=true`. **Take a backup before setting it**;
`AlgoJudge-Ops` does this for you, in that order.

```bash
dotnet ef database update --project AlgoJudge.Server
dotnet ef database update --project AlgoJudge.Server --context LtiDbContext
```

**Two contexts, two histories.** `ApplicationDbContext` keeps its migrations in
`Database/Migrations` and its history in `__EFMigrationsHistory`; the LTI module
keeps its own in `Lti/Migrations` and `__EFMigrationsHistory_Lti`, and applies
them itself (`Lti/LtiModule.cs`). A command that names no context gets the first,
and **both read the switch** — or the LTI module refuses on its own over a table
nobody mentioned.

**A database created before the pre-0.1.0 squash cannot be migrated across it.**
Its history names migrations that no longer exist. Development stacks are
disposable, so the answer is to drop the volume:

```bash
docker compose -f example-server-development-docker-compose.yaml down -v
```

## Building and testing

The gate is three CI jobs, and the first builds with `-warnaserror`, so **a
warning fails it**:

```bash
dotnet build AlgoJudge.sln -c Release
dotnet test  AlgoJudge.sln -c Release --no-build
```

`AlgoJudge.Server.Tests` runs against a **real PostgreSQL** started by
Testcontainers, so Docker has to be running — an in-memory provider would not
exercise the guarantees being relied on, several of which are the database's.
The object-store cases skip where no object store is configured.

`openapi.json` is committed, and CI fails if it stops matching what is served.
**Take it from the running container**, never from the test host: the test host
emits the paths in a different order for identical code, and CI compares the two
files **textually**.

```sh
docker compose -f example-server-development-docker-compose.yaml up -d --build --wait
curl -sS --fail-with-body http://127.0.0.1:8080/api/v1/swagger/v1/swagger.json -o openapi.json
docker compose -f example-server-development-docker-compose.yaml down -v
```

`--build` is not optional when the change is yours: without it Compose serves
the image it already has, and the document you commit is the one from before
the edit. The endpoint is mapped in Development only, which is what that stack
runs.

## Architecture rules

The Server does not compile or execute code, does not implement a sandbox or a
checker, and must not depend on one Runner implementation. Adding an activity or
problem type must not require a change to this repository — no type-specific
controller, table or conditional. `Activity.Type` is the type discriminator, one
string formatted `name@version`.

## Related repositories

- [AlgoJudge-Client](https://github.com/AlgoJudge/AlgoJudge-Client) — the web frontend
- [AlgoJudge-Runner](https://github.com/AlgoJudge/AlgoJudge-Runner) — isolated execution and evaluation
- [AlgoJudge-External-Runner](https://github.com/AlgoJudge/AlgoJudge-External-Runner) —
  a second Runner, forwarding submissions to external judging systems. It
  registers, claims and reports over the same contract as the first, which is the
  point of the contract
- [AlgoJudge-Ops](https://github.com/AlgoJudge/AlgoJudge-Ops) — the self-hosted
  production Compose stack, and the update, backup and restore scripts around it.
  No application code, no build: every image is pulled from GHCR by tag
- [AlgoJudge-Docs](https://github.com/AlgoJudge/AlgoJudge-Docs) — the source of
  the documentation site linked under *Documentation* above

## Contributing

Open an issue saying what you expected, what happened, and how to reproduce it.
Or open a pull request against `main`: one subject per pull request, with a note
on what changes and why.

By contributing you agree that your work is licensed under the terms below.

## License

This project is licensed under the MIT License.
See [LICENSE](LICENSE).

Authors are listed in [AUTHORS.txt](AUTHORS.txt).
