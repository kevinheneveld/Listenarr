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

using System.Collections.Concurrent;

namespace Listenarr.Application.Common
{
    /// <summary>
    /// Rate-limits a warning that would otherwise repeat on every poll for the same
    /// unchanged condition. The first occurrence of a key is allowed; further
    /// occurrences inside the window are suppressed (callers log them at debug), and
    /// the warning fires again once the window elapses so a still-present problem
    /// stays visible.
    ///
    /// Live case: the download monitor re-translated the content paths of ~26 stale
    /// NZBGet history entries every 15 seconds and warned each time the directory was
    /// gone — 99,000 warnings and ~900 MB of log per day saying the same thing.
    /// </summary>
    public sealed class RepeatedLogSuppressor
    {
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(6);
        private const int PruneAboveEntries = 5000;

        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastLogged = new(StringComparer.Ordinal);
        private readonly TimeProvider _timeProvider;

        public RepeatedLogSuppressor(TimeProvider? timeProvider = null)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>True when the caller should emit the full-severity log line for this key now.</summary>
        public bool ShouldLog(string key, TimeSpan? window = null)
        {
            var now = _timeProvider.GetUtcNow();
            var span = window ?? DefaultWindow;
            var allowed = false;
            _lastLogged.AddOrUpdate(
                key,
                _ => { allowed = true; return now; },
                (_, last) =>
                {
                    if (now - last >= span)
                    {
                        allowed = true;
                        return now;
                    }
                    return last;
                });

            if (_lastLogged.Count > PruneAboveEntries)
            {
                Prune(now, span);
            }

            return allowed;
        }

        private void Prune(DateTimeOffset now, TimeSpan span)
        {
            foreach (var entry in _lastLogged)
            {
                if (now - entry.Value >= span)
                {
                    _lastLogged.TryRemove(entry.Key, out _);
                }
            }
        }
    }
}
