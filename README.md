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

## What it does

| Area | State |
|---|---|
| API | **160 controller actions**, all under `/api/v1` (`UsePathBase`), plus what `MapIdentityApi` adds under `/identity` |
| WebSocket | served at `/ws`; the event catalogue is committed as `events.json`, so both sides can diff their names against it |
| Authorization | a real permission model: **52 keys**, grants scoped system-wide or to one activity, templates, and `system:administrator` as a bypass |
| Evaluation | Runner registration, Ed25519 challenge–response, atomic job claiming, leases, heartbeats, idempotent reporting, trials |
| Files | upload, download, metadata, and a collector for orphans. The SHA-256 the caller declares is **recomputed before storing** and the upload is refused if it disagrees. Where the bytes live is configuration — `postgres`, `filesystem` or `s3`, several stores at once — and a worker moves them between stores on request |
| Identity | several OIDC providers registered at once from the database, first-sign-in provisioning, and a claim-to-permission mapping the installation configures |
| LTI | grade synchronisation, roster and deep linking |
| Background work | **nine hosted services**: the maintenance drainer, the lease reaper, the series scheduler, the deletion sweeper, the merge sweeper, the address sweeper, the file collector, the storage migrator, and the LTI module's grade synchroniser |
| Operations | maintenance levels `open`/`draining`/`closed`, `aj-admin` in the image, and `/admin/storage`, `/admin/keyring` and `/admin/config` behind loopback and a token |
| Schema | **two migrations in the main context and one in the LTI one** |
| OpenAPI | `openapi.json` is committed and CI fails if it stops matching what is served |

**Thirty-two `DbSet`s** on top of `IdentityDbContext<User>`. The main ones:
`Activity`, `Series`, `SeriesProblem`, `Problem`, `ProblemVersion`,
`Submission`, `EvaluationJob`, `Trial`, `Result`, `Runner`, `Question`, `File`,
`Grant`, `PermissionTemplate`, `Instance`, `MaintenanceState`, `UserSession`,
`StorageMigration`.

**Three of those numbers are commands**, so read them rather than trust them:

```sh
grep -rhoE '^ +\[Http[A-Za-z]+' AlgoJudge.Server/Controllers | wc -l         # 160
grep -cE '^ +Define\(' AlgoJudge.Server/Authorization/Permissions.cs         # 52
grep -c 'public DbSet<' AlgoJudge.Server/Database/ApplicationDbContext.cs    # 32
```

The permission catalogue is written down in three repositories — here, the
Client's fake, and `docs/specs/PERMISSIONS.md` — and
`scripts/check-permissions.py` in the workspace compares all three. The Server is
the source of truth; the grep above reads it directly, so the two agree by
construction rather than by luck.

Every identifier is a **UUIDv7**, except `User`, which keeps Identity's string
key. The reason for version 7 is under *Decisions in force*.

`Result` carries `Score`, `MaxScore`, `Verdict`, `RunnerVersion` and an opaque
`Extra`; the per-test table and the compiler log are **attachments**, reached by
id like every other stored document, rather than fields on the row.

### Not implemented

- **Mail.** There is no sender, so password reset and confirmation resend do not
  exist. They are **refused rather than absent**: `MapIdentityApi` maps them
  unconditionally and middleware answers them, because an endpoint that exists
  and cannot work invites a screen to promise something nothing will deliver.
- **2FA.** The endpoints `MapIdentityApi` brings are unused rather than
  half-wired, by decision.
- **`/identity/manage/info` is refused as well**, and for a different reason than
  mail: the framework builds that response with a throw when the account has no
  address, and this product allows accounts without one — the seeded
  administrator is one. There is nothing to configure, so the route is closed.
  `GET /api/v1/account` answers for every account this product allows.
- **One person arriving through two doors is two accounts.** A federated
  identity is keyed on the provider and the subject, so a university's SSO and
  its Moodle are two subjects, and an unknown subject provisions a new account.
  Deciding which *existing* account a new subject belongs to is specified and
  not built — and correlating automatically on an unverified address is account
  takeover rather than a convenience, which is why it is a decision rather than
  a patch.

## Decisions in force

- **Identity stays here for the MVP** — and, for administrator, local and
  temporary accounts, permanently. ASP.NET Identity and password storage remain.
  Phase 2 was never a move but an *addition*, and it is **built**: several OIDC
  providers registered at once, from the database, with a claim-to-permission
  mapping the installation configures. The specification is
  `AlgoJudge-Design/adr/IDENTITY_PHASE_2_DECISIONS_2026-08-09.md` — **read its
  two amendment tables first**, because the body of an amended section still
  states the pre-amendment form.
- **`EvaluationJob` is an entity of its own.** It carries the attempt number,
  the Runner, the state, the lease and its token, and `Result` hangs off *it*
  rather than the other way round — something has to name a Runner while an
  evaluation is still running.
- **Every entity carries a `Guid` from `Utils/Uuid.cs`, and specifically a
  version 7 one**: time-ordered, so inserts append to the index instead of
  fragmenting it. `Uuid.New()` calls `Guid.CreateVersion7()`, and the wrapper
  stays so the choice of v7 keeps one place to live. `User` keeps Identity's own
  string key.
- `Activity.Type` is the type discriminator, formatted `name@version`. Adding a
  problem or activity type must not require a change here.

## Requirements

- .NET 10 SDK
- PostgreSQL, or Docker for the supplied Compose file

### Tested against

**What the suites and CI actually run**, rather than what is believed to work.
Anything outside this table is *unverified*, which is not the same as
unsupported — see below for how to check one.

| | version | how it is exercised |
|---|---|---|
| .NET | **10.0** — SDK `10.0.400` here, `10.0.x` on CI | `net10.0`; the images are `aspnet:10.0` and `sdk:10.0` |
| PostgreSQL | **18** | every test suite and the Compose stack. The major is pinned on purpose: 18 moved where the data directory lives |
| RustFS | **1.0.0-rc.4** | the S3 suite's default endpoint, and the Compose stack. There is still no stable `1.0.0` |
| SeaweedFS | **4.43** | the S3 suite with `ALGOJUDGE_S3=seaweedfs`, **run by hand** — it skips by default, on CI included |

**Two of those carry a caveat worth reading before you raise them.**

- **SeaweedFS 4.44 exists and is not taken.** The reason recorded first — that
  it was broken — turned out to be a readiness race in the suite, since fixed.
  What holds the pin now is that the control test guarding the encryption check
  is **intermittent on every version tried**, so the comparison that chose 4.43
  is confounded rather than conclusive. `S3BlobStoreTests` says the rest.
- **The S3 suite is the way to check any other implementation**, and it needs no
  code change: point it at an endpoint with `ALGOJUDGE_S3_ENDPOINT` (see
  "Checking a store against the reference implementation"). An implementation
  that passes it satisfies the contract the suite encodes; one nobody has run it
  against is simply unknown.

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

## Where the files go

**An installation that configures no storage does not start.** That is
deliberate: where a product puts its files is not something to inherit from a
default nobody read. The Server says what is missing and exits.

A **store** is one configured place where bytes may live. A deployment may have
several, including several of the same kind, and every stored file remembers
which one holds it — for ever, which is why a store id may never be reused for
another location.

```bash
# An object store, which is what `Storage__Default` means when nobody says.
AJ_Storage__Stores__objects__Kind=s3
AJ_Storage__Stores__objects__Endpoint=https://s3.example
AJ_Storage__Stores__objects__Bucket=algojudge
AJ_Storage__Stores__objects__AccessKey=…
AJ_Storage__Stores__objects__SecretKey=…
AJ_Storage__Stores__objects__Region=us-east-1        # optional
AJ_Storage__Stores__objects__TimeoutSeconds=600      # optional; how long one request may take
AJ_Storage__Stores__objects__MaxErrorRetry=2         # optional; retries of a retryable failure

# Whose word to take for a visitor's address. Required: the Server does not
# start without one of these.
AJ_Forwarded__KnownProxies=10.0.0.2,10.0.0.3   # the address(es) your proxy reaches this Server from
AJ_Forwarded__KnownNetworks=10.0.0.0/24        # or its network, in CIDR; both families accepted
AJ_Forwarded__KnownProxies=none                # or this, when nothing sits in front

# **Addresses go in the first, networks in the second**, and the Server will not
# read one as the other. `KnownNetworks=10.0.0.2` is refused and told to use
# `KnownProxies`, or `10.0.0.2/32` if that one machine is the whole of it.
#
# **A network with bits below its prefix is refused too.** `10.0.0.5/24` reads
# two ways — the machine, or the 254 around it — and this list decides whose
# word is taken for every visitor's address, so it is not guessed at. The
# refusal names the network you would have got.

# How long a session keeps the address and user agent it was made with.
AJ_Retention__SessionOriginDays=30             # optional; the aj_session cookie's own life
AJ_Retention__SubmissionOriginDays=365         # optional; a submission's address is evidence in a contest

# Where the problem picker's credentials are minted, for a self-hosted archive.
AJ_UvaExplorer__Origin=https://uvaexplorer.example   # optional; defaults to the hosted one
```

`TimeoutSeconds` is the one worth knowing about. **The SDK's own default is no
deadline at all** — an unassigned `AmazonS3Config` carries a `Timeout` of
twenty-four days — and this Server holds a gate across its S3 calls
while it checks the bucket, so one unanswered request would have queued every
upload in the installation behind it with no end. Ten minutes is generous
against the 128 MiB ceiling on a single write; lower it only where the link is
known.

**The access key stored for `uvaexplorer` is never handed to a browser.** An
administrator sets the long-lived `uexpl_…` key in the instance
settings; when a manager opens the problem picker, this Server exchanges it at
`{UvaExplorer:Origin}/api/access/token` for an hourly `uexplt_…` token and sends
only that. The picker puts whatever it is given into an iframe address, which is
why the exchange exists. **A failed exchange is a refusal, never a fallback to
the stored key** — and an installation holding no key at all gets a 404, which
the Client reads as "browse the public archive".

**`Forwarded__KnownProxies` is required and has no default.** The address is
shown to a judge asked whether a solution came from the examination room, so
`X-Forwarded-For` is honoured only from a proxy named here.

There is no safe default. Trusting everyone is where this came from; trusting
only loopback silently records the proxy's own address in a container network
and looks like it is working. So an installation says which it is, and `none` —
"nothing sits in front, believe the socket" — is as valid an answer as naming a
proxy.

Behind nginx the proxy must send the header, and one hop is what this Server
reads:

```nginx
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;
```

That directive *appends* the peer to whatever arrived, so a header a participant
wrote themselves ends up earlier in the chain and the address nginx observed ends
up last — which is the one taken.

Back to where the bytes go, and the two stores that are not an object store:

```bash
# Or a volume.
AJ_Storage__Stores__local__Kind=filesystem
AJ_Storage__Stores__local__Path=/var/lib/algojudge/blobs

# Or the database, which needs nothing else at all.
AJ_Storage__Stores__pg__Kind=postgres

AJ_Storage__Default=objects        # which one takes new writes
```

Three kinds, all supported:

| Kind | What it needs | What it costs |
|---|---|---|
| `s3` | an endpoint, a bucket and a key pair | an object store to run or to buy |
| `filesystem` | a path on a volume | the backup has to cover two things |
| `postgres` | nothing | the database grows by every file |

`postgres` is the configuration with no dependencies: one container, and
`pg_dump` alone is a complete backup. It is the right answer for a small
installation and the wrong one for a large contest.

**Credentials live in the environment and nowhere else.** No endpoint sets a
store, nothing is stored in the database, and none of it appears in a public
answer — `GET /health` says `"storage":"ok"` or `"degraded"` and never which
store, backend, bucket or path.

**The Server never creates a bucket.** On some providers encryption at rest is
applied when the bucket is made and cannot be added convincingly afterwards, so
that is the operator's act. The development Compose file is the one exception and
says so where it sets the flag.

### Checking a store against the reference implementation

The S3 conformance suite runs against **RustFS** by default, which is the local
development endpoint. Before a release it is run against **SeaweedFS**, which is
the reference implementation — the suite starts either, and no test code
changes:

```bash
ALGOJUDGE_S3=seaweedfs dotnet test AlgoJudge.sln -c Release --filter S3BlobStoreTests
```

Or against an endpoint somebody else is running, which skips the one item that
needs to look at the store's own data directory:

```bash
ALGOJUDGE_S3_ENDPOINT=https://… ALGOJUDGE_S3_ACCESS_KEY=… ALGOJUDGE_S3_SECRET_KEY=…   dotnet test AlgoJudge.sln -c Release --filter S3BlobStoreTests
```

**One item cannot be run against either implementation available here**: the
check writes a known
string, enables bucket-default encryption and looks for the string in the
store's files. SeaweedFS 4.41 stores objects readably — so the method works, and
a test proves it does — but answers `PutBucketEncryption` with an internal
error. RustFS accepts the call and stores objects in a form no grep can read
either way. Against an endpoint that supports both, `ALGOJUDGE_S3_SSE=1` runs it.

### Moving files from one store to another

Changing `Storage__Default` decides where the **next** upload goes and moves
nothing. Moving what is already stored is a separate, deliberate act — take a
backup first:

```bash
docker compose exec server aj-admin storage status     # where the files are now
docker compose exec server aj-admin storage migrate    # move them to the default
docker compose exec server aj-admin storage cancel     # call it off
```

It does not begin at once. A migration waits for its window — `02:00` UTC by
default, `AJ_Storage__Migration__StartHourUtc`, and a negative value means any
hour — and for the evaluation queue to empty and every series to close, so
nothing moves under a running contest. `storage status` says which of those it is
waiting for.

Each file is read, checked against its own checksum, written to the target, and
only then does its row point at the new store. The old copy is kept for
`AJ_Storage__Migration__GraceMinutes` (60 by default) so that a reader who
resolved the row a moment ago still finds it. A run works for at most
`AJ_Storage__Migration__BudgetMinutes` (30) and continues in the next window;
killing the process loses nothing, because what has moved is recorded on the
files themselves.

## What encrypts a session cookie

A signed-in browser holds a cookie this Server encrypted, and the keys for that
have to outlive the process. **They live in the database, and nothing needs
configuring** — `database` is what an installation gets when nobody says
otherwise.

```bash
AJ_DataProtection__Kind=database   # the default; the keys live beside everything else
AJ_DataProtection__Kind=ephemeral  # in memory, lost on restart — Development only
```

`ephemeral` **refuses to start** unless the environment is Development. It is
the arrangement above with a name, and on a real installation it presents as
people being signed out at random — which gets diagnosed as flakiness rather
than as configuration.

A kind this Server does not implement also refuses to start. **Redis is not
implemented**, deliberately: it is a second stateful service to back up and to
restore correctly, and this product asks a self-hosted installation for one
database and no more. `docs/specs/AUTHENTICATION.md` §10 in the workspace
carries the decision and what would reverse it.

**A database backup now holds what mints a session cookie.** It did not before —
the keys lived in the container and died with it — so a dump that used to carry
accounts and no way to impersonate one now carries both. Nothing was weakened;
keys that survive a restart are the point. But it is worth knowing on the day you
set backups up, and the certificate below is the mitigation.

**Two things to know before running more than one instance.** Every instance
needs the same database, and the application name is fixed in code — Data
Protection mixes it into every purpose, and two instances that disagree would
not share a ring even sharing a table.

### Encrypting the keys themselves

Optional, and off unless configured. Without it the keys are stored as plain XML;
whoever can read that table can also write a row into `AspNetUsers`, so this buys
less than it looks — it is here for installations whose database is somebody
else's to hold.

```bash
AJ_DataProtection__Certificates__0__Path=/run/secrets/keyring.pfx
AJ_DataProtection__Certificates__0__Password=…   # omit for a PFX with no password
```

A PKCS#12 file carrying its private key:

```bash
openssl req -x509 -newkey rsa:2048 -keyout keyring.key -out keyring.crt \
    -days 3650 -nodes -subj "/CN=algojudge-key-ring"
openssl pkcs12 -export -out keyring.pfx -inkey keyring.key -in keyring.crt \
    -passout pass:the-password-you-will-configure
```

Mount it read-only and point the setting at it. One that is missing, unreadable,
or carries **no private key** stops the Server at startup rather than at the
first sign-in — the last of those would otherwise encrypt a ring it could never
read back.

**The first listed encrypts new keys; every one listed can decrypt old ones.** So
rotating means putting the new certificate at the head and *keeping the old one
in the list*: keys encrypted with a certificate nobody supplies any more are keys
nobody can read, which looks exactly like having no key ring at all.

#### Turning it on later does nothing until the ring rotates

Worth knowing before an operator concludes it did not work: **adding the
setting to an installation that already has a key is neither disruptive nor
effective**. Sessions continue — the
existing plaintext key is still readable — and that key **stays plaintext**,
because Data Protection encrypts a key when it *writes* one and it writes one
only near the current key's expiry, ninety days out.

`aj-admin keyring rotate` writes one now. See below.

### Operating the key ring

```bash
docker compose exec server aj-admin keyring status
docker compose exec server aj-admin keyring rotate
docker compose exec server aj-admin keyring revoke --yes [reason]
```

**`status` is also the validation.** It reports which arrangement is in force,
every key with its dates and whether it is stored encrypted or in plain text,
and — the part nothing else answers — **whether this Server can still read each
key**. A key it cannot read is a certificate that was dropped instead of kept,
and every session minted under that key is already gone. It also names the two
problems an operator otherwise meets as symptoms: a certificate configured over
a plaintext key, and usable plaintext keys still sitting in the store.

**`rotate` signs nobody out.** It writes a new key, active immediately, and the
previous key stays readable — which is exactly what lets every open session carry
on. This is what to run after configuring a certificate on an installation that
already had keys.

**`revoke` signs everybody out**, and that is the point of it: a revoked key
cannot mint a cookie this Server will accept, so it is the answer to a database
dump that leaked. It requires `--yes`, because a flag that defaults to false is
still a flag somebody sets while reading the other half of a sentence.

**Rotating does not remove the plaintext key**, and `status` keeps saying so.
The old key stays usable until it expires — that is what makes rotating safe —
so a backup taken in the meantime still carries something that can mint a cookie.
Only `revoke`, or ninety days, ends that.

**With several instances, neither is immediate.** Data Protection refreshes its
key ring on a timer and one process's write does not notify another, so a revoke
takes effect on the instance you ran it against at once and on the others when
their ring next refreshes.

**The unencrypted-key warning names a key, not a startup.** Data Protection logs
*"No XML encryptor configured. Key {id} may be persisted to storage in
unencrypted form."* when it **creates** a key, so an installation running on a
key made months ago logs nothing at all. The absence of that line is not evidence
the keys are encrypted — `aj-admin keyring status` is.

## Configuring an installation from files

An installation's own settings — its name, how it admits people, its welcome page
and its policies, its mark — normally arrive by somebody clicking through the
manager panel. They can arrive from a directory instead:

```
preconfig/
├── algojudge.yml
├── pages/welcome.md        and home, terms, privacy, cookies, accessibility
└── logo.svg                or .png, or .webp
```

Point the Server at it and it is read:

```bash
AJ_Preconfiguration__Path=/etc/algojudge/preconfig
```

`preconfig.example/` in this repository is a working template, and is what the
development Compose file mounts. The format is specified in
`docs/specs/PRECONFIGURATION.md` in the workspace.

**Applied at the first start of an empty installation, and never again by a
boot.** After that it is a command, run by somebody who meant it:

```bash
docker compose exec algojudge aj-admin config status   # what would change
docker compose exec algojudge aj-admin config apply    # change it
```

`status` writes nothing, so it is safe to run at any time; `apply` performs
exactly what `status` listed.

Four things about it are worth knowing before it surprises you:

- **It adds; it never withdraws.** A setting the file leaves out is left as it
  is, not reset to a default, and a document the directory does not carry stays
  published. Removing something is done in the panel, by somebody who chose to.
- **A page is republished only when its contents differ**, compared by SHA-256.
  Publishing *adds* a revision rather than replacing one, so an apply that
  republished everything would grow a privacy policy's history on every run.
- **Nothing records what was applied.** `aj-admin config status` derives the
  answer from the database each time, so it cannot be stale. An empty `changes`
  list is the whole of "this installation matches its files".
- **A first start that cannot read the directory does not start.** A typo stops
  the deployment rather than bringing up an installation that is half what was
  asked for — and it can only happen on a start somebody is watching, because no
  later restart reads the files at all.

A value that must not sit in a repository is written as `${VARIABLE}` and read
from the Server's environment; a variable that resolves to nothing refuses the
apply rather than storing its own name. No setting in the current format is a
secret — the rule is there for the ones that come.

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

**This file is for development, and there is a separate one for installing.**
[`AlgoJudge-Ops`](https://github.com/AlgoJudge/AlgoJudge-Ops) — the self-hosted
Compose stack 0.1.0 targets — assembles the Server, the Client and a Runner
behind nginx, and carries the update, backup and restore scripts an installation
needs.
It builds nothing: every image is pulled from GHCR by tag. **An administrator
standing an installation up wants that repository, not this file**, and this
one is unaffected by it — a developer running the Server still runs what is
below.

Brings up PostgreSQL, an S3 endpoint and the Server:

```bash
docker compose -f example-server-development-docker-compose.yaml up
```

PostgreSQL and the Server are bound to `127.0.0.1`. The object store — RustFS,
pinned, the local development endpoint and nothing more — is reachable only from
inside the Compose network: it holds development files, it has no console, and
its credentials exist for this stack alone. The Server creates its bucket here
because the alternative was a second image whose only job is one `mb`; that flag
is set in this file and nowhere else.

**The service is called `algojudge` here and `server` in `AlgoJudge-Ops`**,
which is why both spellings appear in the `docker compose exec` recipes above
and below. Read the one that matches the stack in front of you; nothing else
about the commands differs.

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

**The Development seed adds somebody to compete against it**, which
`preconfig.example/pages/home.md` points at this file for:

| | login | password | what it is |
|---|---|---|---|
| administrator | `admin` | `admin-development-only` | seeded everywhere; only the password is Development's |
| participant | `student` | `student-development-only` | Development only |

They meet in `DEV-2026`, a seeded `contest@1` activity the participant holds a
participant grant on and the administrator a **systemic** manager grant — which
is what makes "staff are not counted among the competitors" visible in the
seeded data rather than only described. `Database/Seeder.cs` is the whole of it,
and none of the three exists outside Development.

`docs/specs/MAINTENANCE.md` in the workspace has the exact `docker exec` recipes
— the shipped image has no `curl` and no `wget`, so they are written with bash's
`/dev/tcp` — along with the maintenance switch, which lives on the same surface.

**`admin` is a reserved login.** No other account may be created with it, nobody
may rename themselves to it, and the administrator may not rename itself away:
the password endpoint resets *the account named `admin`*, so the name is what the
endpoint points at.

## Registering an identity provider

The Server sees a **plain OIDC provider** and nothing else — it holds no field
that only one product could fill, and it never learns which one is behind a
registration.

| Field | What it is |
|---|---|
| `issuer` | the provider's issuer URL, the one its discovery document is served under |
| `claimPath` | where group membership sits in the token |
| `scopes` | `openid profile email` |
| `accountUrl` | where somebody manages the identity itself |
| `deletionUrl` | where somebody ends the identity itself |
| `deletionChannelEnabled` | `true` once the provider id and the secret it mints are configured at the provider |

**`claimPath` is a dotted path and nothing else** — never an expression, which
would be code executed against the contents of a token. `ClaimMappingService`
walks it and flattens both a repeated claim and a JSON array, so a provider
emitting bare names at `groups` and one emitting a nested shape both work.
`FederatedSignInTests` drives a whole sign-in through each.

**The deletion channel is generic.** A provider that can call out reports a
deleted identity to one endpoint, in one shape, with one header, and the Server
holds nothing about how the provider produced it. Register the provider with the
channel enabled, take the provider id and the shared secret it mints, and put
both into the provider's own configuration — **the secret is write-only here**,
so a lost one is generated again rather than looked up.

The Server answers **404** to a wrong provider id, a wrong secret and a channel
switched off alike — deliberately indistinguishable, so nobody can learn which by
asking, and worth remembering when diagnosing from the other side.

**Neither issuer may be plain HTTP** except on loopback, which is exempted so a
development stack can be registered. Over plain HTTP on a real network, whoever
answers first decides who your users are.

The accepted record for the model is
`AlgoJudge-Design/adr/IDENTITY_PHASE_2_DECISIONS_2026-08-09.md`.

## Migrations

In the Development environment the application applies pending migrations on
startup. Outside Development it refuses to start while migrations are pending —
**unless the operator has asked it to apply them**, with
`AJ_Database__MigrateOnStart=true`.

```bash
dotnet ef database update --project AlgoJudge.Server
dotnet ef database update --project AlgoJudge.Server --context LtiDbContext
```

**The switch exists because nothing else shipped can apply a migration.**
`aj-admin` has no migrate command and the image carries no SDK; the commands
above need a workstation with the source, which a self-hosted stack does not
have.

It is **off by default**, and the refusal it replaces is unchanged when it is:
applying a schema change to a production database stays a decision somebody
makes rather than one a boot makes for them. What changed is that there is now a
way to say yes. **Take a backup before setting it** — `AlgoJudge-Ops` does this
for you, in that order.

`Database/Schema.cs` is the whole of it, and it takes a **PostgreSQL advisory
lock** while it works. Several instances against one database is a supported
arrangement and they start together after an update; EF Core 10 has no
migration lock of its own, so without this one of the instances dies of `23505`
on `PK___EFMigrationsHistory`.

**Both contexts read the switch**, or the LTI module refuses on its own over a
table nobody mentioned.

**Two contexts, two histories.** `ApplicationDbContext` keeps its migrations in
`Database/Migrations` and its history in `__EFMigrationsHistory`; the LTI module
keeps its own in `Lti/Migrations` and `__EFMigrationsHistory_Lti`, and applies
them itself (`Lti/LtiModule.cs`). A command that names no context gets the first.

**Each was squashed to one**, before 0.1.0 and therefore before any
installation had a database to carry forward. What the old chain carried and
these do not is its backfills — every one of them rewrote rows that a new
database does not have.

**A database created before the squash cannot be migrated across it.** Its
`__EFMigrationsHistory` names thirty-one migrations that no longer exist, and
`InitialCreate` is not among them, so the next start tries to create tables that
are already there. Development stacks are disposable, so the answer is to drop
the volume:

```bash
docker compose -f example-server-development-docker-compose.yaml down -v
```

**One block in `InitialCreate` is written by hand** and is not generated from the
model: `FileContents`, which the postgres blob store reads with raw SQL and which
is not an EF entity. `FileStorageSchemaTests` fails if it goes — including if it
survives without `SET STORAGE EXTERNAL`, which was checked by removing exactly
that line.

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

`BlobStoreContract` is abstract and xUnit discovers an inherited test once per
derived class, so its cases run against the filesystem, PostgreSQL and S3
implementations alike.

`openapi.json` is committed, and CI fails if it stops matching what is served.
**Take it from the running container**, never from the test host: the test host
emits the paths in a different order for identical code, and CI compares the two
files **textually**.

### Architecture rules

The Server does not compile or execute code, does not implement a sandbox or a
checker, and must not depend on one Runner implementation. Adding an activity or
problem type must not require a change to this repository — no type-specific
controller, table or conditional.

## Related repositories

- [AlgoJudge-Client](https://github.com/AlgoJudge/AlgoJudge-Client) — the web frontend
- [AlgoJudge-Runner](https://github.com/AlgoJudge/AlgoJudge-Runner) — isolated execution and evaluation
- [AlgoJudge-External-Runner](https://github.com/AlgoJudge/AlgoJudge-External-Runner) — a **second Runner implementation**, which
  judges nothing: it forwards submissions to external judging systems and
  reports their verdicts, with UVa Online Judge as its one integration, serving
  `uva@1` against `onlinejudge.org`. It registers, claims and reports over the
  same contract as the first, which is the point of the contract
- `AlgoJudge-Ops` (private) — the self-hosted production Compose stack, and the
  update, backup and restore scripts around it. No application code, no build:
  every image is pulled from GHCR by tag
### The frontend that used to live here

A copy of the frontend once sat in `algojudge-client/` here. **Look for that
code and its history in
[`AlgoJudge-Client`](https://github.com/AlgoJudge/AlgoJudge-Client), not here**:
it was migrated there with its commits, so that repository's history reaches
back further than the repository itself, and carries the authors who worked on
the copy that used to be in this one.

## Contributing

Open an issue saying what you expected, what happened, and how to reproduce it.
Or open a pull request against `main`: one subject per pull request, with a note
on what changes and why.

By contributing you agree that your work is licensed under the terms below.

## License

This project is licensed under the MIT License.
See [LICENSE](LICENSE).

Authors are listed in [AUTHORS.txt](AUTHORS.txt).
