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
using Listenarr.Application.Audiobooks.Verification;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Area", "Verification")]
    [Trait("Name", "VerificationEtaMathTests")]
    public class VerificationEtaMathTests
    {
        private static VerificationJobRecord Job(string? idsJson, DateTime enqueued, DateTime completed) =>
            new()
            {
                Id = Guid.NewGuid(),
                AudiobookIdsJson = idsJson,
                Status = "Completed",
                EnqueuedAt = enqueued,
                CompletedAt = completed
            };

        [Fact]
        public void CountBooks_ParsesJson_AndHandlesNullAndGarbage()
        {
            Assert.Equal(3, VerificationEtaMath.CountBooks("[1,2,3]"));
            Assert.Null(VerificationEtaMath.CountBooks(null));
            Assert.Null(VerificationEtaMath.CountBooks(""));
            Assert.Null(VerificationEtaMath.CountBooks("not json"));
        }

        [Fact]
        public void EmptyHistory_FallsBackToDefault()
        {
            var avg = VerificationEtaMath.ComputeAvgSecondsPerBook(Array.Empty<VerificationJobRecord>());
            Assert.Equal(VerificationEtaMath.FallbackSecondsPerBook, avg);
        }

        [Fact]
        public void SerialQueue_AttributesWorkWindow_NotQueueWait()
        {
            // Both jobs enqueued at t0; job A runs 0→10min (5 books), job B waits
            // behind it and runs 10→14min (2 books). Naive (Completed−Enqueued)
            // would charge B fourteen minutes; serial attribution charges four.
            var t0 = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);
            var records = new[]
            {
                Job("[1,2,3,4,5]", t0, t0.AddMinutes(10)),
                Job("[6,7]", t0, t0.AddMinutes(14))
            };

            var avg = VerificationEtaMath.ComputeAvgSecondsPerBook(records);

            // (600s + 240s) / 7 books = 120s
            Assert.Equal(120, avg, precision: 0);
        }

        [Fact]
        public void WholeLibraryRows_AreExcluded_ButStillAdvanceTheClock()
        {
            var t0 = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);
            var records = new[]
            {
                Job(null, t0, t0.AddMinutes(30)),                 // whole-library walk: unknown size
                Job("[1,2]", t0.AddMinutes(30), t0.AddMinutes(34)) // 2 books in 4 minutes
            };

            var avg = VerificationEtaMath.ComputeAvgSecondsPerBook(records);

            Assert.Equal(120, avg, precision: 0);
        }

        [Fact]
        public void Average_IsClampedToSaneRange()
        {
            var t0 = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);

            var tooFast = new[] { Job("[1,2,3,4,5,6,7,8,9,10]", t0, t0.AddSeconds(5)) };
            Assert.Equal(VerificationEtaMath.MinSecondsPerBook,
                VerificationEtaMath.ComputeAvgSecondsPerBook(tooFast));

            var tooSlow = new[] { Job("[1]", t0, t0.AddHours(5)) };
            Assert.Equal(VerificationEtaMath.MaxSecondsPerBook,
                VerificationEtaMath.ComputeAvgSecondsPerBook(tooSlow));
        }
    }
}
