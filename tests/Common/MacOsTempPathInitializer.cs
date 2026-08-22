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

using System.Runtime.CompilerServices;

namespace Listenarr.Tests.Common
{
    /// <summary>
    /// macOS aliases /tmp and /var as symlinks into /private. The pinned-
    /// directory hierarchy walk opens every path component with O_NOFOLLOW and
    /// correctly refuses symlinked ancestors, so every Path.GetTempPath() used
    /// by the suite must resolve to the real /private/... location before any
    /// fixture builds a path from it.
    /// </summary>
    internal static class MacOsTempPathInitializer
    {
        [ModuleInitializer]
        internal static void CanonicalizeTempDir()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            var tmp = Environment.GetEnvironmentVariable("TMPDIR");
            if (!string.IsNullOrEmpty(tmp)
                && (tmp.StartsWith("/var/", StringComparison.Ordinal)
                    || tmp.StartsWith("/tmp/", StringComparison.Ordinal)
                    || tmp is "/tmp" or "/var"))
            {
                Environment.SetEnvironmentVariable("TMPDIR", "/private" + tmp);
            }
        }
    }
}
