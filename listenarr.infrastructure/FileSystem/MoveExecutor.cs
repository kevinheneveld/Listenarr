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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem
{
    /// <summary>
    /// Pure file-system move logic, extracted from <see cref="MoveBackgroundService"/>
    /// so it can be exercised directly by tests against real temp directories.
    ///
    /// Handles the tricky case where the destination is a subdirectory of the
    /// source (e.g. a folder pattern that adds a <c>{Narrator}</c> level under
    /// the current title). The original implementation placed the temp copy
    /// directory under <c>targetParent</c>, which when the target lived inside
    /// the source meant the temp dir was a descendant of source — and the
    /// recursive enumeration kept discovering its own output, copying tmp into
    /// tmp into tmp until it ran out of path length or disk. See history entries
    /// for jobs whose error messages were essentially the same nested path
    /// repeated dozens of times.
    /// </summary>
    public static class MoveExecutor
    {
        public sealed class MoveOutcome
        {
            public required bool Success { get; init; }
            public string? ErrorMessage { get; init; }
            public int FilesCopied { get; init; }
            public bool TempPathInsideSource { get; init; }
            public string? TempPathUsed { get; init; }
        }

        /// <summary>
        /// Tmp prefix for the staging directory created during a move. Leading
        /// dot keeps it out of normal directory listings; the <c>lna-move-</c>
        /// segment is greppable from the host so an operator can find orphaned
        /// staging dirs from interrupted moves.
        /// </summary>
        public const string TempPrefix = ".lna-move-";

        /// <summary>
        /// Legacy tmp prefix used by the pre-recursion-fix code path. Cleanup
        /// scans look for both so old orphans get swept too.
        /// </summary>
        public const string LegacyTempSuffixPattern = ".tmp-";

        /// <summary>
        /// Cap surfaced error messages so a runaway recursion (or any other
        /// pathological state) can't turn a single failure into a multi-MB
        /// row in History.
        /// </summary>
        public const int MaxErrorMessageLength = 500;

        /// <summary>
        /// Copy everything under <paramref name="source"/> into
        /// <paramref name="target"/> via a sibling temp directory, then delete
        /// the source. Caller is responsible for DB / metadata updates after
        /// success.
        /// </summary>
        public static async Task<MoveOutcome> ExecuteMoveAsync(
            string source,
            string target,
            Guid jobId,
            ILogger? logger,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return new MoveOutcome { Success = false, ErrorMessage = "Source path is empty" };
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                return new MoveOutcome { Success = false, ErrorMessage = "Target path is empty" };
            }

            if (!Directory.Exists(source))
            {
                return new MoveOutcome { Success = false, ErrorMessage = "Source path does not exist" };
            }

            // Normalize to absolute, separator-trimmed forms so prefix
            // comparisons are reliable across "with-trailing-slash" inputs.
            var sourceFull = TrimTrailingSeparator(Path.GetFullPath(source));
            var targetFull = TrimTrailingSeparator(Path.GetFullPath(target));

            if (string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase))
            {
                // No-op move. Caller treats this as success.
                return new MoveOutcome { Success = true, FilesCopied = 0 };
            }

            var sep = Path.DirectorySeparatorChar;
            var sourceWithSep = sourceFull + sep;
            var targetWithSep = targetFull + sep;

            var sourceContainsTarget = targetWithSep.StartsWith(sourceWithSep, StringComparison.OrdinalIgnoreCase);
            var targetContainsSource = sourceWithSep.StartsWith(targetWithSep, StringComparison.OrdinalIgnoreCase);

            if (targetContainsSource)
            {
                // E.g. source = /a/b/c, target = /a/b — flattening into an
                // ancestor. The move semantics get weird (we'd have to merge
                // into a directory that itself contains the source), so refuse.
                return new MoveOutcome
                {
                    Success = false,
                    ErrorMessage = "Target is an ancestor of source; refusing to flatten",
                };
            }

            var targetParent = Path.GetDirectoryName(targetFull);
            if (string.IsNullOrEmpty(targetParent))
            {
                return new MoveOutcome { Success = false, ErrorMessage = "Target has no parent directory" };
            }

            // Decide where the temp staging directory lives.
            //
            // Default: alongside the eventual target (same volume, so the
            // final Directory.Move is atomic).
            //
            // Special case — source contains target: we MUST NOT stage inside
            // source, or the recursive copy enumerates its own output. Use
            // source's parent instead.
            string tempParent;
            if (sourceContainsTarget)
            {
                tempParent = Path.GetDirectoryName(sourceFull) ?? string.Empty;
                if (string.IsNullOrEmpty(tempParent))
                {
                    return new MoveOutcome
                    {
                        Success = false,
                        ErrorMessage = "Cannot stage move outside source: source has no parent",
                    };
                }
            }
            else
            {
                tempParent = targetParent;
            }

            if (!Directory.Exists(tempParent)) Directory.CreateDirectory(tempParent);

            var tempName = Path.Combine(tempParent, $"{TempPrefix}{jobId:N}");
            var tempInsideSource = (tempName + sep).StartsWith(sourceWithSep, StringComparison.OrdinalIgnoreCase);
            if (tempInsideSource)
            {
                // Belt and suspenders — if for any reason the chosen tempParent
                // lands inside source, bail rather than recurse.
                return new MoveOutcome
                {
                    Success = false,
                    ErrorMessage = "Refusing to stage move inside source",
                    TempPathInsideSource = true,
                    TempPathUsed = tempName,
                };
            }

            // Target's existence: only a non-empty target is a hard error.
            // An empty target (e.g. left behind by a previous failed attempt)
            // can be reclaimed by removing it before Directory.Move.
            if (Directory.Exists(targetFull))
            {
                var hasContent = Directory.EnumerateFileSystemEntries(targetFull).Any();
                if (hasContent)
                {
                    return new MoveOutcome
                    {
                        Success = false,
                        ErrorMessage = "Target directory already exists and contains files",
                    };
                }
            }

            var filesCopied = 0;
            try
            {
                Directory.CreateDirectory(tempName);

                // Capture the snapshot of entries up front. Enumerating eagerly
                // means even if our staging dir somehow ends up adjacent to
                // source's tree we won't pick up newly-created files mid-copy.
                var entries = Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).ToList();

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    // Defense in depth: never copy our own staging directory.
                    var entryFull = TrimTrailingSeparator(Path.GetFullPath(entry));
                    if (string.Equals(entryFull, tempName, StringComparison.OrdinalIgnoreCase)
                        || entryFull.StartsWith(tempName + sep, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var rel = Path.GetRelativePath(source, entry);
                    var destPath = Path.Combine(tempName, rel);

                    if (Directory.Exists(entry))
                    {
                        if (!Directory.Exists(destPath)) Directory.CreateDirectory(destPath);
                        continue;
                    }

                    var ddir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(ddir) && !Directory.Exists(ddir)) Directory.CreateDirectory(ddir);

                    var copied = await CopyFileWithRetryAsync(entry, destPath, logger, ct);
                    if (!copied)
                    {
                        throw new IOException($"Failed to copy file after retries: {entry}");
                    }
                    filesCopied++;
                }

                // Finalize: delete source, then atomically rename temp -> target.
                //
                // Order matters in the source-contains-target case: deleting
                // source removes the would-be target's parent path, so we
                // recreate it before the Directory.Move.
                Directory.Delete(source, true);

                if (Directory.Exists(targetFull))
                {
                    // Target existed but was empty when we checked above; remove
                    // the empty shell so Directory.Move doesn't refuse.
                    Directory.Delete(targetFull, false);
                }

                var finalParent = Path.GetDirectoryName(targetFull);
                if (!string.IsNullOrEmpty(finalParent) && !Directory.Exists(finalParent))
                {
                    Directory.CreateDirectory(finalParent);
                }

                Directory.Move(tempName, targetFull);

                return new MoveOutcome
                {
                    Success = true,
                    FilesCopied = filesCopied,
                    TempPathUsed = tempName,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Best-effort cleanup of the staging dir so we don't leak.
                try { if (Directory.Exists(tempName)) Directory.Delete(tempName, true); }
                catch (Exception cleanupEx) when (cleanupEx is not OperationCanceledException && cleanupEx is not OutOfMemoryException && cleanupEx is not StackOverflowException)
                {
                    logger?.LogDebug(cleanupEx, "Non-fatal: failed to remove temp staging dir {Temp}", tempName);
                }

                return new MoveOutcome
                {
                    Success = false,
                    ErrorMessage = TruncateErrorMessage(ex.Message),
                    FilesCopied = filesCopied,
                    TempPathUsed = tempName,
                };
            }
        }

        public static string TruncateErrorMessage(string? message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            if (message.Length <= MaxErrorMessageLength) return message;
            return message.Substring(0, MaxErrorMessageLength) + $"… [truncated; original {message.Length} chars]";
        }

        /// <summary>
        /// Find orphan staging directories left behind by interrupted moves.
        /// Looks for both the new <see cref="TempPrefix"/> pattern and the
        /// legacy <c>name.tmp-{guid}</c> pattern.
        /// </summary>
        public static IEnumerable<string> EnumerateOrphanTempDirs(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;

            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;

                // New pattern: starts with .lna-move-
                if (name.StartsWith(TempPrefix, StringComparison.Ordinal))
                {
                    yield return dir;
                    continue;
                }

                // Legacy pattern: ends with .tmp-{32-hex-guid}
                var idx = name.LastIndexOf(LegacyTempSuffixPattern, StringComparison.Ordinal);
                if (idx > 0 && idx + LegacyTempSuffixPattern.Length + 32 == name.Length)
                {
                    var suffix = name.Substring(idx + LegacyTempSuffixPattern.Length);
                    if (LooksLikeHexGuid(suffix))
                    {
                        yield return dir;
                    }
                }
            }
        }

        private static bool LooksLikeHexGuid(string s)
        {
            if (s.Length != 32) return false;
            foreach (var c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        private static string TrimTrailingSeparator(string p)
        {
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static async Task<bool> CopyFileWithRetryAsync(string source, string dest, ILogger? logger, CancellationToken ct)
        {
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    File.Copy(source, dest, false);

                    try
                    {
                        var attrs = File.GetAttributes(source);
                        File.SetAttributes(dest, attrs);
                        File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
                        File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(source));
                    }
                    catch (Exception attrEx) when (attrEx is not OperationCanceledException && attrEx is not OutOfMemoryException && attrEx is not StackOverflowException)
                    {
                        logger?.LogDebug(attrEx, "Non-fatal: failed to preserve attributes for {File}", source);
                    }

                    return true;
                }
                catch (IOException ioex)
                {
                    logger?.LogWarning(ioex, "IO error copying file {File} attempt {Attempt}", source, attempt);
                    var delay = TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1)));
                    await Task.Delay(delay, ct);
                }
            }
            return false;
        }
    }
}
