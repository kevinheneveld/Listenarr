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
using System.Text;
using System.Text.RegularExpressions;

namespace Listenarr.Application.Common
{
    /// <summary>
    /// The one way a metadata value becomes a folder or file name segment.
    /// Shared by the import/rename naming service and the organize planner so
    /// the two can never disagree about what a book's canonical folder is
    /// called: colons (and path separators) become " - ", every other
    /// portable-invalid character becomes "_". The organize sweep once used
    /// its own sanitizer that turned ":" into "_", so per-book organize and
    /// the library sweep produced different "canonical" folders for any title
    /// with a colon and the sweep kept proposing moves into a second copy of
    /// a folder that already existed under the other spelling.
    /// </summary>
    public static class PathComponentSanitizer
    {
        public static readonly IReadOnlySet<char> PortableInvalidFileNameChars = BuildPortableInvalidFileNameChars();

        public static readonly IReadOnlySet<string> ReservedWindowsDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "COM¹", "COM²", "COM³",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "LPT¹", "LPT²", "LPT³"
        };

        /// <summary>
        /// Sanitize one path segment. Returns an empty string when nothing
        /// printable survives; callers decide whether that means "Unknown"
        /// (a file name) or "drop the segment" (an optional folder level).
        /// </summary>
        public static string Sanitize(string? pathComponent)
        {
            if (string.IsNullOrWhiteSpace(pathComponent))
            {
                return string.Empty;
            }

            var sanitized = new StringBuilder(pathComponent.Length + 8);
            foreach (var c in pathComponent)
            {
                if (char.IsControl(c))
                {
                    continue;
                }

                if (c == ':' || c == '/' || c == '\\')
                {
                    sanitized.Append(" - ");
                }
                else if (PortableInvalidFileNameChars.Contains(c))
                {
                    sanitized.Append('_');
                }
                else
                {
                    sanitized.Append(c);
                }
            }

            var result = sanitized.ToString();
            result = Regex.Replace(result, @"\s+", " ");
            result = Regex.Replace(result, @"(?:\s*-\s*){2,}", " - ");
            result = Regex.Replace(result, @"_+", "_");
            result = result.Trim();
            result = result.TrimEnd('.', ' ');
            result = Regex.Replace(result, @"^\s*[-_]+\s*", string.Empty);
            result = Regex.Replace(result, @"\s*[-_]+\s*$", string.Empty);

            if (string.IsNullOrWhiteSpace(result))
            {
                return string.Empty;
            }

            var extensionSeparator = result.IndexOf('.');
            var deviceNameStem = extensionSeparator >= 0 ? result[..extensionSeparator] : result;
            if (ReservedWindowsDeviceNames.Contains(deviceNameStem))
            {
                result = extensionSeparator >= 0
                    ? deviceNameStem + "_" + result[extensionSeparator..]
                    : result + "_";
            }

            return result;
        }

        private static HashSet<char> BuildPortableInvalidFileNameChars()
        {
            var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());
            foreach (var c in "<>:\"/\\|?*")
            {
                invalidChars.Add(c);
            }

            for (var i = 0; i < 32; i++)
            {
                invalidChars.Add((char)i);
            }

            return invalidChars;
        }
    }
}
