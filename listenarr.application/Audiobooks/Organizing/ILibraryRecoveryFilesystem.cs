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

namespace Listenarr.Application.Audiobooks.Organizing
{
    /// <summary>
    /// Filesystem probes used by the library recovery/maintenance flows.
    /// Mirrors <see cref="IOrganizeFilesystem"/>'s role: the workflows stay
    /// IO-free; all disk access lives behind this contract in infrastructure.
    /// </summary>
    public interface ILibraryRecoveryFilesystem
    {
        /// <summary>True when the directory exists and contains at least one entry.</summary>
        bool DirectoryExistsWithContent(string path, out bool exists);

        /// <summary>
        /// Count audio files directly inside <paramref name="basePath"/>; when none are found,
        /// checks each immediate subdirectory and returns the first non-zero count (the
        /// "/Author/Title/Narrator" shape). Returns 0 when the directory is missing/unreadable.
        /// </summary>
        int CountAudioFiles(string basePath, out bool directoryExists);

        /// <summary>
        /// Walk each root folder once and index every directory that contains audio files
        /// (directly or one level down), keyed by (top-level author folder name, folder name).
        /// Used by root-base recovery to find a book's real folder without per-book disk walks.
        /// </summary>
        IRecoveryFolderIndex BuildFolderIndex(IReadOnlyCollection<string> rootFolderPaths);

        /// <summary>
        /// Find orphan staging directories left behind by interrupted moves under
        /// <paramref name="root"/> (the move processor's <c>name.tmp-{guid}</c> staging
        /// pattern, plus the legacy <c>.lna-move-</c> prefix).
        /// </summary>
        IReadOnlyList<string> EnumerateOrphanMoveTempDirs(string root);

        /// <summary>Total size in bytes of all files under <paramref name="path"/> (0 on error).</summary>
        long DirectorySizeBytes(string path);

        /// <summary>Recursively delete <paramref name="path"/>. Throws on failure.</summary>
        void DeleteDirectoryRecursive(string path);
    }

    /// <summary>Lookup half of the recovery folder index (see <see cref="ILibraryRecoveryFilesystem.BuildFolderIndex"/>).</summary>
    public interface IRecoveryFolderIndex
    {
        int IndexedFolders { get; }

        /// <summary>All indexed paths whose (author folder, normalized title) match.</summary>
        IReadOnlyList<string> Lookup(string author, string title);
    }
}
