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
using Listenarr.Infrastructure.HostedServices.Catalog;

namespace Listenarr.Tests.Features.Infrastructure
{
    [Trait("Area", "HostedServices")]
    [Trait("Name", "SeriesCatalogBackfillTests")]
    public class SeriesCatalogBackfillTests
    {
        private static IReadOnlyCollection<string> Book(params string[] names) => names;

        [Fact]
        public void SelectSeriesToBackfill_RequiresMinimumBooks()
        {
            // Audible labels standalones as 1-member series — a single tracked
            // book must not trigger a catalog fetch.
            var result = SeriesCatalogBackfillProcessor.SelectSeriesToBackfill(
                new[] { Book("Lone Series"), Book("Real Series"), Book("Real Series") },
                minBooks: 2);

            Assert.Equal(["Real Series"], result);
        }

        [Fact]
        public void SelectSeriesToBackfill_MergesCaseInsensitively_KeepsFirstCasing()
        {
            var result = SeriesCatalogBackfillProcessor.SelectSeriesToBackfill(
                new[] { Book("The Expanse"), Book("the expanse"), Book("THE EXPANSE") },
                minBooks: 2);

            Assert.Equal(["The Expanse"], result);
        }

        [Fact]
        public void SelectSeriesToBackfill_CountsABookOncePerSeries()
        {
            // A book carrying the same series via membership AND the legacy
            // field must count once, not twice.
            var result = SeriesCatalogBackfillProcessor.SelectSeriesToBackfill(
                new[] { Book("Dune", "dune") },
                minBooks: 2);

            Assert.Empty(result);
        }

        [Fact]
        public void SelectSeriesToBackfill_IgnoresBlankNames()
        {
            var result = SeriesCatalogBackfillProcessor.SelectSeriesToBackfill(
                new[] { Book("", "  "), Book("  ") },
                minBooks: 1);

            Assert.Empty(result);
        }

        [Fact]
        public void SelectSeriesToBackfill_BookInMultipleSeries_CountsEach()
        {
            var result = SeriesCatalogBackfillProcessor.SelectSeriesToBackfill(
                new[]
                {
                    Book("Cosmere", "Mistborn"),
                    Book("Cosmere", "Mistborn"),
                },
                minBooks: 2);

            Assert.Equal(2, result.Count);
            Assert.Contains("Cosmere", result);
            Assert.Contains("Mistborn", result);
        }

        [Fact]
        public void IsEnabled_DefaultsOn_ExplicitFalseDisables()
        {
            var original = Environment.GetEnvironmentVariable("LISTENARR_AUTO_CACHE_SERIES_CATALOGS");
            try
            {
                Environment.SetEnvironmentVariable("LISTENARR_AUTO_CACHE_SERIES_CATALOGS", null);
                Assert.True(SeriesCatalogBackfillProcessor.IsEnabled());

                Environment.SetEnvironmentVariable("LISTENARR_AUTO_CACHE_SERIES_CATALOGS", "false");
                Assert.False(SeriesCatalogBackfillProcessor.IsEnabled());

                Environment.SetEnvironmentVariable("LISTENARR_AUTO_CACHE_SERIES_CATALOGS", "0");
                Assert.False(SeriesCatalogBackfillProcessor.IsEnabled());

                Environment.SetEnvironmentVariable("LISTENARR_AUTO_CACHE_SERIES_CATALOGS", "true");
                Assert.True(SeriesCatalogBackfillProcessor.IsEnabled());
            }
            finally
            {
                Environment.SetEnvironmentVariable("LISTENARR_AUTO_CACHE_SERIES_CATALOGS", original);
            }
        }
    }
}
