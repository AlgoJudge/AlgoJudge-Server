# Releasing the Server

For whoever cuts the release. Nothing here is addressed to somebody installing
the product — that is [AlgoJudge-Ops](https://github.com/AlgoJudge/AlgoJudge-Ops)
and the documentation site.

Every dated figure below was measured on the date it names — the 2026-09-07
readings on `release/0.1.0` at `fa9bc29`, the 2026-09-20 ones on `release/0.2.0`
off `b4b1455`. Anything undated is a rule rather than a reading.

## Where the version lives

**`Directory.Build.props`, one line.** Both projects inherit it, so a release
changes that file and nothing else. Without it MSBuild uses 1.0.0, which is what
this repository shipped as until 0.1.0 because nobody had said otherwise.

**`openapi.json` does not carry it.** Its `info.version` is `1.0` and stays
there: that is the version of the **API**, which is served at `/api/v1` and does
not move because the product released. Changing it would move the REST reference
on the documentation site, which pins this file by commit and checksum.

## What a tag does

`.github/workflows/release.yml` runs on a pushed tag matching `v*`, and **only
then** — nothing that lands on `main` reaches the registry on its own. It
refuses a tag that does not point at a commit on `main`, and a name that is not
`v<major>.<minor>.<patch>[-prerelease]`.

For `v0.2.0` it publishes one image, `ghcr.io/algojudge/algojudge-server`, under
four tags:

| | |
|---|---|
| `0.2.0` | the release |
| `0.2` | the moving minor |
| `0` | the moving major, and what an installation asks for by default |
| `latest` | |

**A prerelease publishes its own tag alone.** `v0.2.0-rc.1` gets `0.2.0-rc.1`
and nothing moving, because the point of a release candidate is that somebody
asked for it by name.

**The moving major is how an installation crosses a minor without being asked.**
`AlgoJudge-Ops` defaults every product image to `0` (`compose.yaml`), so the
next `update.sh` on any installation takes this image the moment the tag lands.
`0.2` is not compatible with `0.1` by decision, so **the release note is the
only warning an operator gets**, and what the Server changes on the wire has to
be in it.

The workflow builds, checks the image carries the application and `aj-admin`,
and pushes. It does **not** re-run the test suite: a tag points at a commit, and
that commit's own CI run is the evidence. It also does **not** create a GitHub
Release and writes no release notes — it holds `contents: read`, and the only
thing it writes is the package.

## The migrations are squashed into one, named for the release

**Before each release, every migration added since the previous release becomes
one migration named `version_<major>_<minor>_<patch>`.** A context that gained
no migration in the range gains none for that release: `LtiDbContext` holds
`version_0_1_0` alone after 0.2.0, because nothing touched it.

**Only unreleased migrations are ever squashed, and that is what makes the rule
safe.** A database at 0.1.0 carries `…_version_0_1_0` in its history and reaches
0.2.0 by applying `…_version_0_2_0` on top of it; no released history row is
ever removed, so no released database is ever stranded. The only database that
cannot cross a squash is one migrated from unreleased code — a developer's own
stack, whose history names files that no longer exist. Those are disposable:
`docker compose … down -v`.

**There are two ways to produce that one migration, and choosing wrongly loses
data silently.** A *collapse* regenerates from the model differ, and is right
only where the whole range is shape. An *assembly* concatenates the range's own
statements, and is required the moment any of them rewrites a row or renames
something. 0.1.0 was the first; 0.2.0 was the second. *How it is done* below
covers both, and **which one applies is decided by reading the range, not by the
version number**.

### Where they live

Two contexts, two chains, two history tables, and a command that names no
context gets the first.

| | migrations | model snapshot | history table |
|---|---|---|---|
| `ApplicationDbContext` | `AlgoJudge.Server/Database/Migrations` | `ApplicationDbContextModelSnapshot.cs` | `__EFMigrationsHistory` |
| `LtiDbContext` | `AlgoJudge.Server/Lti/Migrations` | `LtiDbContextModelSnapshot.cs` | `__EFMigrationsHistory_Lti` |

**Two and one since 2026-09-20.** `ApplicationDbContext` carries
`version_0_1_0` and `version_0_2_0`, `LtiDbContext` carries `version_0_1_0`
alone. The two classes sit in different namespaces, so a shared name does not
collide. Six migrations went into `version_0_2_0` and none into the LTI chain,
which is why that release added nothing there.

### What a regeneration silently drops

Three things, all of them measured on 2026-09-07 rather than remembered.

1. **The `FileContents` block**, at the end of `version_0_1_0.Up`, with its
   `DROP TABLE IF EXISTS` at the start of `Down`. It is not an EF entity — the
   postgres blob store reads and writes those bytes with raw SQL — so
   `dotnet ef migrations add` does not produce it, and neither does the
   `ALTER TABLE … SET STORAGE EXTERNAL` that keeps a ranged read seekable.
   **Copy it out of the file before deleting anything.** `FileStorageSchemaTests`
   is what fails if it goes.

2. **Three column defaults the model does not declare**: `EvaluationJobs.Releases`
   and `EvaluationJobs.Refunds` (`DEFAULT 0`), and `Instance.ShowHero`
   (`DEFAULT true`). Every one came from an `AddColumn(defaultValue: …)` whose
   job was to backfill a table that already had rows, and a table created in one
   statement has no rows to backfill. **Let them go** — the same decision as the
   eleven dropped in the 2026-08-28 squash. Each is matched by a CLR initializer
   (`int` is 0; `Instance.ShowHero` is `= true`), and putting them in the model
   with `HasDefaultValue` would be worse than losing them, because EF omits a
   property whose value equals the CLR default. They are the **expected**
   difference in the schema comparison below.

3. **The comments.** The summary on the migration class, which is written again
   rather than recovered, and the note above `ShowHero`'s `defaultValue: true`
   recording that the generator wrote `false` and that it was corrected by hand.
   That correction has no successor after a squash and needs none: on a database
   built from one migration the column arrives with the row.

   **A hand-written fragment is not automatically one to keep.** The question is
   whether it describes something outside the model — carry it — or only the way
   across from the previous version, which a fresh `CREATE TABLE` does not
   travel. `FileContents` is the first; the `ShowHero` correction is the
   second.

**Nothing else in either chain is hand-written.** Every check constraint, every
filtered index and `RunnerTags`' `defaultValueSql` is declared in the model —
`ApplicationDbContextModelSnapshot.cs` carries nine `HasFilter`, four
`HasCheckConstraint` and one `HasDefaultValueSql`, so the differ reproduces all
of them.

### How a collapse is done — the whole range is shape

**Read *Which of the two this release needs* below before starting this.** What
follows is the 0.1.0 procedure, and it is right only where nothing in the range
rewrites a row or renames anything.

Nothing here is a dry run. Do it on a branch, with the tree clean.

**0.** `dotnet tool restore` — `dotnet-ef` is pinned at 10.0.11 in
`.config/dotnet-tools.json`, with `rollForward: false`. Docker running.

**1. Record the schema the current chain builds**, from an empty database, into
a directory outside this repository:

```sh
compose="docker compose -f example-server-development-docker-compose.yaml"
$compose down -v
$compose up -d --build --wait
$compose exec -T postgres pg_dump --schema-only --no-owner --no-privileges \
    -U algojudge algojudge > ../before.sql
```

The stack applies migrations at start because it runs as Development. 2610 lines
on 2026-09-07.

**2. Copy the `FileContents` block out**, into that same directory. Not to a
stash and not to a branch: `git checkout` is how it gets lost.

**3. Delete the whole migrations directory for a context — snapshot included —
and regenerate:**

```sh
rm AlgoJudge.Server/Database/Migrations/*.cs
dotnet ef migrations add version_0_1_0 \
    --project AlgoJudge.Server --context ApplicationDbContext \
    --output-dir Database/Migrations

rm AlgoJudge.Server/Lti/Migrations/*.cs
dotnet ef migrations add version_0_1_0 \
    --project AlgoJudge.Server --context LtiDbContext \
    --output-dir Lti/Migrations
```

**The snapshot goes with them.** It is the differ's *before*: leave it in place
and the new migration comes out empty. **`--output-dir` is not optional** once
the directory is bare — EF follows the last migration's directory, and there is
no longer one to follow.

**4. Paste the `FileContents` block back**, with its comment: the `Sql` call at
the end of `Up`, the `DROP TABLE IF EXISTS` at the start of `Down`.

**5. Build and test.**

```sh
dotnet build AlgoJudge.sln -c Release -warnaserror
dotnet test AlgoJudge.sln -c Release --no-build
```

`MigrationsDescribeTheModelTests` proves the new snapshot still describes both
models; `FileStorageSchemaTests` proves `FileContents` came back and that its
`attstorage` is still `e`. A migration class named `version_0_1_0` compiles with
no warning under `-warnaserror` — checked 2026-09-07 against the same SDK and
the same project shape, so the lower-case name and the underscores cost nothing.

**6. Compare the two schemas.** This is the check that the squash is honest, and
reading the generated file is not a substitute for it.

```sh
$compose down -v
$compose up -d --build --wait
$compose exec -T postgres pg_dump --schema-only --no-owner --no-privileges \
    -U algojudge algojudge > ../after.sql
diff -u ../before.sql ../after.sql
```

**Read the diff; do not expect it to be empty.** One `CREATE TABLE` writes its
columns in model order where a chain appended them, so columns move. What is
allowed to differ, and nothing else:

- the three defaults above, gone;
- no other `DEFAULT` changed — `RunnerTags`' `'{}'::text[]` stays, because that
  one is in the model;
- `FileContents` still there, still `SET STORAGE EXTERNAL`.

On 2026-09-07 that came to fifty-two lines of `diff` output and nothing else:
`pg_dump`'s own session token, reordered columns in `EvaluationJobs` and
`Instance`, and those three `DEFAULT`s. Both dumps were 2610 lines, with 53
tables, 102 indexes, 4 check constraints and 61 foreign keys on each side.

**7. One history row per context.**

```sh
$compose exec -T postgres psql -U algojudge -d algojudge \
    -c 'SELECT * FROM "__EFMigrationsHistory"' \
    -c 'SELECT * FROM "__EFMigrationsHistory_Lti"'
```

Eight rows and one before, on 2026-09-07; one and one after.

**8.** `$compose down -v`, and regenerate `openapi.json` from a stack that is up
if anything about the API moved. The squash alone does not move it — checked on
2026-09-07, and the served document was identical to the committed one.

### Which of the two this release needs

Read the range before touching it:

```sh
git diff --name-only v<previous> -- AlgoJudge.Server/Database/Migrations
grep -c 'migrationBuilder.Sql' <each unreleased migration>
grep -n 'RenameTable\|RenameColumn\|RenameIndex' <each unreleased migration>
```

**A collapse is safe only when both counts are zero** and no released database
exists to carry. One `migrationBuilder.Sql` that touches a row, or one rename,
and it is an assembly. 0.2.0 had 23 of the first across three migrations and two
of the second, so it was an assembly.

**Why a collapse would have been wrong there, in three ways a schema comparison
cannot see.** A model differ emits DDL from a comparison of two models. It
therefore writes:

- **no data step at all.** The `UPDATE`s that carry an installation's existing
  permission keys from `template:` to `role:`, the two that add what the release
  grants the shipped `manager` role, and the one that links every grant that
  never diverged from the role it was made from — all gone. The schema is
  correct and the rows mean nothing.
- **a drop and a create for `RenameTable`.** `PermissionTemplates` → `Roles`
  regenerates as `DROP TABLE` plus `CREATE TABLE`. Every role in the
  installation goes, and the resulting schema is identical.
<!-- american-english: keep-start — the 0.1 column names, quoted as they were -->
- **a drop and an add for `RenameColumn`.** `AnonymiseAfter` and
  `SourceAnonymisedAt` regenerate as two columns dropped and two added. They
  arrive empty, and the resulting schema is identical.
<!-- american-english: keep-end -->

### How an assembly is done — the range rewrites rows or renames

**Nothing is regenerated.** The one migration is the range's own statements, in
the range's own order: `Up` is the unreleased `Up` bodies concatenated
chronologically, `Down` is the `Down` bodies concatenated in reverse. Every data
step and every rename survives because none of them is rewritten.

**1.** Assemble the bodies into `<timestamp>_version_<x>_<y>_<z>.cs`, with a
timestamp later than the last migration in the range. Mark each stretch with the
migration it came from — the comments inside the bodies come with them and stop
making sense unattributed.

**2. The Designer file is the last migration's**, with its `[Migration("…")]`
and its class name changed to the new one. It carries the model as of the end of
the range, which is what the new migration leaves behind.
**`ApplicationDbContextModelSnapshot.cs` is not touched at all** — it already
describes the current model, and this migration does not change the model.

**3. Check for a local that would collide.** Two bodies concatenated into one
method share a scope. EF writes almost none, but `var` in a body is a name that
now has to be unique across the whole range.

**4.** Delete the range's `.cs` and `.Designer.cs`, then
`dotnet build AlgoJudge.sln -c Release -warnaserror`.

**5. The proof, and it is not the schema comparison.** Generate what a database
standing at the previous release actually receives, before and after, and diff
them:

```sh
dotnet ef migrations script version_<previous> \
    --project AlgoJudge.Server --context ApplicationDbContext -o before.sql
# assemble, then the same command again into after.sql
diff -u before.sql after.sql
```

**Everything must be identical except the migration history.** Six migrations
become one, so five `INSERT INTO "__EFMigrationsHistory"` blocks and their
`COMMIT; START TRANSACTION;` pairs disappear and the last row's `MigrationId`
changes. Not one DDL statement and not one data statement may move. On
2026-09-20 that left **95 statements on each side, byte-identical** once the
history rows were stripped.

This is the check to run first and to trust. A regeneration that silently
dropped every `UPDATE` passes the schema comparison and fails this one on the
first line.

**6.** Then steps 6 and 7 of the collapse procedure as written — the schema from
an empty database, and the history rows. An assembly changes neither, so the
schema diff is **`pg_dump`'s session token and nothing else**: two lines out of
2972 on 2026-09-20, with no column reordering, because a delta applies the same
`ALTER`s in the same order rather than writing one `CREATE TABLE`.

**What a collapse has to worry about and an assembly does not.** Backfill values
for `AddColumn` on a table that now holds rows, and the hand-written fragments
of *What a regeneration silently drops* — both are carried across verbatim
because nothing was regenerated.

## Before the tag

- [ ] `Directory.Build.props` says the version being released. **`0.2.0` there
      on 2026-09-20.**
- [ ] `README.md` names that version where it shows a `docker pull`, and again
      in the sentence listing the four tags below it. **`git grep -n` the
      previous version rather than trusting a line number** — the one quoted
      here was three lines out by the next release.
- [ ] **The migrations are squashed into one per context for this release**,
      named for it, by the section above — *Which of the two this release needs*
      first. **Done for 0.2.0 on 2026-09-20**, as an assembly: six migrations
      into `version_0_2_0`, nothing added to the LTI chain, two history rows and
      one. The delta script was byte-identical either side at 95 statements, and
      the schema comparison differed only in `pg_dump`'s session token.
- [ ] **The commit is on `main`**, and **its** CI run is green — not a later
      one. `release/0.2.0` is not `main`, and the workflow refuses a tag that is
      not an ancestor of it, so the branch has to land there first. `main` was
      green at `b4b1455` on 2026-09-20 — run `35525016463`, read by id, because
      `gh run list --commit` returned nothing for it.
- [ ] `dotnet restore AlgoJudge.sln`, then
      `dotnet build AlgoJudge.sln -c Release --no-restore -warnaserror`. The
      release build treats **every warning as an error**; the count to aim at is
      zero, and it was zero on 2026-09-20 as it has been since 2026-08-29.
- [ ] `dotnet test AlgoJudge.sln -c Release --no-build`. Docker has to be
      running — the suite starts a real PostgreSQL 18 per run. 989 passed, none
      skipped, on 2026-09-20, in 3 m 9 s.

      **`RoleMigrationTests` is the one suite a squash breaks**, and it is the
      only thing in the repository that checks a *data* migration: it migrates
      to a named earlier migration, seeds an installation's rows, migrates
      forward and asserts what the `UPDATE`s did. A squash deletes the name it
      targets, so all three fail with *the migration … was not found*. Point
      `Previous` at the previous release's migration, which is the state they
      were always meant to start from and the only id a squash leaves standing.
- [ ] The development stack comes up and answers: the `compose` job in
      `.github/workflows/ci.yml` is the list, and the one to run by hand if
      anything about configuration changed.
- [ ] **`openapi.json` matches what the container serves.** Regenerate it from
      the running **development** stack — the Swagger endpoint is mapped under
      `IsDevelopment()` in `Program.cs`, so the released image with a production
      environment does not serve it at all. `curl` its
      `/api/v1/swagger/v1/swagger.json`, never a test host, and commit any
      difference; `README.md` carries the three commands, `--build` included.
      CI compares the two textually. Identical on 2026-09-20,
      `sha256 793eb42a…`, 171 paths and 215 schemas, `info.version` still `1.0`.
      **That checksum is what `AlgoJudge-Docs` pins**, so recompute it here
      rather than copying the previous release's.
- [ ] **Nothing is vulnerable, and what is behind is behind on purpose.**

      ```sh
      dotnet list AlgoJudge.sln package --vulnerable --include-transitive
      dotnet list AlgoJudge.sln package --outdated
      ```

      2026-09-20: **no vulnerable package in either project**, transitive
      included. Ten are behind by a patch or a minor — `AWSSDK.S3`, six
      `Microsoft.*` at 10.0.11 → 10.0.12, `Microsoft.IdentityModel.*` 8.22.0 →
      8.23.0, `Microsoft.NET.Test.Sdk` and `Testcontainers.PostgreSql`. None was
      taken here; whether to take them is the owner's call, and none is a reason
      to hold a release.

      **A dependency bump merged mid-release costs the migration proof.**
      Dependabot's group also raises `dotnet-ef`, and the tool version is
      written into the new migration's `ProductVersion` annotation and its
      history row. Merging it after the preparation commit means assembling and
      re-verifying the migration again, so take it before the branch or after
      the tag.

- [ ] **Every image this repository pins has been looked at**, and what is
      behind is behind for a reason somebody wrote down. There are five, in
      three files, and **two of them are pinned twice** — a bump that changes
      one copy and not the other is the failure this list exists to catch.
      `README.md`'s version table states four of the five in prose as well, and
      it went stale exactly that way on 2026-09-07. Both object stores were
      raised to their newest stable on 2026-09-20 and both `rustfs` copies
      agree.

      | | |
      |---|---|
      | `mcr.microsoft.com/dotnet/aspnet:10.0` | `AlgoJudge.Server/Dockerfile` |
      | `mcr.microsoft.com/dotnet/sdk:10.0` | `AlgoJudge.Server/Dockerfile` |
      | `postgres:18` | `example-server-development-docker-compose.yaml` |
      | `rustfs/rustfs:1.0.0` | that file **and** `S3BlobStoreTests.cs` |
      | `chrislusf/seaweedfs:4.47` | `S3BlobStoreTests.cs` |

      ```sh
      grep -rn 'image:' example-server-development-docker-compose.yaml
      grep -n '^FROM' AlgoJudge.Server/Dockerfile
      grep -n 'ContainerBuilder(' AlgoJudge.Server.Tests/S3BlobStoreTests.cs
      ```

      **A store this suite starts is not a store an installation runs**, so a
      newer one is taken when the suite agrees on it and left alone otherwise.
      `postgres:18` is different: the major is pinned deliberately, and 18 moved
      the data directory, so raising it is a migration question rather than a
      version bump. The two .NET images follow the target framework and move
      with it, not on their own.

      The actions the workflows use are pinned by major — `actions/checkout@v7`,
      `actions/setup-dotnet@v6` — and are worth the same glance.
- [ ] **The .NET version is the one this targets.** `net10.0` in both projects,
      `aspnet:10.0` and `sdk:10.0` in the Dockerfile, `10.0.x` on CI, and
      `10.0.401` locally on 2026-09-20. .NET 10 is the LTS; .NET 8 leaves
      support on 2026-11-10.
- [ ] **`.env.example`, checked in both directions.** Everything the development
      compose substitutes is listed, and nothing is listed that it does not
      substitute. Three on 2026-09-20 — `AJ_ADMIN_TOKEN`,
      `AJ_STORAGE_ACCESS_KEY`, `AJ_STORAGE_SECRET_KEY` — and the two sets match
      exactly. **Nothing checks this for you here**, so read the substitutions
      in the compose file against the keys in the file. **The Server's own
      configuration does not belong in it**: an installation is configured with
      `AJ_`-prefixed variables on the Server's environment, which `README.md`
      and the documentation site carry, and which no `.env` is involved in.
- [ ] **No `.env` in the repository, only `.env.example`.** Working tree *and*
      index, because `.gitignore` covering `.env` and `.env.*` does not
      un-track a file already added.

      ```sh
      find . -name '.env*' -not -path './.git/*'
      git ls-files | grep -i env
      ```

      Both named `.env.example` alone on 2026-09-20. If a real one turns up,
      report that it exists and do not open it.
- [ ] **The documentation describes the software as it is.** `README.md`,
      `AlgoJudge.Server/README.md`, `AUTHORS.md` and `AUTHORS.txt` — which
      `git shortlog -sne --all` still agrees with — `CLAUDE.md`, this file, and
      the comments in `example-server-development-docker-compose.yaml`,
      `AlgoJudge.Server/Dockerfile`, `AlgoJudge.Server/aj-admin` and the two
      workflows. `preconfig.example/pages/*.md` are an installation's own
      content, not documentation.

      **The one that goes stale every time is `CLAUDE.md`'s account of the
      migration chain**, which names the migrations by version and so is wrong
      the moment a release adds one. It was wrong on 2026-09-07 and corrected
      with that squash; corrected again on 2026-09-20, where it also gained why
      a squash is an assembly rather than a regeneration.

## Cutting the tag

The tag is what publishes; nothing that lands on `main` reaches the registry on
its own. Three things are true before it is cut, and each is read rather than
assumed:

```sh
git merge-base --is-ancestor <sha> origin/main              # it is on main
gh run list -R AlgoJudge/AlgoJudge-Server --commit <sha>    # its own run, green
git tag --list                                              # the name is free
```

**Its own run.** A later green run on `main` is evidence about a later commit,
and a release branch has no run at all — CI triggers on `main` and on pull
requests into it. That middle command has also returned **nothing at all** for a
commit whose run was running and then green; when it does, read the run by id
rather than concluding there was none.

The tag is annotated, and the message names the product:

```sh
git tag -a v<version> -m "AlgoJudge Server <version>" <sha>
git push origin v<version>
```

That push starts `.github/workflows/release.yml`, which takes about a minute.
Watch it — `gh run watch <id>` — rather than assuming it.

**Two things have no undo.** The run is never canceled: `cancel-in-progress` is
`false` here because a run interrupted between two `docker push` calls leaves a
version half in the registry. And **deleting a tag unpublishes nothing** — the
images of `v0.0.1-rc.1`, a tag deleted from the Runner's remote in August 2026,
are still in GHCR. The name is checked before the push or not at all.

Then the GitHub Release, which no workflow creates — `release.yml` holds
`contents: read`:

```sh
gh release create v<version> -R AlgoJudge/AlgoJudge-Server --title "<version>" --notes-file <file>
```

The title is the bare version, no `v`. `--prerelease` when the version carries
one; a prerelease publishes its own tag alone and moves the major, the minor and
`latest` onto nothing.

**A release body is not a file in this repository.** GitHub renders a single
newline as a line break, so each paragraph is written as one long line.

## After the tag

The image exists before an installation can pull it, so the order across
repositories is **Server and Client, then `AlgoJudge-Runner`, then
`AlgoJudge-External-Runner`, then `AlgoJudge-Ops`.**

The two Runners are not interchangeable in that order.
`AlgoJudge-External-Runner` pins `aj-protocol` at a Git revision of
`AlgoJudge-Runner` (`Cargo.toml:15`) and its own runbook says to move that pin
to the commit the Runner's tag points at — so it cannot be released until that
tag exists. `AlgoJudge-Ops` comes last because it pulls every image by tag,
`algojudge-server:${SERVER_TAG:-0}` among them (`compose.yaml:90`), and its own
CI says the full-stack check waits on the first release because
`algojudge-server:0` resolves to nothing (`.github/workflows/check.yml:97`).

### What this repository owes the others

- **The image, under all four tags.** Nothing downstream can be tested against a
  registry holding nothing.
- **The package has to be readable.** Nothing has ever been pushed, so its
  visibility on GitHub could not be checked here. `AlgoJudge-Ops` records that
  the first release includes a one-time flip of seven packages to public
  (`.github/workflows/check.yml:98`). Confirm it after the first push and before
  telling anybody to pull.
- **`openapi.json` and `events.json` at the released commit.**
  `AlgoJudge-Client` checks itself against both, in `scripts/check-api.mjs` and
  `scripts/check-events.mjs`. `AlgoJudge-Docs` fetches `openapi.json` by pinned
  ref and verifies a SHA-256 (`content-sources.json`), and says there that the
  pin becomes the tag once one exists — so hand over `v0.1.0` and the checksum
  of the file at it. On 2026-09-07 that pin was still a commit, `e01247c` of
  2026-09-04, five commits behind this branch's `openapi.json`.
- **Nothing else pins this repository's release number.** The Runners speak the
  wire contract, not the Server's version.

The documentation site cuts its `/server/` snapshot on release day, from
`AlgoJudge-Docs`.
### The public website states this component's version

`algojudge.pl` prints **`Server v<version>`** in four places — a card badge and a
roadmap item, in each of `src/content/pl.json` and `src/content/en.json` of
`AlgoJudge-Website`. A release makes all four wrong.

**`AlgoJudge-Website` has no CI.** Its fifteen tests run only when somebody types
`npm test`, so nothing reports the mismatch.

`AlgoJudge-Website/tests/content.test.mjs:125` pins the version literal by regex,
`Server v0\.1\.0`, and hard-codes the five repository keys. Correcting the content
turns that suite red: the test asserts the literal and needs the same edit. Change
content and test in one commit.

The correction is `/website-sync` in the workspace. This runbook's step is to
record that it is owed.
