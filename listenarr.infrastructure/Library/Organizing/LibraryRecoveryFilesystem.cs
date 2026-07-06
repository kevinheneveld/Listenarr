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

using Listenarr.Application.Audiobooks.Organizing;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Organizing
{
    /// <summary>
    /// Disk probes for the recovery/maintenance flows. All heuristics ported
    /// from kevin/live's recovery endpoints: the one-pass folder index replaces
    /// per-book disk walks (which timed out at the proxy on libraries with
    /// hundreds of broken rows), staging-dir names from interrupted moves are
    /// skipped while indexing, and when both a parent and its child contain
    /// audio the leaf wins (that's where the files live).
    /// </summary>
    public sealed class LibraryRecoveryFilesystem(ILogger<LibraryRecoveryFilesystem> logger) : ILibraryRecoveryFilesystem
    {
        // Staging-dir markers from interrupted moves. Canary's MoveJobProcessor
        // stages as "name.tmp-{guid:N}"; ".lna-move-" is the legacy prefix.
        private const string TempPrefix = ".lna-move-";
        private const string LegacyTempSuffixPattern = ".tmp-";

        public bool DirectoryExistsWithContent(string path, out bool exists)
        {
            exists = false;
            try
            {
                exists = Directory.Exists(path);
                return exists && Directory.EnumerateFileSystemEntries(path).Any();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Recovery: failed to stat {Path}", LogRedaction.SanitizeFilePath(path));
                return false;
            }
        }

        public int CountAudioFiles(string basePath, out bool directoryExists)
        {
            directoryExists = false;
            try
            {
                directoryExists = Directory.Exists(basePath);
                if (!directoryExists) return 0;

                var count = Directory.EnumerateFiles(basePath, "*.*", SearchOption.TopDirectoryOnly)
                    .Count(f => IsAudioExtension(Path.GetExtension(f)));
                if (count > 0) return count;

                foreach (var sub in Directory.EnumerateDirectories(basePath))
                {
                    count += Directory.EnumerateFiles(sub, "*.*", SearchOption.TopDirectoryOnly)
                        .Count(f => IsAudioExtension(Path.GetExtension(f)));
                    if (count > 0) break;
                }
                return count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Recovery: failed to count audio under {Path}", LogRedaction.SanitizeFilePath(basePath));
                return 0;
            }
        }

        public IRecoveryFolderIndex BuildFolderIndex(IReadOnlyCollection<string> rootFolderPaths)
        {
            var index = new RecoveryFolderIndex();

            foreach (var rootPath in rootFolderPaths)
            {
                if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) continue;

                IEnumerable<string> authorDirs;
                try
                {
                    authorDirs = Directory.EnumerateDirectories(rootPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    continue;
                }

                foreach (var authorDir in authorDirs)
                {
                    var authorName = Path.GetFileName(authorDir);
                    if (string.IsNullOrEmpty(authorName)) continue;

                    // Walk up to 3 levels under the author. When both a parent and its
                    // child contain audio (e.g. /Author/Title with audio AND
                    // /Author/Title/Narrator with audio), prefer the leaf.
                    IndexUnderAuthor(authorDir, authorName, depth: 0, maxDepth: 3, index);
                }
            }

            return index;
        }

        private static void IndexUnderAuthor(string dir, string authorName, int depth, int maxDepth, RecoveryFolderIndex index)
        {
            if (depth > maxDepth) return;
            var name = Path.GetFileName(dir);
            // Skip staging dirs from interrupted moves.
            if (depth > 0 && name.StartsWith(TempPrefix, StringComparison.Ordinal)) return;
            if (depth > 0 && name.Contains(LegacyTempSuffixPattern, StringComparison.Ordinal)) return;

            bool hasAudioHere;
            try
            {
                hasAudioHere = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                    .Any(f => IsAudioExtension(Path.GetExtension(f)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                children = Array.Empty<string>();
            }

            bool childHasAudio = false;
            foreach (var child in children)
            {
                var childName = Path.GetFileName(child);
                if (childName.StartsWith(TempPrefix, StringComparison.Ordinal)) continue;
                if (childName.Contains(LegacyTempSuffixPattern, StringComparison.Ordinal)) continue;
                try
                {
                    if (Directory.EnumerateFiles(child, "*.*", SearchOption.TopDirectoryOnly)
                        .Any(f => IsAudioExtension(Path.GetExtension(f))))
                    {
                        childHasAudio = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                }
            }

            if (depth > 0 && (hasAudioHere || childHasAudio))
            {
                index.Add(authorName, name, dir);
            }

            // A dir that directly contains audio holds the book's own parts/discs —
            // don't descend. Otherwise deeper folders might be separate books.
            if (depth < maxDepth && !hasAudioHere)
            {
                foreach (var child in children)
                {
                    var childName = Path.GetFileName(child);
                    if (childName.StartsWith(TempPrefix, StringComparison.Ordinal)) continue;
                    if (childName.Contains(LegacyTempSuffixPattern, StringComparison.Ordinal)) continue;
                    IndexUnderAuthor(child, authorName, depth + 1, maxDepth, index);
                }
            }
        }

        public IReadOnlyList<string> EnumerateOrphanMoveTempDirs(string root)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return result;

            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name)) continue;

                if (name.StartsWith(TempPrefix, StringComparison.Ordinal))
                {
                    result.Add(dir);
                    continue;
                }

                // Move-staging pattern: ends with .tmp-{32-hex-guid}
                var idx = name.LastIndexOf(LegacyTempSuffixPattern, StringComparison.Ordinal);
                if (idx > 0 && idx + LegacyTempSuffixPattern.Length + 32 == name.Length
                    && LooksLikeHexGuid(name.Substring(idx + LegacyTempSuffixPattern.Length)))
                {
                    result.Add(dir);
                }
            }
            return result;
        }

        public long DirectorySizeBytes(string path)
        {
            try
            {
                return new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Recovery: could not size {Path}", LogRedaction.SanitizeFilePath(path));
                return 0;
            }
        }

        public void DeleteDirectoryRecursive(string path) => Directory.Delete(path, true);

        private static bool IsAudioExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            return ext.Equals(".m4b", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".opus", StringComparison.OrdinalIgnoreCase);
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

        private sealed class RecoveryFolderIndex : IRecoveryFolderIndex
        {
            // Composite key: "{authorKey}\0{titleKey}" so a plain Dictionary works
            // without a custom comparer.
            private readonly Dictionary<string, List<string>> _byAuthorAndTitle = new(StringComparer.Ordinal);

            public int IndexedFolders { get; private set; }

            public void Add(string authorFolderName, string folderName, string fullPath)
            {
                var titleKey = NormalizeForFolderMatch(folderName);
                if (string.IsNullOrEmpty(titleKey)) return;
                var key = authorFolderName.ToUpperInvariant() + "\0" + titleKey;
                if (!_byAuthorAndTitle.TryGetValue(key, out var list))
                {
                    list = new List<string>(1);
                    _byAuthorAndTitle[key] = list;
                }
                list.Add(fullPath);
                IndexedFolders++;
            }

            public IReadOnlyList<string> Lookup(string author, string title)
            {
                var titleKey = NormalizeForFolderMatch(title);
                if (string.IsNullOrEmpty(titleKey)) return Array.Empty<string>();
                var key = author.ToUpperInvariant() + "\0" + titleKey;
                return _byAuthorAndTitle.TryGetValue(key, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();
            }
        }

        /// <summary>Letters+digits, lowercased — folder-name matching tolerant of punctuation/spacing.</summary>
        internal static string NormalizeForFolderMatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }
    }
}
