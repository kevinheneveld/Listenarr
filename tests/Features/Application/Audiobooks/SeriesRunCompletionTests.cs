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
using Listenarr.Application.Audiobooks.Series;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Area", "Series")]
    [Trait("Name", "SeriesRunCompletionTests")]
    public class SeriesRunCompletionTests
    {
        private static CachedSeriesCatalogBook Entry(string title, string number, string narrator, string? publisher = "Pub")
            => new()
            {
                Asin = "B" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant(),
                Title = title,
                Authors = new List<string> { "Test Author" },
                Narrators = new List<string> { narrator },
                Publisher = publisher,
                SeriesNumber = number,
            };

        private static string Key(string title, string number)
            => SeriesWorkKey.Build(title, new List<string> { "Test Author" }, number);

        [Fact]
        public void BestRun_PrefersTheNearlyCompleteEdition_OverScatteredOwnership()
        {
            // Nine works, two runs: narrator A covers all nine, narrator B
            // covers three. The user owns 8 of A's and 0 of B's — best run
            // must be A at 8/9, not overall bookkeeping.
            var entries = new List<CachedSeriesCatalogBook>();
            for (var i = 1; i <= 9; i++) entries.Add(Entry($"Book {i}", i.ToString(), "Narrator A"));
            for (var i = 1; i <= 3; i++) entries.Add(Entry($"Book {i}", i.ToString(), "Narrator B"));

            var owned = new HashSet<string>(Enumerable.Range(1, 8).Select(i => Key($"Book {i}", i.ToString())));

            var best = SeriesRunCompletion.BestRun(entries, owned);

            Assert.NotNull(best);
            Assert.Equal(9, best!.TotalWorks);
            Assert.Equal(8, best.OwnedWorks);
            Assert.Contains("Narrator A", best.Narrators);
        }

        [Fact]
        public void BestRun_SingleWorkRun_CannotClaimSeriesComplete()
        {
            // A lone omnibus recording grouped as its own "run" at 1/1 must
            // not outrank a real run — a nine-book series is not 100%
            // complete because one boxed set is owned.
            var entries = new List<CachedSeriesCatalogBook>();
            for (var i = 1; i <= 9; i++) entries.Add(Entry($"Book {i}", i.ToString(), "Narrator A"));
            entries.Add(Entry("The Complete Series", "1-9", "Narrator C", "Boxset House"));

            var owned = new HashSet<string> { Key("The Complete Series", "1-9"), Key("Book 1", "1") };

            var best = SeriesRunCompletion.BestRun(entries, owned);

            Assert.NotNull(best);
            Assert.True(best!.TotalWorks > 1, $"single-work run must not win: {best.Label} {best.OwnedWorks}/{best.TotalWorks}");
        }

        [Fact]
        public void BestRun_NoEntries_ReturnsNull()
        {
            Assert.Null(SeriesRunCompletion.BestRun(Array.Empty<CachedSeriesCatalogBook>(), new HashSet<string>()));
        }

        [Fact]
        public void BestRun_NothingOwned_StillReportsLargestRunShape()
        {
            var entries = new List<CachedSeriesCatalogBook>();
            for (var i = 1; i <= 5; i++) entries.Add(Entry($"Book {i}", i.ToString(), "Narrator A"));

            var best = SeriesRunCompletion.BestRun(entries, new HashSet<string>());

            Assert.NotNull(best);
            Assert.Equal(0, best!.OwnedWorks);
            Assert.Equal(5, best.TotalWorks);
            Assert.Equal(0, best.Completion);
        }
    }
}
