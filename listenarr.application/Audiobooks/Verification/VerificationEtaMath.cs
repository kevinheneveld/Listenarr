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

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Pure math for the dashboard's background-activity panel: how fast is
    /// verification actually running, and how long until the queue drains.
    /// Jobs run serially, so a job's processing time is bounded below by the
    /// previous job's completion — using (CompletedAt − EnqueuedAt) directly
    /// would count queue-wait as work and wildly inflate the average.
    /// </summary>
    public static class VerificationEtaMath
    {
        /// <summary>Fallback when no completed history exists yet (observed live: 2–4 min/book with escalations).</summary>
        public const double FallbackSecondsPerBook = 180;

        public const double MinSecondsPerBook = 10;
        public const double MaxSecondsPerBook = 3600;

        /// <summary>Book count a job row covers; null AudiobookIdsJson = whole-library walk (unknown here).</summary>
        public static int? CountBooks(string? audiobookIdsJson)
        {
            if (string.IsNullOrWhiteSpace(audiobookIdsJson)) return null;
            try
            {
                var ids = JsonSerializer.Deserialize<List<int>>(audiobookIdsJson);
                return ids?.Count;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Average seconds per book from completed job rows (any order; sorted
        /// internally by CompletedAt). Serial-queue attribution: a job's work
        /// window starts at max(its EnqueuedAt, previous job's CompletedAt).
        /// Whole-library rows (unknown book count) are excluded. Falls back to
        /// <see cref="FallbackSecondsPerBook"/> when no usable sample exists;
        /// the result is clamped to a sane range either way.
        /// </summary>
        public static double ComputeAvgSecondsPerBook(IReadOnlyList<VerificationJobRecord> completed)
        {
            var ordered = completed
                .Where(r => r.CompletedAt != null)
                .OrderBy(r => r.CompletedAt)
                .ToList();

            double totalSeconds = 0;
            var totalBooks = 0;
            DateTime? previousCompleted = null;

            foreach (var record in ordered)
            {
                var books = CountBooks(record.AudiobookIdsJson);
                var start = previousCompleted.HasValue && previousCompleted.Value > record.EnqueuedAt
                    ? previousCompleted.Value
                    : record.EnqueuedAt;
                var seconds = (record.CompletedAt!.Value - start).TotalSeconds;
                previousCompleted = record.CompletedAt;

                if (books is > 0 && seconds > 0)
                {
                    totalSeconds += seconds;
                    totalBooks += books.Value;
                }
            }

            if (totalBooks == 0) return FallbackSecondsPerBook;
            return Math.Clamp(totalSeconds / totalBooks, MinSecondsPerBook, MaxSecondsPerBook);
        }
    }
}
