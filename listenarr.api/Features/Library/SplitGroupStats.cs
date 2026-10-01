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

using Listenarr.Application.Audiobooks;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// The size-and-quality line shown for a split group or a destination's
    /// existing copy: how many files, how long, how big, at what bitrate.
    /// </summary>
    internal static class SplitGroupStats
    {
        public static object Describe(IReadOnlyCollection<AudiobookFile> files)
        {
            var summary = CopyQualityComparer.Summarize(files);
            return new
            {
                fileCount = summary.FileCount,
                totalBytes = summary.TotalBytes,
                totalDurationSeconds = summary.TotalDurationSeconds,
                bitrateKbps = summary.BitrateKbps,
                codec = summary.Codec
            };
        }
    }
}
