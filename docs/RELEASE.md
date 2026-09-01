# Releasing the Server

For whoever cuts the release. Nothing here is addressed to somebody installing
the product — that is [AlgoJudge-Ops](https://github.com/AlgoJudge/AlgoJudge-Ops)
and the documentation site.

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

For `v0.1.0` it publishes one image, `ghcr.io/algojudge/algojudge-server`, under
four tags:

| | |
|---|---|
| `0.1.0` | the release |
| `0.1` | the moving minor |
| `0` | the moving major, and what an installation asks for by default |
| `latest` | |

**A prerelease publishes its own tag alone.** `v0.1.0-rc.1` gets `0.1.0-rc.1`
and nothing moving, because the point of a release candidate is that somebody
asked for it by name.

The workflow builds, checks the image carries the application and `aj-admin`,
and pushes. It does **not** re-run the test suite: a tag points at a commit, and
that commit's own CI run is the evidence.

## Before the tag

- [ ] `Directory.Build.props` says the version being released.
- [ ] `README.md` names that version where it shows a `docker pull`.
- [ ] The commit is on `main`, and **its** CI run is green — not a later one.
- [ ] `dotnet restore AlgoJudge.sln`, then
      `dotnet build AlgoJudge.sln -c Release --no-restore -warnaserror`. The
      release build treats **every warning as an error**; the count to aim at is
      zero, and it has been zero since 2026-08-29.
- [ ] `dotnet test AlgoJudge.sln -c Release --no-build`.
- [ ] The development stack comes up and answers: the `compose` job in
      `.github/workflows/ci.yml` is the list, and the one to run by hand if
      anything about configuration changed.
- [ ] **`openapi.json` matches what the container serves.** Regenerate it from
      the running container — `curl` its
      `/api/v1/swagger/v1/swagger.json` — never from a test host, and commit any
      difference. CI compares the two textually.
- [ ] `.env.example` lists every variable the development compose substitutes,
      and no others. Three today, and nothing checks this for you.
- [ ] The migration history is what a release should carry: no migration added
      that a released database cannot reach.

## After the tag

The image exists before an installation can pull it, so the order across
repositories is: **Server and Client, then the Runners, then Ops.**
`AlgoJudge-Ops` asks for the moving major `0` and cannot be tested against a
registry that has nothing in it.

The documentation site cuts its `/server/` snapshot on release day, from
`AlgoJudge-Docs`, and re-pins `openapi.json` at the released commit.
