# AlgoJudge Server

Persistent state, REST API, authorization, activities, tasks, submissions and
results for [AlgoJudge](https://github.com/AlgoJudge).

The Server is deliberately simple. It stores files, metadata, access policies
and results, and moves data between the Client and the Runners. It never
compiles or executes submitted code, and it holds no knowledge of what a
particular task type means.

## Status

Early development. The domain model exists; most of the API does not.

| Endpoint | State |
|---|---|
| `GET /ping/ping` | implemented |
| `GET /activity/list` | implemented; the `Query` paging and ordering parameters are accepted but ignored |
| `POST /activity/create` | implemented |
| `/identity/*` | provided by ASP.NET Identity |

Entities: `Activity`, `Series`, `SeriesProblem`, `Problem`, `Submission`,
`Result`, `File`, `User`. One migration, `20240130140424_InitialCreate`.

Not implemented: WebSocket, the Runner registry, job reservation, task and
submission endpoints, file upload and download, and result payloads — `Result`
currently carries no verdict, score or per-test data. Authorization checks only
that a request is authenticated.

## Decisions in force

- **Identity stays here for the MVP.** ASP.NET Identity and password storage
  remain. Moving identity into a separate component behind OIDC is the target,
  deliberately deferred.
- **`EvaluationJob` is deferred as an entity.** The Runner linkage will live on
  `Result`, naming the Runner that is evaluating or has evaluated a submission.
  Because it must name a Runner while evaluation is still running, `Result` is
  created at claim time and doubles as the job record.
- **All identifiers become string UUIDs.** The entities still use `int` keys;
  that migration is outstanding.
- `Activity.Type` is the type discriminator, formatted `name@version`. Adding a
  task or activity type must not require a change here.

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
requests. `dotnet build` must succeed before opening one. There is no CI and no
test project yet, so that is the whole gate.

Architecture rules that apply here: the Server does not compile or execute code,
does not implement a sandbox or a checker, and must not depend on one Runner
implementation. Adding an activity or task type must not require a change to
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
