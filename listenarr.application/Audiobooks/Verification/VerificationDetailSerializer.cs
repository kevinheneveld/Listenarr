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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// The one place that knows how a <see cref="VerificationVerdict"/> is laid
    /// out in Audiobook.VerificationDetailJson: camelCase, string enums, nulls
    /// omitted, transcript excluded (it has its own column). Shared by the
    /// verification worker that writes it and the AI review backfill that
    /// reads stored verdicts back.
    /// </summary>
    public static class VerificationDetailSerializer
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string Serialize(VerificationVerdict verdict) =>
            JsonSerializer.Serialize(verdict with { Transcript = null }, Options);

        /// <summary>Null on empty or malformed JSON — a stored verdict from an older build is still readable.</summary>
        public static VerificationVerdict? TryDeserialize(string? detailJson)
        {
            if (string.IsNullOrWhiteSpace(detailJson)) return null;
            try
            {
                return JsonSerializer.Deserialize<VerificationVerdict>(detailJson, Options);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
