#!/usr/bin/env bash
# deploy-local.sh — Build and deploy Listenarr to the live Docker container on media
#
# Always deploys from the kevin/live branch — the personal integration branch
# that stacks all of Kevin's fixes and features on top of upstream canary.
# See CLAUDE.md "Branch Strategy" for the full explanation.
#
# This script is [personal] infrastructure — it reflects a build-on-remote
# setup (the local machine has no Docker; the image is built on the live host
# via SSH). Host-specific values (SSH alias, IP) are loaded from a gitignored
# `.deploy-local.env` at the repo root — see `.deploy-local.env.example`.
#
# Other assumptions baked in:
#   - The remote host runs the standalone `docker-compose` v2.x binary,
#     not the `docker compose` plugin.
#   - The compose file lives at /srv/listenarr/docker-compose.yml on the host.
#
# Usage:
#   ./scripts/deploy-local.sh                      # Full deploy from kevin/live (sync source → build on host → swap compose → health → smoke)
#   ./scripts/deploy-local.sh --skip-tests         # Skip backend test run (hotfixes only)
#   ./scripts/deploy-local.sh --tag listenarr:abc  # Use an explicit image tag
#   ./scripts/deploy-local.sh --dry-run            # Show what would happen, don't actually deploy
#
# Requirements:
#   - .deploy-local.env populated (copy from .deploy-local.env.example)
#   - SSH access to the configured MEDIA_SSH host; Docker on that host
#   - rsync installed locally
#   - dotnet installed locally if running tests (--skip-tests bypasses)

set -euo pipefail

# ── Configuration ────────────────────────────────────────────────────────────
# Load host-specific values from gitignored env file if present. Otherwise
# expect MEDIA_SSH / MEDIA_IP to be set in the calling environment.
SCRIPT_DIR="$( cd -- "$( dirname -- "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )"
REPO_ROOT="$( cd -- "${SCRIPT_DIR}/.." &> /dev/null && pwd )"
if [[ -f "${REPO_ROOT}/.deploy-local.env" ]]; then
  # shellcheck disable=SC1091
  source "${REPO_ROOT}/.deploy-local.env"
fi

: "${MEDIA_SSH:?MEDIA_SSH not set — copy .deploy-local.env.example to .deploy-local.env and fill it in}"
: "${MEDIA_IP:?MEDIA_IP not set — copy .deploy-local.env.example to .deploy-local.env and fill it in}"
BUILD_DIR_REMOTE="${BUILD_DIR_REMOTE:-/root/listenarr-build/listenarr-src}"
COMPOSE_DIR="/srv/listenarr"
CONFIG_DIR="${COMPOSE_DIR}/config"
TIMESTAMP=$(date +%Y%m%d-%H%M)
DEPLOY_BRANCH="${DEPLOY_BRANCH:-kevin/live}"
DEFAULT_TAG="listenarr:local-${TIMESTAMP}"
HEALTH_URL="http://${MEDIA_IP}:4545/"
HEALTH_TIMEOUT=30

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
ssh_media() { ssh "${MEDIA_SSH}" "$@"; }

if [[ "$DRY_RUN" == true ]]; then
  log "DRY RUN — showing steps only, not executing"
fi

# ── Pre-flight ───────────────────────────────────────────────────────────────
log "Starting deploy: tag=${TAG}, skip_tests=${SKIP_TESTS}"

# Confirm we're in the repo root
[[ -f "listenarr.slnx" ]] || die "Run this script from the repo root (listenarr-src/)"

# Confirm kevin/live exists
git rev-parse --verify "${DEPLOY_BRANCH}" > /dev/null 2>&1 \
  || die "Branch '${DEPLOY_BRANCH}' does not exist. Create it first:
  git checkout canary && git checkout -b kevin/live
  Then merge your feature branches into it."

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

# Confirm SSH access to media
if [[ "$DRY_RUN" == false ]]; then
  ssh_media "echo SSH OK" > /dev/null || die "Cannot SSH to '${MEDIA_SSH}'. Check ~/.ssh/config."
fi

# ── Config backup ────────────────────────────────────────────────────────────
log "Checking for a recent config backup on media..."
if [[ "$DRY_RUN" == false ]]; then
  # Real backups are timestamped: ${CONFIG_DIR}.bak.YYYYMMDD-HHMM
  BACKUP_FOUND=$(ssh_media "ls -d ${CONFIG_DIR}.bak.* 2>/dev/null | tail -1 || true")
  if [[ -z "$BACKUP_FOUND" ]]; then
    warn "No timestamped backup found matching ${CONFIG_DIR}.bak.*"
    warn "Creating one now before proceeding..."
    ssh_media "cp -r ${CONFIG_DIR} ${CONFIG_DIR}.bak.${TIMESTAMP}" \
      && log "Backup created: ${CONFIG_DIR}.bak.${TIMESTAMP}" \
      || die "Could not create config backup. Aborting for safety."
  else
    log "Existing backup found: ${BACKUP_FOUND}"
  fi
fi

# Note current image for rollback reference
if [[ "$DRY_RUN" == false ]]; then
  CURRENT_IMAGE=$(ssh_media "docker ps --filter name=listenarr --format '{{.Image}}'" 2>/dev/null || echo "unknown")
  log "Current live image: ${CURRENT_IMAGE} (rollback target if needed)"
fi

# ── Tests ────────────────────────────────────────────────────────────────────
if [[ "$SKIP_TESTS" == true ]]; then
  warn "Skipping tests (--skip-tests flag set)"
else
  log "Running backend tests..."
  if [[ "$DRY_RUN" == false ]]; then
    (cd tests && dotnet test --logger "console;verbosity=minimal") \
      || die "Tests failed — aborting deploy. Use --skip-tests to override."
    log "Backend tests passed"
  else
    log "[dry-run] Would run: cd tests && dotnet test"
  fi
fi

# ── Sync source to media ─────────────────────────────────────────────────────
log "Syncing source to ${MEDIA_SSH}:${BUILD_DIR_REMOTE}..."
if [[ "$DRY_RUN" == false ]]; then
  ssh_media "mkdir -p '${BUILD_DIR_REMOTE}'"
  rsync -a --delete \
    --exclude='node_modules' \
    --exclude='fe/node_modules' \
    --exclude='fe/dist' \
    --exclude='fe/cypress/screenshots' \
    --exclude='fe/cypress/videos' \
    --exclude='**/bin/' \
    --exclude='**/obj/' \
    --exclude='.git/objects/pack' \
    ./ "${MEDIA_SSH}:${BUILD_DIR_REMOTE}/" \
    || die "rsync to ${MEDIA_SSH} failed"
  log "Source synced"
else
  log "[dry-run] Would rsync ./ to ${MEDIA_SSH}:${BUILD_DIR_REMOTE}/"
fi

# ── Build on media ───────────────────────────────────────────────────────────
log "Building Docker image on ${MEDIA_SSH}: ${TAG}"
if [[ "$DRY_RUN" == false ]]; then
  ssh_media "cd '${BUILD_DIR_REMOTE}' && docker build -t '${TAG}' ." \
    || die "Docker build on ${MEDIA_SSH} failed"
  log "Build complete"
else
  log "[dry-run] Would run: ssh ${MEDIA_SSH} 'cd ${BUILD_DIR_REMOTE} && docker build -t ${TAG} .'"
fi

# ── Update compose and restart ───────────────────────────────────────────────
log "Updating compose file and restarting container on ${MEDIA_SSH}..."
if [[ "$DRY_RUN" == false ]]; then
  ssh_media "
    set -e
    cd ${COMPOSE_DIR}
    sed -i.bak-${TIMESTAMP} 's|image: listenarr:.*|image: ${TAG}|' docker-compose.yml
    docker-compose down
    docker-compose up -d
  " || die "Failed to restart container on ${MEDIA_SSH}"
  log "Container restarted"
else
  log "[dry-run] Would sed-edit compose tag → ${TAG} and run docker-compose down && docker-compose up -d"
fi

# ── Health check ─────────────────────────────────────────────────────────────
log "Waiting for container to become healthy (up to ${HEALTH_TIMEOUT}s)..."
if [[ "$DRY_RUN" == false ]]; then
  ELAPSED=0
  until curl -sf "${HEALTH_URL}" > /dev/null 2>&1; do
    if [[ $ELAPSED -ge $HEALTH_TIMEOUT ]]; then
      die "Container did not become healthy within ${HEALTH_TIMEOUT}s. Check logs: ssh ${MEDIA_SSH} 'docker logs listenarr --tail 50'"
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
  LISTENARR_HOST="${MEDIA_IP}" ./scripts/smoke-test.sh || warn "Smoke tests had failures — check output above"
else
  log "[dry-run] Would run: LISTENARR_HOST=${MEDIA_IP} ./scripts/smoke-test.sh"
fi

# ── Done ─────────────────────────────────────────────────────────────────────
log "Deploy complete!"
log "  Image:       ${TAG}"
log "  Live URL:    https://your-host.example"
log "  Note:        For frontend bundle changes, also load the SPA in a browser"
log "               (incognito preferred) and watch the console. HTTP smoke is"
log "               necessary but not sufficient for JS module-init failures."
log "  Rollback:    ssh ${MEDIA_SSH} 'cd ${COMPOSE_DIR} && sed -i \"s|image: .*|image: ${CURRENT_IMAGE:-<previous-tag>}|\" docker-compose.yml && docker-compose up -d'"
