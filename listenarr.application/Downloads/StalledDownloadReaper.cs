/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Domain.Models;

namespace Listenarr.Application.Downloads
{
    /// <summary>
    /// Pure, testable logic for the stalled-download reaper.
    ///
    /// Background: once metaDL/0-seeder torrents are no longer falsely marked Complete,
    /// nothing reaps them. They sit in Queued/Downloading forever and Listenarr re-enriches
    /// and content-path-scans every one of them on every poll cycle (the live CPU driver).
    /// This reaper detects downloads that have made no download progress for a configurable
    /// timeout and transitions them to Failed (which removes them from every recurring active
    /// loop: GetActiveAsync, the queue display, and queue matching all exclude Failed).
    ///
    /// Detection is a progress-stall timer rather than a "progress == 0" / StartedAt heuristic.
    /// Live-DB analysis showed the dead set is NOT progress==0 (stalled torrents sit at fractional
    /// progress) and a large share are "ghost" rows with no matching client torrent at all. A stall
    /// timer keyed on whether progress advances handles both populations uniformly and—critically—
    /// never reaps a genuinely slow-but-advancing download, because any real byte of progress
    /// resets the timer.
    /// </summary>
    public static class StalledDownloadReaper
    {
        /// <summary>Result of evaluating a single download against the stall timer.</summary>
        /// <param name="ShouldReap">True when the download has been stalled past the timeout.</param>
        /// <param name="SnapshotProgress">The progress value to remember for the next poll.</param>
        /// <param name="SnapshotAt">The timestamp the current stall window started.</param>
        public readonly record struct StallEvaluation(bool ShouldReap, decimal SnapshotProgress, DateTime SnapshotAt);

        /// <summary>
        /// Only Queued/Downloading downloads are reap-eligible. Paused is a deliberate user/client
        /// state, and Completed/Processing/ImportPending/Moved/ImportBlocked/Failed are past the
        /// downloading phase or already terminal.
        /// </summary>
        public static bool IsReapEligibleStatus(DownloadStatus status) =>
            status is DownloadStatus.Queued or DownloadStatus.Downloading;

        /// <summary>
        /// Evaluate one download against the stall timer.
        /// </summary>
        /// <param name="status">Current download status.</param>
        /// <param name="currentProgress">Current progress (0-100).</param>
        /// <param name="previousSnapshot">
        /// The progress/timestamp remembered from a prior poll, or null on first observation.
        /// </param>
        /// <param name="now">Current UTC time.</param>
        /// <param name="timeoutMinutes">Stall timeout in minutes; values &lt;= 0 disable reaping.</param>
        /// <returns>
        /// Whether to reap, plus the snapshot to carry forward. When progress advances (or on the
        /// first observation) the snapshot is reset to (currentProgress, now) and ShouldReap is
        /// false. When progress is unchanged, the original snapshot timestamp is preserved so the
        /// stall window keeps accumulating across polls.
        /// </returns>
        public static StallEvaluation Evaluate(
            DownloadStatus status,
            decimal currentProgress,
            (decimal Progress, DateTime At)? previousSnapshot,
            DateTime now,
            int timeoutMinutes)
        {
            // Ineligible status, already-complete progress, or a disabled timeout: never reap and
            // keep a fresh snapshot so the timer starts clean if the download later becomes eligible.
            if (!IsReapEligibleStatus(status) || currentProgress >= 100m || timeoutMinutes <= 0)
            {
                return new StallEvaluation(false, currentProgress, now);
            }

            // First observation, or progress advanced since the last poll: (re)seed the snapshot.
            if (previousSnapshot is null || currentProgress > previousSnapshot.Value.Progress)
            {
                return new StallEvaluation(false, currentProgress, now);
            }

            // No progress since the snapshot was taken. Reap once we have been stalled past the timeout.
            var snapshot = previousSnapshot.Value;
            var stalledFor = now - snapshot.At;
            var shouldReap = stalledFor >= TimeSpan.FromMinutes(timeoutMinutes);
            return new StallEvaluation(shouldReap, snapshot.Progress, snapshot.At);
        }
    }

    /// <summary>
    /// Operator-facing knobs for the stalled-download reaper, read from environment variables so the
    /// destructive behaviour can be armed deliberately without a schema migration. Defaults are safe:
    /// the reaper is OFF, and when enabled it starts in DRY-RUN (reports only, removes nothing).
    /// </summary>
    public sealed record StalledReaperOptions(bool Enabled, bool DryRun, int TimeoutMinutes, bool DeleteFiles)
    {
        public const string EnabledEnv = "LISTENARR_STALL_REAPER_ENABLED";
        public const string DryRunEnv = "LISTENARR_STALL_REAPER_DRY_RUN";
        public const string TimeoutMinutesEnv = "LISTENARR_STALL_REAPER_TIMEOUT_MINUTES";
        public const string DeleteFilesEnv = "LISTENARR_STALL_REAPER_DELETE_FILES";

        public const int DefaultTimeoutMinutes = 60;

        public static StalledReaperOptions FromEnvironment() => From(Environment.GetEnvironmentVariable);

        /// <summary>Resolve options from an arbitrary variable lookup (injected for testability).</summary>
        public static StalledReaperOptions From(Func<string, string?> getVar)
        {
            // Off unless explicitly turned on.
            var enabled = ParseBool(getVar(EnabledEnv), defaultValue: false);
            // When enabled, stay in dry-run until explicitly disarmed.
            var dryRun = ParseBool(getVar(DryRunEnv), defaultValue: true);
            // Conservative default: just drop the torrent/nzb from the client and leave any partial
            // files on disk for a later sweep. Opt in to deleting partials to reclaim space.
            var deleteFiles = ParseBool(getVar(DeleteFilesEnv), defaultValue: false);

            var timeout = DefaultTimeoutMinutes;
            var rawTimeout = getVar(TimeoutMinutesEnv);
            if (!string.IsNullOrWhiteSpace(rawTimeout) &&
                int.TryParse(rawTimeout.Trim(), out var parsed) &&
                parsed > 0)
            {
                timeout = parsed;
            }

            return new StalledReaperOptions(enabled, dryRun, timeout, deleteFiles);
        }

        private static bool ParseBool(string? raw, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            var v = raw.Trim();
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1")
            {
                return true;
            }
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) || v == "0")
            {
                return false;
            }
            return defaultValue;
        }
    }
}
