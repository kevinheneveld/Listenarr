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
using Listenarr.Domain.Search;

namespace Listenarr.Application.Search
{
    /// <summary>
    /// Decides whether a search result is one of a book's blocked releases:
    /// by info-hash when both sides carry one (a retitled re-list of the same
    /// torrent cannot dodge the blocklist), else via punctuation-tolerant
    /// normalized title equality (indexers re-list the same release with
    /// minor punctuation drift).
    /// </summary>
    public static class BlockedReleaseMatcher
    {
        public static bool IsBlocked(string? resultTitle, IReadOnlyList<BlockedRelease> blocked)
            => IsBlocked(resultTitle, resultMagnetOrHash: null, blocked);

        public static bool IsBlocked(string? resultTitle, string? resultMagnetOrHash, IReadOnlyList<BlockedRelease> blocked)
        {
            if (blocked.Count == 0) return false;

            // Hash first: exact identity, immune to retitling.
            var resultHash = TryExtractInfoHash(resultMagnetOrHash);
            if (resultHash != null
                && blocked.Any(entry => TryExtractInfoHash(entry.TorrentHash) == resultHash))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(resultTitle)) return false;

            var normalizedResult = TitleMatcher.Normalize(resultTitle);
            if (normalizedResult.Length == 0) return false;

            return blocked.Any(entry =>
                !string.IsNullOrWhiteSpace(entry.ReleaseTitle)
                && TitleMatcher.Normalize(entry.ReleaseTitle) == normalizedResult);
        }

        /// <summary>
        /// Normalizes a bare BitTorrent info-hash or a magnet URI to uppercase
        /// 40-char hex, so hex and base32 encodings of the same torrent
        /// compare equal. Returns null when no usable hash is present.
        /// </summary>
        public static string? TryExtractInfoHash(string? magnetOrHash)
        {
            if (string.IsNullOrWhiteSpace(magnetOrHash)) return null;

            var candidate = magnetOrHash.Trim();
            var btih = candidate.IndexOf("urn:btih:", StringComparison.OrdinalIgnoreCase);
            if (btih >= 0)
            {
                candidate = candidate[(btih + "urn:btih:".Length)..];
                var end = candidate.IndexOfAny(new[] { '&', '#' });
                if (end >= 0) candidate = candidate[..end];
            }
            candidate = candidate.Trim();

            if (candidate.Length == 40 && candidate.All(Uri.IsHexDigit))
            {
                return candidate.ToUpperInvariant();
            }

            if (candidate.Length == 32)
            {
                var decoded = TryDecodeBase32(candidate);
                if (decoded is { Length: 20 })
                {
                    return Convert.ToHexString(decoded);
                }
            }

            return null;
        }

        private static byte[]? TryDecodeBase32(string input)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bits = 0;
            var bitCount = 0;
            var output = new List<byte>(20);
            foreach (var ch in input.ToUpperInvariant())
            {
                var value = alphabet.IndexOf(ch);
                if (value < 0) return null;
                bits = (bits << 5) | value;
                bitCount += 5;
                if (bitCount >= 8)
                {
                    bitCount -= 8;
                    output.Add((byte)((bits >> bitCount) & 0xFF));
                }
            }
            return output.ToArray();
        }
    }
}
