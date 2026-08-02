# AlgoJudge Server

Persistent state, REST API, authorization, activities, tasks, submissions and
results for [AlgoJudge](https://github.com/AlgoJudge).

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

## Notes

- The Server does not compile or execute submitted code. Evaluation belongs to
  [AlgoJudge-Runner](https://github.com/AlgoJudge/AlgoJudge-Runner).
- The frontend lives in
  [AlgoJudge-Client](https://github.com/AlgoJudge/AlgoJudge-Client). An older
  copy used to sit in `algojudge-client/` here; it was removed once verified to
  be an outdated duplicate, and remains in this repository's history.
