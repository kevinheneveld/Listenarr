#!/usr/bin/env bash
# sync-upstream.sh — Sync your local canary branch from upstream (origin/canary)
#
# Usage:
#   ./scripts/sync-upstream.sh              # Sync and push to fork
#   ./scripts/sync-upstream.sh --no-push    # Sync locally only, don't push to fork
#   ./scripts/sync-upstream.sh --report     # Show divergence report only, don't sync
#
# This script:
#   1. Fetches from origin (upstream: therobbiedavis/Listenarr)
#   2. Shows new upstream commits since your last sync
#   3. Merges origin/canary into your local canary
#   4. Pushes the updated canary to your fork (kevinheneveld/Listenarr)

set -euo pipefail

UPSTREAM_REMOTE="origin"
FORK_REMOTE="fork"
BRANCH="canary"
NO_PUSH=false
REPORT_ONLY=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-push)   NO_PUSH=true ;;
    --report)    REPORT_ONLY=true; NO_PUSH=true ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
  shift
done

log()  { echo "[sync-upstream] $*"; }
warn() { echo "[sync-upstream] WARNING: $*" >&2; }

CURRENT_BRANCH=$(git symbolic-ref --short HEAD)

# ── Fetch ─────────────────────────────────────────────────────────────────────
log "Fetching from ${UPSTREAM_REMOTE} (upstream)..."
git fetch "${UPSTREAM_REMOTE}"
git fetch "${FORK_REMOTE}" 2>/dev/null || true

# ── Divergence report ─────────────────────────────────────────────────────────
UPSTREAM_NEW=$(git log "${BRANCH}..${UPSTREAM_REMOTE}/${BRANCH}" --oneline 2>/dev/null || echo "")
YOUR_AHEAD=$(git log "${UPSTREAM_REMOTE}/${BRANCH}..${BRANCH}" --oneline 2>/dev/null || echo "")

echo ""
echo "────────────────────────────────────────────────────────"
echo "Divergence report: ${BRANCH} vs ${UPSTREAM_REMOTE}/${BRANCH}"
echo "────────────────────────────────────────────────────────"

if [[ -z "$UPSTREAM_NEW" ]]; then
  echo "  Your canary is up to date with upstream."
else
  echo "  New upstream commits (need to merge in):"
  echo "$UPSTREAM_NEW" | sed 's/^/    /'
fi

echo ""

if [[ -z "$YOUR_AHEAD" ]]; then
  echo "  No local canary commits ahead of upstream."
else
  echo "  Your canary commits not yet upstream:"
  echo "$YOUR_AHEAD" | sed 's/^/    /'
fi
echo "────────────────────────────────────────────────────────"
echo ""

if [[ "$REPORT_ONLY" == true ]]; then
  log "Report complete (--report flag set, no changes made)"
  exit 0
fi

if [[ -z "$UPSTREAM_NEW" ]]; then
  log "Already up to date — nothing to merge"
  exit 0
fi

# ── Stash any local changes before switching ──────────────────────────────────
STASH_NEEDED=false
if [[ -n "$(git status --porcelain)" ]]; then
  log "Stashing local changes..."
  git stash push -m "sync-upstream auto-stash $(date +%Y%m%d-%H%M)"
  STASH_NEEDED=true
fi

# ── Merge ─────────────────────────────────────────────────────────────────────
log "Switching to ${BRANCH}..."
git checkout "${BRANCH}"

log "Merging ${UPSTREAM_REMOTE}/${BRANCH}..."
git merge "${UPSTREAM_REMOTE}/${BRANCH}" || {
  warn "Merge conflict — resolve conflicts, then run: git merge --continue"
  warn "Your branch was left at: ${BRANCH}"
  exit 1
}

# ── Push to fork ──────────────────────────────────────────────────────────────
if [[ "$NO_PUSH" == false ]]; then
  log "Pushing updated ${BRANCH} to ${FORK_REMOTE}..."
  git push "${FORK_REMOTE}" "${BRANCH}"
  log "Fork is now in sync with upstream"
else
  warn "--no-push set — fork not updated"
fi

# ── Return to original branch ─────────────────────────────────────────────────
if [[ "$CURRENT_BRANCH" != "$BRANCH" ]]; then
  log "Returning to ${CURRENT_BRANCH}..."
  git checkout "${CURRENT_BRANCH}"
fi

# ── Restore stash ─────────────────────────────────────────────────────────────
if [[ "$STASH_NEEDED" == true ]]; then
  log "Restoring stashed changes..."
  git stash pop
fi

# ── Done ──────────────────────────────────────────────────────────────────────
UPSTREAM_HEAD=$(git rev-parse --short "${UPSTREAM_REMOTE}/${BRANCH}")
log "Sync complete — upstream canary is now at ${UPSTREAM_HEAD}"
log ""
log "If you have a feature branch, rebase it:"
log "  git checkout feat/your-branch"
log "  git rebase canary"
log ""
log "Update upstream tracking log:"
log "  ~/.openclaw/workspace/projects/listenarr/UPSTREAM.md"
