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

namespace Listenarr.Infrastructure.AiAssist
{
    /// <summary>
    /// Process-wide memory of an AI-assist endpoint that just failed at the transport level
    /// (unreachable host, refused connection, timed-out request). While the cooldown runs,
    /// consumers skip the call and keep their deterministic behavior immediately instead of
    /// each paying the full connect/request timeout.
    ///
    /// Live case: the Ollama box answered ping but nothing listened on its port, so every
    /// automatic-search pick that had candidates waited 100s for the release gate before
    /// falling back — a Wanted-page sweep crawled at one book per two minutes.
    ///
    /// The typed AI client is transient, so this state lives in its own singleton. Keyed by
    /// base URL: changing the endpoint in settings starts clean.
    /// </summary>
    public sealed class AiAssistEndpointHealth
    {
        public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

        private readonly object _sync = new();
        private string? _baseUrl;
        private DateTimeOffset? _unavailableUntil;

        /// <summary>True while the cooldown opened for <paramref name="baseUrl"/> is still running.</summary>
        public bool IsUnavailable(string baseUrl, DateTimeOffset now)
        {
            lock (_sync)
            {
                return _unavailableUntil.HasValue
                    && string.Equals(_baseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)
                    && _unavailableUntil.Value > now;
            }
        }

        /// <summary>When the current cooldown ends, or null when none is running.</summary>
        public DateTimeOffset? UnavailableUntil(string baseUrl, DateTimeOffset now)
        {
            lock (_sync)
            {
                return IsUnavailableUnlocked(baseUrl, now) ? _unavailableUntil : null;
            }
        }

        /// <summary>
        /// Opens (or extends) the cooldown. Returns true when this call opened it — the caller
        /// logs the warning once, later failures inside the window stay quiet.
        /// </summary>
        public bool MarkUnavailable(string baseUrl, DateTimeOffset now, TimeSpan? cooldown = null)
        {
            lock (_sync)
            {
                var wasRunning = IsUnavailableUnlocked(baseUrl, now);
                _baseUrl = baseUrl;
                _unavailableUntil = now + (cooldown ?? DefaultCooldown);
                return !wasRunning;
            }
        }

        /// <summary>A successful call (or a successful settings-page test) clears the cooldown.</summary>
        public void MarkAvailable()
        {
            lock (_sync)
            {
                _baseUrl = null;
                _unavailableUntil = null;
            }
        }

        private bool IsUnavailableUnlocked(string baseUrl, DateTimeOffset now)
            => _unavailableUntil.HasValue
               && string.Equals(_baseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)
               && _unavailableUntil.Value > now;
    }
}
