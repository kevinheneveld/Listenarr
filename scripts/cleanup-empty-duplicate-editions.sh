#!/usr/bin/env bash
#
# One-off cleanup: delete empty "duplicate-edition" audiobook records.
#
# Some books exist as several audiobook records of the *same recording* (identical
# title + author + narrator) under different ASINs — one record actually owns the files,
# the others are empty, monitored "wishlist" rows that just clutter the library and drive
# redundant auto-searches for a book already owned. Settings -> Find Duplicates deliberately
# hides these (it drops fileless rows from a group when a sibling has files), so they can't be
# resolved from the UI.
#
# Scope was established by a library-wide scan on 2026-06-08 grouping records by
# lower(title) + authors + narrators and keeping only groups with >=1 record that has files
# AND >=1 that is empty (no AudiobookFiles, no FilePath). It found exactly 4 groups / 5 empties:
#
#   keep 3408 "A Darker Shade of Magic" (Steven Crossley)   <- delete 3410
#   keep 3477 "Elantris"               (Jack Garrett)       <- delete 3395, 3400
#   keep  426 "Lord of Chaos"          (Reading/Kramer)     <- delete 3377
#   keep  170 "The Crooked Staircase"  (Elisabeth Rodgers)  <- delete 784
#
# Re-detect later with the SELECT in dry-run mode (below) — if it returns rows, there are new
# groups this script does not cover; re-scope before extending the EMPTIES list.
#
# Metadata preservation: keeper 170 lacks an ASIN and a Jane Hawk series number, both of which
# its empty (784) carries. Those are back-filled onto 170 before 784 is deleted so the data is
# not lost. (The other keepers already have their ASIN + series number.)
#
# No foreign_keys cascade fires (the DB runs with foreign_keys=off), so each empty's child rows
# are deleted explicitly: external identifiers, series memberships, downloads (all terminal
# Failed/ImportBlocked dup grabs, plus one stale Downloading row that the stall-reaper will drop
# from the client once untracked), history, move jobs. No DownloadProcessingJobs reference them.
#
# Usage:
#   scripts/cleanup-empty-duplicate-editions.sh           # dry run (default)
#   scripts/cleanup-empty-duplicate-editions.sh --apply   # back up the DB, then delete
#
# Reads MEDIA_SSH and COMPOSE_DIR from .deploy-local.env (same contract as deploy-local.sh).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
ENV_FILE="${REPO_ROOT}/.deploy-local.env"

if [[ -f "${ENV_FILE}" ]]; then
  # shellcheck disable=SC1090
  source "${ENV_FILE}"
fi

: "${MEDIA_SSH:?MEDIA_SSH not set — copy .deploy-local.env.example to .deploy-local.env and fill it in}"
: "${COMPOSE_DIR:?COMPOSE_DIR not set — copy .deploy-local.env.example to .deploy-local.env and fill it in}"
DB_PATH="${COMPOSE_DIR}/config/database/listenarr.db"

# The empty duplicate-edition records to delete (see header for keeper mapping).
EMPTIES="3410,3395,3400,3377,784"

APPLY=0
[[ "${1:-}" == "--apply" ]] && APPLY=1

echo "DB:    ${MEDIA_SSH}:${DB_PATH}"
echo "Mode:  $([[ ${APPLY} -eq 1 ]] && echo APPLY || echo 'DRY RUN')"
echo

echo "=== Empty duplicate-edition records to delete (with child-row counts) ==="
ssh "${MEDIA_SSH}" "sqlite3 -header -column '${DB_PATH}' \"
SELECT a.Id, substr(a.Title,1,24) AS Title, substr(a.Narrators,1,20) AS Narrator, a.Asin, a.Monitored AS Mon,
 (SELECT COUNT(*) FROM AudiobookExternalIdentifiers x WHERE x.AudiobookId=a.Id) AS ExtIds,
 (SELECT COUNT(*) FROM AudiobookSeriesMemberships s WHERE s.AudiobookId=a.Id) AS SeriesMem,
 (SELECT COUNT(*) FROM Downloads d WHERE d.AudiobookId=a.Id) AS Dls,
 (SELECT COUNT(*) FROM History hi WHERE hi.AudiobookId=a.Id) AS Hist,
 (SELECT COUNT(*) FROM MoveJobs mj WHERE mj.AudiobookId=a.Id) AS Moves
FROM Audiobooks a WHERE a.Id IN (${EMPTIES}) ORDER BY a.Id;\""

echo
echo "=== Keeper metadata back-fill that will be applied ==="
ssh "${MEDIA_SSH}" "sqlite3 -header -column '${DB_PATH}' \"
SELECT 170 AS KeeperId, Asin AS CurrentAsin, 'B079NDWGTF' AS NewAsin,
 (SELECT SeriesNumber FROM AudiobookSeriesMemberships WHERE AudiobookId=170 AND SeriesName='Jane Hawk') AS CurrentJaneHawkNum,
 '3' AS NewJaneHawkNum
FROM Audiobooks WHERE Id=170;\""

# Safety net: surface any OTHER duplicate-edition groups not covered by EMPTIES, so the script
# is re-scoped rather than silently going stale if new dupes appear later.
echo
echo "=== Re-detect: empty duplicate-edition records NOT in this script's list ==="
ssh "${MEDIA_SSH}" "sqlite3 -header -column '${DB_PATH}' \"
WITH keyed AS (
  SELECT a.Id, lower(trim(a.Title)) AS T, a.Authors AS AU, a.Narrators AS NA,
         (SELECT COUNT(*) FROM AudiobookFiles f WHERE f.AudiobookId=a.Id) AS Files,
         (a.FilePath IS NOT NULL AND a.FilePath<>'') AS HasFP
  FROM Audiobooks a WHERE a.Narrators IS NOT NULL AND a.Narrators<>'' AND a.Narrators<>'[]'
),
grp AS (
  SELECT T, AU, NA, SUM(CASE WHEN Files>0 OR HasFP THEN 1 ELSE 0 END) AS keepers,
         SUM(CASE WHEN Files=0 AND NOT HasFP THEN 1 ELSE 0 END) AS empties
  FROM keyed GROUP BY T, AU, NA HAVING keepers>=1 AND empties>=1
)
SELECT k.Id, substr(k.T,1,30) AS title
FROM keyed k JOIN grp g ON k.T=g.T AND k.AU=g.AU AND k.NA=g.NA
WHERE k.Files=0 AND NOT k.HasFP AND k.Id NOT IN (${EMPTIES}) ORDER BY k.T;\""

if [[ ${APPLY} -eq 0 ]]; then
  echo
  echo "Dry run only. Re-run with --apply to back up the DB and delete these records."
  echo "(If the re-detect section above is non-empty, re-scope before applying.)"
  exit 0
fi

BACKUP="${COMPOSE_DIR}/config/backups/listenarr-predupedition-$(date +%Y%m%d-%H%M%S).db"
echo
echo "Backing up DB -> ${BACKUP}"
ssh "${MEDIA_SSH}" "sqlite3 '${DB_PATH}' \".backup '${BACKUP}'\""

echo "Applying back-fill + deletions in a transaction..."
ssh "${MEDIA_SSH}" "sqlite3 '${DB_PATH}' \"
BEGIN;
-- Preserve keeper 170's metadata from its empty (784) before deleting it.
UPDATE Audiobooks SET Asin='B079NDWGTF' WHERE Id=170 AND (Asin IS NULL OR Asin='');
UPDATE AudiobookSeriesMemberships SET SeriesNumber='3'
 WHERE AudiobookId=170 AND SeriesName='Jane Hawk' AND (SeriesNumber IS NULL OR SeriesNumber='');
-- Delete each empty record's child rows (no FK cascade with foreign_keys=off), then the records.
DELETE FROM AudiobookExternalIdentifiers WHERE AudiobookId IN (${EMPTIES});
DELETE FROM AudiobookSeriesMemberships  WHERE AudiobookId IN (${EMPTIES});
DELETE FROM Downloads                   WHERE AudiobookId IN (${EMPTIES});
DELETE FROM History                     WHERE AudiobookId IN (${EMPTIES});
DELETE FROM MoveJobs                    WHERE AudiobookId IN (${EMPTIES});
DELETE FROM Audiobooks                  WHERE Id          IN (${EMPTIES});
COMMIT;\""

echo
echo "=== Verify: empties gone, keeper 170 enriched ==="
ssh "${MEDIA_SSH}" "sqlite3 -header -column '${DB_PATH}' \"
SELECT (SELECT COUNT(*) FROM Audiobooks WHERE Id IN (${EMPTIES})) AS empties_remaining,
       (SELECT COUNT(*) FROM Downloads WHERE AudiobookId IN (${EMPTIES})) AS orphan_downloads,
       (SELECT Asin FROM Audiobooks WHERE Id=170) AS keeper170_asin,
       (SELECT SeriesNumber FROM AudiobookSeriesMemberships WHERE AudiobookId=170 AND SeriesName='Jane Hawk') AS keeper170_janehawk_num;\""
echo "Done. Backup at ${BACKUP}"
