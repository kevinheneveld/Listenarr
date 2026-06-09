#!/usr/bin/env bash
#
# One-off cleanup: remove legacy *duplicate* AudiobookFile rows.
#
# An older scan stored some files under a bare/relative path (e.g. "Elantris.mp3" with no
# directory). A later scan re-found the same file at its absolute path and — because the
# dedup did a raw exact-string match — failed to recognise the relative row and inserted a
# second, absolute-path row. The result is two rows for one physical file: the absolute one
# streams fine, the bare one resolves to the wrong place and 404s on playback.
#
# This script deletes ONLY the relative row when an absolute-path row for the *same file*
# (same AudiobookId + same Size) already exists — i.e. a confirmed duplicate. Solo legacy
# relative rows (no absolute sibling) are deliberately left alone: those reflect a separate
# stale-BasePath problem and need individual attention, not a blind rewrite.
#
# The code-level fix (EfAudiobookFileRepository.ExistsAtPathAsync now normalises paths before
# comparing) stops new duplicates forming; this script clears the ones already in the DB.
#
# Usage:
#   scripts/cleanup-duplicate-relative-file-rows.sh           # dry run (default) — shows what would go
#   scripts/cleanup-duplicate-relative-file-rows.sh --apply   # back up the DB, then delete
#
# Reads MEDIA_SSH and COMPOSE_DIR from .deploy-local.env (same as deploy-local.sh).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
ENV_FILE="${REPO_ROOT}/.deploy-local.env"

if [[ -f "${ENV_FILE}" ]]; then
  # shellcheck disable=SC1090
  source "${ENV_FILE}"
fi

# Required (sourced from .deploy-local.env — same contract as deploy-local.sh). No hardcoded
# defaults: keeps host identifiers out of the committed script.
: "${MEDIA_SSH:?MEDIA_SSH not set — copy .deploy-local.env.example to .deploy-local.env and fill it in}"
: "${COMPOSE_DIR:?COMPOSE_DIR not set — copy .deploy-local.env.example to .deploy-local.env and fill it in}"
DB_PATH="${COMPOSE_DIR}/config/database/listenarr.db"

APPLY=0
[[ "${1:-}" == "--apply" ]] && APPLY=1

# Selector predicate, shared by the SELECT / COUNT / DELETE statements: a relative-path row (not
# starting with "/") that has an absolute-path sibling of the same size under the same audiobook.
# NULL sizes never match (NULL = NULL is false in SQL), so we only ever delete on a positive size
# match. The subquery is correlated to the unaliased outer table name so it is valid in a SQLite
# DELETE (which permits neither a table alias nor ORDER BY).
read -r -d '' WHERE_SQL <<'SQL' || true
WHERE AudiobookFiles.Path NOT LIKE '/%'
  AND EXISTS (
    SELECT 1 FROM AudiobookFiles s
    WHERE s.AudiobookId = AudiobookFiles.AudiobookId
      AND s.Path LIKE '/%'
      AND s.Size = AudiobookFiles.Size
      AND s.Id <> AudiobookFiles.Id
  )
SQL

SELECT_SQL="SELECT Id AS DupRowId, AudiobookId AS Book, Path AS RelativePath, Size FROM AudiobookFiles ${WHERE_SQL} ORDER BY AudiobookId;"
COUNT_SQL="SELECT COUNT(*) FROM AudiobookFiles ${WHERE_SQL};"
DELETE_SQL="DELETE FROM AudiobookFiles ${WHERE_SQL};"

echo "DB:    ${MEDIA_SSH}:${DB_PATH}"
echo "Mode:  $([[ ${APPLY} -eq 1 ]] && echo APPLY || echo 'DRY RUN')"
echo

echo "=== Duplicate relative rows that would be deleted ==="
ssh "${MEDIA_SSH}" "sqlite3 -header -column '${DB_PATH}' \"${SELECT_SQL}\""
COUNT=$(ssh "${MEDIA_SSH}" "sqlite3 '${DB_PATH}' \"${COUNT_SQL}\"")
echo
echo "Total: ${COUNT} row(s)"

if [[ ${APPLY} -eq 0 ]]; then
  echo
  echo "Dry run only. Re-run with --apply to back up the DB and delete these rows."
  exit 0
fi

if [[ "${COUNT}" -eq 0 ]]; then
  echo "Nothing to delete."
  exit 0
fi

BACKUP="${COMPOSE_DIR}/config/backups/listenarr-predupclean-$(date +%Y%m%d-%H%M%S).db"
echo
echo "Backing up DB -> ${BACKUP}"
ssh "${MEDIA_SSH}" "sqlite3 '${DB_PATH}' \".backup '${BACKUP}'\""

echo "Deleting ${COUNT} duplicate row(s)..."
ssh "${MEDIA_SSH}" "sqlite3 '${DB_PATH}' \"BEGIN; ${DELETE_SQL} COMMIT;\""

REMAIN=$(ssh "${MEDIA_SSH}" "sqlite3 '${DB_PATH}' \"${COUNT_SQL}\"")
echo "Done. Remaining matching rows: ${REMAIN} (expected 0). Backup at ${BACKUP}"
