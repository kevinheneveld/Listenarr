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

namespace Listenarr.Application.Search
{
    /// <summary>
    /// Decides whether a search result is one of a book's blocked releases,
    /// via punctuation-tolerant normalized title equality (indexers re-list
    /// the same release with minor punctuation drift; search results carry no
    /// info-hash to compare, though blocked entries store one for the future).
    /// </summary>
    public static class BlockedReleaseMatcher
    {
        public static bool IsBlocked(string? resultTitle, IReadOnlyList<BlockedRelease> blocked)
        {
            if (string.IsNullOrWhiteSpace(resultTitle) || blocked.Count == 0) return false;

            var normalizedResult = TitleMatcher.Normalize(resultTitle);
            if (normalizedResult.Length == 0) return false;

            return blocked.Any(entry =>
                !string.IsNullOrWhiteSpace(entry.ReleaseTitle)
                && TitleMatcher.Normalize(entry.ReleaseTitle) == normalizedResult);
        }
    }
}
