#!/usr/bin/env bash
# deploy-local.sh — Build and deploy Listenarr to your-server.local
#
# Always deploys from the kevin/live branch — the personal integration branch
# that stacks all of Kevin's fixes and features on top of upstream canary.
# See CLAUDE.md "Branch Strategy" for the full explanation.
#
# Usage:
#   ./scripts/deploy-local.sh                      # Full deploy from kevin/live (tests → build → deploy → smoke)
#   ./scripts/deploy-local.sh --skip-tests         # Skip test run (hotfixes only)
#   ./scripts/deploy-local.sh --tag listenarr:abc  # Use an explicit image tag
#   ./scripts/deploy-local.sh --dry-run            # Show what would happen, don't actually deploy
#
# Requirements:
#   - Docker installed and running locally
#   - SSH access to your-server.local (key-based auth recommended)
#   - your-server.local has Docker and the compose file at /srv/listenarr/docker-compose.yml

set -euo pipefail

# ── Configuration ────────────────────────────────────────────────────────────
MEDIA_HOST="${MEDIA_HOST:-your-server.local}"
MEDIA_USER="${MEDIA_USER:-kevin}"
COMPOSE_DIR="/srv/listenarr"
CONFIG_DIR="${COMPOSE_DIR}/config"
TIMESTAMP=$(date +%Y%m%d-%H%M)
DEPLOY_BRANCH="kevin/live"          # always deploy from the integration branch
DEFAULT_TAG="listenarr:local-${TIMESTAMP}"
HEALTH_URL="http://${MEDIA_HOST}:4545/"
HEALTH_TIMEOUT=30   # seconds to wait for container to come up

# ── Flags ────────────────────────────────────────────────────────────────────
SKIP_TESTS=false
DRY_RUN=false
TAG="${DEFAULT_TAG}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-tests) SKIP_TESTS=true ;;
    --dry-run)    DRY_RUN=true ;;
    --tag)        TAG="$2"; shift ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
  shift
done

# ── Helpers ──────────────────────────────────────────────────────────────────
log()  { echo "[deploy] $*"; }
warn() { echo "[deploy] WARNING: $*" >&2; }
die()  { echo "[deploy] ERROR: $*" >&2; exit 1; }
ssh_media() { ssh "${MEDIA_USER}@${MEDIA_HOST}" "$@"; }

if [[ "$DRY_RUN" == true ]]; then
  log "DRY RUN — showing steps only, not executing"
fi

# ── Pre-flight ───────────────────────────────────────────────────────────────
log "Starting deploy: tag=${TAG}, skip_tests=${SKIP_TESTS}"

# Confirm we're in the repo root
[[ -f "listenarr.slnx" ]] || die "Run this script from the repo root (listenarr-src/)"

# Confirm kevin/live exists
git rev-parse --verify "${DEPLOY_BRANCH}" > /dev/null 2>&1 \
  || die "Branch '${DEPLOY_BRANCH}' does not exist. Create it first:\n  git checkout canary && git checkout -b kevin/live\n  Then merge your feature branches into it."

# Warn if not on kevin/live (we build from HEAD, so branch matters)
CURRENT_BRANCH=$(git symbolic-ref --short HEAD 2>/dev/null || echo "detached")
if [[ "$CURRENT_BRANCH" != "$DEPLOY_BRANCH" ]]; then
  warn "You are on '${CURRENT_BRANCH}', not '${DEPLOY_BRANCH}'."
  warn "Switching to ${DEPLOY_BRANCH} for the build..."
  if [[ "$DRY_RUN" == false ]]; then
    git checkout "${DEPLOY_BRANCH}"
  else
    log "[dry-run] Would switch to ${DEPLOY_BRANCH}"
  fi
fi

# Confirm SSH access to your-server.local
if [[ "$DRY_RUN" == false ]]; then
  ssh_media "echo 'SSH OK'" || die "Cannot SSH to ${MEDIA_HOST}. Check your SSH config."
fi

# Check for a config backup on your-server.local
log "Checking for config backup on your-server.local..."
if [[ "$DRY_RUN" == false ]]; then
  BACKUP_EXISTS=$(ssh_media "ls ${CONFIG_DIR}.bak 2>/dev/null && echo yes || echo no")
  if [[ "$BACKUP_EXISTS" != "yes" ]]; then
    warn "No config backup found at ${CONFIG_DIR}.bak"
    warn "Creating a backup now before proceeding..."
    ssh_media "cp -r ${CONFIG_DIR} ${CONFIG_DIR}.bak.${TIMESTAMP}" \
      && log "Backup created: ${CONFIG_DIR}.bak.${TIMESTAMP}" \
      || die "Could not create config backup. Aborting for safety."
  else
    log "Config backup exists — proceeding"
  fi
fi

# Note current image for rollback reference
if [[ "$DRY_RUN" == false ]]; then
  CURRENT_IMAGE=$(ssh_media "docker ps --filter name=listenarr --format '{{.Image}}' 2>/dev/null || echo unknown")
  log "Current live image: ${CURRENT_IMAGE} (rollback target if needed)"
fi

# ── Tests ────────────────────────────────────────────────────────────────────
if [[ "$SKIP_TESTS" == true ]]; then
  warn "Skipping tests (--skip-tests flag set)"
else
  log "Running tests..."
  if [[ "$DRY_RUN" == false ]]; then
    (cd tests && dotnet test --logger "console;verbosity=minimal") \
      || die "Tests failed — aborting deploy. Use --skip-tests to override."
    log "Tests passed"
  else
    log "[dry-run] Would run: cd tests && dotnet test"
  fi
fi

# ── Build ────────────────────────────────────────────────────────────────────
log "Building Docker image: ${TAG}"
if [[ "$DRY_RUN" == false ]]; then
  docker build -t "${TAG}" . \
    || die "Docker build failed"
  log "Build complete"
else
  log "[dry-run] Would run: docker build -t ${TAG} ."
fi

# ── Transfer image to your-server.local ────────────────────────────────────────────
log "Transferring image to ${MEDIA_HOST}..."
if [[ "$DRY_RUN" == false ]]; then
  docker save "${TAG}" | ssh "${MEDIA_USER}@${MEDIA_HOST}" "docker load" \
    || die "Image transfer failed"
  log "Image transferred"
else
  log "[dry-run] Would run: docker save ${TAG} | ssh ${MEDIA_USER}@${MEDIA_HOST} docker load"
fi

# ── Update compose and restart ───────────────────────────────────────────────
log "Updating compose file and restarting container on ${MEDIA_HOST}..."
if [[ "$DRY_RUN" == false ]]; then
  ssh_media "
    cd ${COMPOSE_DIR}
    # Update the image tag in docker-compose.yml
    sed -i.bak \"s|image: listenarr:.*|image: ${TAG}|\" docker-compose.yml
    # Restart
    docker compose down
    docker compose up -d
  " || die "Failed to restart container on your-server.local"
  log "Container restarted"
else
  log "[dry-run] Would update image tag in compose and restart container on ${MEDIA_HOST}"
fi

# ── Health check ─────────────────────────────────────────────────────────────
log "Waiting for container to become healthy (up to ${HEALTH_TIMEOUT}s)..."
if [[ "$DRY_RUN" == false ]]; then
  ELAPSED=0
  until curl -sf "${HEALTH_URL}" > /dev/null 2>&1; do
    if [[ $ELAPSED -ge $HEALTH_TIMEOUT ]]; then
      die "Container did not become healthy within ${HEALTH_TIMEOUT}s. Check logs: ssh ${MEDIA_USER}@${MEDIA_HOST} 'docker logs listenarr'"
    fi
    sleep 2
    ELAPSED=$((ELAPSED + 2))
  done
  log "Health check passed (${ELAPSED}s)"
else
  log "[dry-run] Would poll ${HEALTH_URL} for up to ${HEALTH_TIMEOUT}s"
fi

# ── Smoke test ───────────────────────────────────────────────────────────────
log "Running smoke tests..."
if [[ "$DRY_RUN" == false ]]; then
  ./scripts/smoke-test.sh || warn "Smoke tests had failures — check output above"
else
  log "[dry-run] Would run: ./scripts/smoke-test.sh"
fi

# ── Done ─────────────────────────────────────────────────────────────────────
log "Deploy complete!"
log "  Image:       ${TAG}"
log "  Live URL:    https://your-host.example"
log "  Rollback:    ssh ${MEDIA_USER}@${MEDIA_HOST} 'cd ${COMPOSE_DIR} && sed -i \"s|image: .*|image: ${CURRENT_IMAGE:-<previous-tag>}|\" docker-compose.yml && docker compose up -d'"
