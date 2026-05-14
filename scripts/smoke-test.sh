#!/usr/bin/env bash
# smoke-test.sh — Quick health check against the live Listenarr instance
#
# Usage:
#   ./scripts/smoke-test.sh                   # Test against your-server.local (default)
#   LISTENARR_HOST=127.0.0.1 ./scripts/smoke-test.sh  # Test against a specific host
#
# Exit codes:
#   0 — all checks passed
#   1 — one or more checks failed

set -uo pipefail

# ── Config ────────────────────────────────────────────────────────────────────
HOST="${LISTENARR_HOST:-your-server.local}"
PORT="${LISTENARR_PORT:-4545}"
BASE="http://${HOST}:${PORT}"

PASS=0
FAIL=0

# ── Helpers ───────────────────────────────────────────────────────────────────
check() {
  local label="$1"
  local url="$2"
  local expected_status="${3:-200}"

  HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 "${url}" 2>/dev/null || echo "000")

  if [[ "$HTTP_STATUS" == "$expected_status" ]]; then
    echo "  [PASS] ${label} → ${HTTP_STATUS}"
    PASS=$((PASS + 1))
  else
    echo "  [FAIL] ${label} → got ${HTTP_STATUS}, expected ${expected_status}  (${url})"
    FAIL=$((FAIL + 1))
  fi
}

echo "Smoke testing Listenarr at ${BASE}"
echo "────────────────────────────────────"

# ── Checks ────────────────────────────────────────────────────────────────────

# Root / app shell loads
check "App root"            "${BASE}/"               200

# Key UI routes (SPA — all should return 200 if routing is set up)
check "Library page"        "${BASE}/library"        200
check "Wanted page"         "${BASE}/wanted"         200
check "Library import"      "${BASE}/library-import" 200
check "Settings"            "${BASE}/settings"       200

# API health endpoint (if one exists — returns 404 if not implemented yet)
check "API health"          "${BASE}/api/v1/health"  200

# ── Results ───────────────────────────────────────────────────────────────────
echo "────────────────────────────────────"
echo "Results: ${PASS} passed, ${FAIL} failed"

if [[ $FAIL -gt 0 ]]; then
  echo ""
  echo "To check container logs:"
  echo "  ssh media 'docker logs listenarr --tail 50'"
  exit 1
fi

exit 0
