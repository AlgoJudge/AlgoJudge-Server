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

- [AlgoJudge-Client](https://github.com/AlgoJudge/AlgoJudge-Client) — the web frontend. An older copy sat in `algojudge-client/` here until 2026-08-02, when it was verified as an outdated duplicate and removed; it remains in this repository's history.
- [AlgoJudge-Runner](https://github.com/AlgoJudge/AlgoJudge-Runner) — isolated execution and evaluation

## License

See [LICENSE](LICENSE). Contributors are listed in [AUTHORS.txt](AUTHORS.txt).
