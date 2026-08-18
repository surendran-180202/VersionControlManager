# VersionControlManager

A .NET 9 console application that copies a GitHub repository — **including its full
check-in history** — into Azure DevOps.

## Before you start: passwords vs. tokens

GitHub and Azure DevOps both stopped accepting account passwords for Git and API access.
The value you supply as a "password" must be a **personal access token (PAT)**:

| Service | Where to create it | Scope required |
| --- | --- | --- |
| GitHub | Settings → Developer settings → Personal access tokens | `repo` (for private repositories) |
| Azure DevOps | User settings → Personal access tokens | **Code** (read, write, and manage) |

An account password will be rejected by the service, and the tool reports that as an
authentication failure.

## Running it

Build once:

```bash
dotnet build
```

Run with no arguments to be prompted for everything. Passwords are not echoed:

```bash
dotnet run
```

```
  -- GitHub (source) --
  GitHub repository URL: https://github.com/octocat/Hello-World
  GitHub username: octocat
  GitHub password / token:

  -- Azure DevOps (target) --
  Azure DevOps project URL: https://dev.azure.com/contoso/Payments
  Azure DevOps username: octocat
  Azure DevOps password / token:
```

Or supply everything up front:

```bash
dotnet run -- --github-url https://github.com/octocat/Hello-World --github-user octocat --github-password ghp_xxx --azure-url https://dev.azure.com/contoso/Payments --azure-user octocat --azure-password abc123
```

To keep tokens out of your shell history, use environment variables instead:

```bash
export VCM_GITHUB_PASSWORD=ghp_xxx
export VCM_AZURE_PASSWORD=abc123
```

Precedence, highest first: **command line → environment → appsettings.json → prompt.**

Run `--help` for the full option list.

## What it does

```
 1  Check git is installed
 2  Read the source repository from the GitHub API      (fail fast on a bad URL or token)
 3  Read the target project from the Azure DevOps API
 4  Create the target repository, or confirm it is empty
 5  git clone --mirror                                  <- the full history arrives here
 6  git push refs/heads/* refs/tags/* refs/notes/*      <- and goes out here
 7  Set the default branch to match GitHub
 8  Read the refs back from Azure DevOps and compare
```

### How the history is preserved

A **bare mirror clone** copies every object and every ref verbatim, and the push transfers
those same objects. Nothing is replayed, rebuilt, or rewritten, so on the target you get
identical commit SHAs, and the original authors, committers, dates, parents, and messages.
Merge commits keep both parents. Annotated tags keep their tagger and message. `git notes`
are carried across.

### Why not `git push --mirror`?

Because it fails on real GitHub repositories. A mirror clone also pulls down GitHub's
pull-request refs, and Azure DevOps rejects that namespace as reserved. This is not a
corner case — cloning `octocat/Hello-World` yields:

| Ref namespace | Count |
| --- | --- |
| `refs/heads/*` (branches) | 3 |
| `refs/pull/*` (GitHub PR refs) | 3,452 |

`push --mirror` would try to push all 3,455 refs and fail the migration. This tool pushes
explicit refspecs for branches, tags, and notes instead.

## How credentials are handled

Tokens are passed to git through `GIT_CONFIG_KEY_n` / `GIT_CONFIG_VALUE_n` environment
variables on the child process, which avoids the two usual leaks:

- a token in the remote URL gets **persisted into `.git/config`**;
- a token in `git -c http.extraHeader=...` is **visible in any process listing**.

All console output — including git's own stdout and stderr — is passed through a redactor
that masks registered secrets, so a token cannot reach the screen or a CI log. Git
Credential Manager is disabled for these calls so it cannot substitute a cached identity
or open a dialog, and `GIT_TERMINAL_PROMPT=0` ensures a credential prompt fails fast
instead of looking like a hang.

The temporary mirror clone is deleted on both the success and failure paths.

## Safety behaviour

- If the target repository **already contains commits**, the tool refuses to push rather
  than risk overwriting history. Use `--target-repo <name>` to create a separate
  repository, or `--allow-existing` to override deliberately.
- If the repository uses **Git LFS**, the history migrates but the LFS payloads do not —
  the tool warns and tells you to re-run with `--lfs`.
- After pushing, branch and tag counts are **read back from Azure DevOps** and compared
  with the source, so success is confirmed by the server rather than assumed.

## URL formats accepted

**GitHub** — HTTPS, SSH, `owner/repo` shorthand, deep links, `.git` suffix optional, and
GitHub Enterprise Server (which uses `/api/v3` on its own host).

**Azure DevOps** — `dev.azure.com/org/project`, with or without `/_git/repo`; the legacy
`org.visualstudio.com` form (normalised to `dev.azure.com`, because that redirect would
otherwise strip the auth header); and on-premises `server/tfs/collection/project`,
including a non-default port. Usernames and query strings in the URL are stripped.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Configuration or usage error |
| 2 | Authentication failure |
| 3 | Source (GitHub) error |
| 4 | Target (Azure DevOps) error |
| 5 | Git error |
| 6 | Cancelled |
| 99 | Unexpected error (not one the tool anticipated) |

## Project layout

```
Program.cs                Entry point, exit codes, help
Configuration/            Options, argument/env/file resolution, masked prompts
Vcs/                      GitHub and Azure DevOps URL parsing
Clients/                  REST clients for both services
Git/                      git process runner, mirror clone and push
Migration/                Pipeline, temp workspace, error types
Logging/                  Console output with secret redaction
```
