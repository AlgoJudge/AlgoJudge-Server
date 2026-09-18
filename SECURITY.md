# Security policy

## Reporting a vulnerability

Report vulnerabilities privately. Do not open a public issue.

- **On GitHub:** open the **Security** tab of the affected repository and select
  **Report a vulnerability**.
- **By email:** contact@algojudge.pl.

Describe what an attacker can do, what access they need, and how to reproduce
it. If you are not sure which repository is affected, use any of them.

We will confirm that we received your report, keep you informed while we work
on a fix, and credit you in the published advisory unless you prefer not to be
named. There is no guaranteed response time.

## Supported versions

Security fixes are released as a new version of the latest release line. Older
release lines do not receive fixes.

AlgoJudge is at version 0.x, and a new minor version can break compatibility.
Read the release notes before you upgrade.

## Scope

AlgoJudge runs code that its users submit. Anything that lets a submission
escape its sandbox, read another submission, reach the network, or affect the
host is in scope. The Runner's threat model is in
[AlgoJudge-Runner/docs/SECURITY.md](https://github.com/AlgoJudge/AlgoJudge-Runner/blob/main/docs/SECURITY.md).

So is anything that lets a user sign in as someone else, or read or change data
that their permissions do not allow.

A vulnerability in a dependency belongs upstream, unless AlgoJudge makes it
exploitable.
