# Listenarr — Kevin's Development Guide

> This is Kevin's personal CLAUDE.md for working in this fork.
> Upstream AI/agent guidelines (security rules, coding conventions) live in `.github/CLAUDE_LISTENARR.md`, `.github/AGENTS.md`, and `.github/CLAUDE.md` — read those for project-level standards.

---

## What Is This

Kevin's fork of [therobbiedavis/Listenarr](https://github.com/therobbiedavis/Listenarr) — an automated audiobook management system (C# .NET 8 backend + Vue 3 frontend). Kevin runs a live instance on his home network, tracks bugs and features as GitHub Issues, and submits PRs back to the upstream author.

| | |
|---|---|
| **Upstream repo** | https://github.com/therobbiedavis/Listenarr |
| **Kevin's fork** | https://github.com/kevinheneveld/Listenarr |
| **Live instance** | https://your-host.example (your-server.local port 4545) |
| **Compose file** | `/srv/listenarr/docker-compose.yml` on your-server.local |
| **Config + DB** | `/srv/listenarr/config/` on your-server.local |
| **Issue tracker** | https://github.com/kevinheneveld/Listenarr/issues |
| **Project docs** | `~/.openclaw/workspace/projects/listenarr/` |

---

## Git Remote Setup

```
origin  → therobbiedavis/Listenarr  (upstream — fetch/pull only)
fork    → kevinheneveld/Listenarr   (Kevin's fork — push branches here)
```

**Rule: always push branches to `fork`. Never push to `origin`.**

---

## Branch Strategy

Three branches, three purposes — keep them distinct:

| Branch | Purpose | Deployed? | PR target? |
|---|---|---|---|
| `canary` | Clean mirror of upstream. Never commit here directly. | No | No |
| `feat/*` / `fix/*` | Individual changes cut from `canary`. Kept minimal and focused. | No | **Yes** — these go upstream |
| `kevin/live` | Personal integration layer. All Kevin's fixes + features stacked on canary. | **Yes** | No |

### The rule Opus will remind you of

> "Cut from `canary`, PR to `canary`" — this refers to PR branches only.  
> **You deploy from `kevin/live`, not from PR branches.**

### Starting a new fix or feature

```bash
# 1. Make sure canary is current
./scripts/sync-upstream.sh

# 2. Cut your PR branch from canary
git checkout canary
git checkout -b fix/short-description

# 3. Do the work, then also bring it into kevin/live
git checkout kevin/live
git merge fix/short-description    # or cherry-pick if you only want specific commits
git push fork kevin/live

# 4. Deploy kevin/live to your-server.local
./scripts/deploy-local.sh
```

### `kevin/live` commit convention

Two types of commits live in `kevin/live`:
- **Upstream-worthy**: the actual fix/feature commit (identical to what's in the PR branch). These will eventually merge upstream and disappear from the diff.
- **Personal-only**: prefix with `[personal]` — e.g., `[personal] default sort by date added`. These are UI preferences, behavior tweaks, or setup-specific things that would never make sense as upstream PRs. Know that these will always need to be rebased over.

### Reconciling `kevin/live` with new canary

Pick the right tool for the size of the advance:

**Small advance (handful of upstream commits)** — rebase, for a linear history:

```bash
./scripts/sync-upstream.sh          # brings canary current
git checkout kevin/live
git rebase canary                   # replay kevin/live commits on top of new canary
# Resolve conflicts — [personal] commits are the most likely conflict sources
git push fork kevin/live --force-with-lease
```

**Large advance (dozens of upstream commits, especially when upstream has done architectural refactors)** — merge, because rebasing the same conflict per replayed commit becomes a multi-hour grind:

```bash
./scripts/sync-upstream.sh
git checkout kevin/live
git checkout -b kevin/live-merge-test     # work in a throwaway first
git merge canary                          # resolve all conflicts in one pass
# build + test + browser-smoke, then:
git checkout kevin/live
git merge --ff-only kevin/live-merge-test
git push fork kevin/live --force-with-lease   # if history was rewritten elsewhere
git branch -D kevin/live-merge-test
```

The merge commit lingers in kevin/live history forever, but for a large catch-up that's a fair price vs. rebasing per-commit. After a large merge, return to the small-rebase rhythm for subsequent canary advances.

### Check divergence from upstream

```bash
git fetch origin
git log canary..origin/canary --oneline     # upstream commits you don't have yet
git log origin/canary..canary --oneline     # your commits not yet in upstream
git log canary..kevin/live --oneline        # everything in kevin/live beyond canary
```

### Sync canary from upstream

```bash
# Automated (preferred):
./scripts/sync-upstream.sh

# Manual:
git checkout canary
git fetch origin
git merge origin/canary
git push fork canary
```

---

## Build & Test

```bash
# Install all dependencies (first time or after upstream changes)
npm install
cd fe && npm install && cd ..

# Run in development mode (hot-reload for both API and frontend)
npm run dev

# Run backend tests only
cd tests && dotnet test && cd ..

# Build Docker image with a timestamped tag
docker build -t listenarr:local-$(date +%Y%m%d-%H%M) .
```

---

## Deploy to Live (your-server.local)

The live instance runs as a Docker container on `your-server.local`. See `scripts/deploy-local.sh` for the full automated flow.

```bash
# Full deploy (runs tests first, then builds and deploys)
./scripts/deploy-local.sh

# Skip tests — hotfixes only
./scripts/deploy-local.sh --skip-tests

# Provide an explicit image tag
./scripts/deploy-local.sh --tag listenarr:my-tag

# Smoke test the live instance without deploying
./scripts/smoke-test.sh
```

**Deploy checklist (always verify before deploying):**
1. Config backup exists at `/srv/listenarr/config/` on your-server.local
2. Previous image tag is noted (rollback target)
3. Tests pass
4. CHANGELOG.md updated

---

## Issue Tracking

GitHub Issues on Kevin's fork are the source of truth. A local mirror lives at:
`~/.openclaw/workspace/projects/listenarr/ISSUES.md`

The local mirror is for quick reference and AI context — GitHub is canonical.

```bash
# gh CLI is installed and authenticated as kevinheneveld

# List open bugs and features
gh issue list --repo kevinheneveld/Listenarr

# Create a bug report
gh issue create \
  --repo kevinheneveld/Listenarr \
  --label bug \
  --title "Short description" \
  --body "Steps to reproduce, expected vs actual behavior, logs if available"

# Create a feature request
gh issue create \
  --repo kevinheneveld/Listenarr \
  --label enhancement \
  --title "Short description" \
  --body "What problem does this solve? What should it do?"

# Check PR status (your PRs against upstream)
gh pr list --repo therobbiedavis/Listenarr --author kevinheneveld

# Watch upstream PRs (new features or fixes to pull in)
gh pr list --repo therobbiedavis/Listenarr --state open
```

---

## PR Workflow

1. **Sync canary first**: `./scripts/sync-upstream.sh`
2. **Branch from canary**: `git checkout canary && git checkout -b feat/my-feature`
3. **Make changes** — follow patterns in `.github/CLAUDE_LISTENARR.md`
4. **Update CHANGELOG.md** (required — see `.github/CLAUDE.md` for format)
5. **Run tests**: `cd tests && dotnet test`
6. **Push to fork**: `git push fork feat/my-feature`
7. **Open PR upstream**: `gh pr create --repo therobbiedavis/Listenarr --base canary`

### Before opening a PR — check upstream for conflicts
```bash
# See if anyone else is working on the same area
gh pr list --repo therobbiedavis/Listenarr --state open
# Review recent merged PRs for overlap
gh pr list --repo therobbiedavis/Listenarr --state merged --limit 10
```

---

## Key Constraints

| Constraint | Detail |
|---|---|
| Single live instance | Never run two active Listenarr containers on your-server.local simultaneously |
| Backup before deploy | Confirm `/srv/listenarr/config/` is backed up before swapping images |
| Rollback path | Keep the previous image tag available until new one is verified healthy |
| Changelog required | Every meaningful change needs a `CHANGELOG.md` entry before merging |
| Scoped PRs | One logical concern per PR — don't bundle deploy workarounds with features |
| Push to fork only | `git push fork <branch>` — never `git push origin` |

---

## Project Structure

```
listenarr.api/            ASP.NET Core Web API (entry point, controllers, middleware)
listenarr.application/    Application layer (use cases, services)
listenarr.domain/         Domain models and interfaces
listenarr.infrastructure/ Data access, external clients (download clients, metadata)
fe/                       Vue 3 + TypeScript frontend (Pinia, Vite, SignalR)
tests/                    .NET test suite
scripts/                  Deploy and utility scripts (deploy-local.sh, smoke-test.sh, etc.)
.github/                  Upstream AI/agent guidelines (read these for coding standards)
```

---

## Common Operations Quick Reference

| Task | Command |
|---|---|
| Start dev environment | `npm run dev` |
| Run tests | `cd tests && dotnet test` |
| Deploy to live | `./scripts/deploy-local.sh` |
| Smoke test live | `./scripts/smoke-test.sh` |
| Sync from upstream | `./scripts/sync-upstream.sh` |
| List open issues | `gh issue list --repo kevinheneveld/Listenarr` |
| List your upstream PRs | `gh pr list --repo therobbiedavis/Listenarr --author kevinheneveld` |
| Check upstream PRs | `gh pr list --repo therobbiedavis/Listenarr` |
