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
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Features.Api.Services
{
    public class SeriesCatalogBackfillServiceTests
    {
        private static Audiobook Book(string title, string? series) =>
            new() { Title = title, Series = series };

        [Fact]
        public void SelectSeriesToBackfill_IncludesOnlySeriesWithEnoughBooks()
        {
            var books = new List<Audiobook>
            {
                Book("Foundation", "Foundation"),
                Book("Foundation and Empire", "Foundation"), // Foundation has 2 → included
                Book("Solo Novel", "Solo Series"),           // 1 book → excluded
                Book("Standalone", null),                    // no series → ignored
            };

            var selected = SeriesCatalogBackfillService.SelectSeriesToBackfill(
                books, SeriesCatalogBackfillService.MinBooksForRealSeries);

            Assert.Equal(new[] { "Foundation" }, selected);
        }

        [Fact]
        public void SelectSeriesToBackfill_GroupsCaseInsensitivelyAndTrims()
        {
            var books = new List<Audiobook>
            {
                Book("A", "Wheel of Time"),
                Book("B", "wheel of time "),
                Book("C", " WHEEL OF TIME"),
            };

            var selected = SeriesCatalogBackfillService.SelectSeriesToBackfill(books, 2);

            Assert.Single(selected);
            Assert.Equal("Wheel of Time", selected[0]);
        }

        [Fact]
        public void SelectSeriesToBackfill_EmptyLibrary_ReturnsNothing()
        {
            Assert.Empty(SeriesCatalogBackfillService.SelectSeriesToBackfill(new List<Audiobook>(), 2));
        }
    }
}
