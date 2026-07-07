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
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_SeriesHealthTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_SeriesHealthTests : BaseTests
    {
        private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

        private async Task<Audiobook> AddBookAsync(string title, string series, bool owned, string? position = null)
        {
            var builder = new AudiobookBuilder()
                .WithTitle(title)
                .WithSeries(series)
                .WithBasePath(FileService.GetTempPath());
            if (position != null)
            {
                builder = builder.WithSeriesNumber(position);
            }

            var book = await _audiobookRepository.AddAsync(builder.Build());
            if (owned)
            {
                await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                    .WithAudiobook(book)
                    .WithPath(Path.Join(FileService.GetTempPath(), $"{title}.m4b"))
                    .Build());
            }
            return book;
        }

        private async Task CacheCatalogAsync(string series, int totalBooks, string region = "us")
        {
            await _audiobookRepository.UpsertCachedSeriesAsync(new SeriesCacheEntry
            {
                SeriesName = series,
                Region = region,
                // Realistic rows: catalog entries carry series positions, which
                // is what keeps distinct works distinct under work-keying.
                CatalogBooks = Enumerable.Range(1, totalBooks)
                    .Select(i => new CachedSeriesCatalogBook { Title = $"{series} #{i}", SeriesNumber = i.ToString() })
                    .ToList(),
                LastFetchedAt = DateTime.UtcNow
            });
        }

        private static JsonElement RowFor(JsonElement payload, string name)
        {
            foreach (var row in payload.GetProperty("rows").EnumerateArray())
            {
                if (string.Equals(row.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }
            throw new Xunit.Sdk.XunitException($"row '{name}' not found");
        }

        [Fact]
        [Trait("Method", "GetSeriesHealth")]
        [Trait("Scenario", "CatalogAware_IncompleteWhenCatalogLarger")]
        public async Task SeriesHealth_CatalogAware_IncompleteWhenCatalogLarger()
        {
            // Owns 2 of a 5-book catalog series — tracked-only logic would call
            // this "complete" (no tracked gaps); catalog-aware must not.
            await AddBookAsync("Alpha 1", "Alpha Saga", owned: true, position: "1");
            await AddBookAsync("Alpha 2", "Alpha Saga", owned: true, position: "2");
            await CacheCatalogAsync("Alpha Saga", totalBooks: 5);

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = Assert.IsType<OkObjectResult>(await controller.GetSeriesHealth(CancellationToken.None));
            var row = RowFor(ToJson(ok.Value), "Alpha Saga");

            Assert.Equal(2, row.GetProperty("owned").GetInt32());
            Assert.Equal(0, row.GetProperty("missingTracked").GetInt32());
            Assert.Equal(5, row.GetProperty("catalogTotal").GetInt32());
            Assert.False(row.GetProperty("complete").GetBoolean());
        }

        [Fact]
        [Trait("Method", "GetSeriesHealth")]
        [Trait("Scenario", "CatalogAware_CompleteWhenOwnedMatchesCatalog")]
        public async Task SeriesHealth_CatalogAware_CompleteWhenOwnedMatchesCatalog()
        {
            await AddBookAsync("Beta 1", "Beta Duo", owned: true, position: "1");
            await AddBookAsync("Beta 2", "Beta Duo", owned: true, position: "2");
            await CacheCatalogAsync("Beta Duo", totalBooks: 2);

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = Assert.IsType<OkObjectResult>(await controller.GetSeriesHealth(CancellationToken.None));
            var row = RowFor(ToJson(ok.Value), "Beta Duo");

            Assert.Equal(2, row.GetProperty("catalogTotal").GetInt32());
            Assert.True(row.GetProperty("complete").GetBoolean());
        }

        [Fact]
        [Trait("Method", "GetSeriesHealth")]
        [Trait("Scenario", "NoCatalog_FallsBackToTrackedOnly")]
        public async Task SeriesHealth_NoCatalog_FallsBackToTrackedOnly()
        {
            await AddBookAsync("Gamma 1", "Gamma Cycle", owned: true, position: "1");
            await AddBookAsync("Gamma 2", "Gamma Cycle", owned: false, position: "2"); // tracked, wanted

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = Assert.IsType<OkObjectResult>(await controller.GetSeriesHealth(CancellationToken.None));
            var row = RowFor(ToJson(ok.Value), "Gamma Cycle");

            Assert.Equal(JsonValueKind.Null, row.GetProperty("catalogTotal").ValueKind);
            Assert.Equal(1, row.GetProperty("owned").GetInt32());
            Assert.Equal(1, row.GetProperty("missingTracked").GetInt32());
            Assert.False(row.GetProperty("complete").GetBoolean());
        }

        [Fact]
        [Trait("Method", "GetSeriesHealth")]
        [Trait("Scenario", "CatalogMatching_IsCaseAndPunctuationTolerant")]
        public async Task SeriesHealth_CatalogMatching_IsCaseAndPunctuationTolerant()
        {
            // Library spells it differently than the cache row was written —
            // the repository-side normalization must still match them.
            await AddBookAsync("Delta 1", "the delta files", owned: true, position: "1");
            await AddBookAsync("Delta 2", "the delta files", owned: true, position: "2");
            await CacheCatalogAsync("The Delta Files", totalBooks: 3);

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = Assert.IsType<OkObjectResult>(await controller.GetSeriesHealth(CancellationToken.None));
            var row = RowFor(ToJson(ok.Value), "the delta files");

            Assert.Equal(3, row.GetProperty("catalogTotal").GetInt32());
            Assert.False(row.GetProperty("complete").GetBoolean());
        }

        [Fact]
        [Trait("Method", "GetSeriesHealth")]
        [Trait("Scenario", "CatalogTotal_CountsWorksNotRecordings")]
        public async Task SeriesHealth_CatalogTotal_CountsWorksNotRecordings()
        {
            // Six catalog rows = two works × three recordings (a narration, a
            // re-release, a full-cast dramatization). The user owns one
            // recording of work 1 — totals must read 1/2, not 1/6.
            await AddBookAsync("Epsilon Book", "Epsilon Cycle", owned: true, position: "1");
            await _audiobookRepository.UpsertCachedSeriesAsync(new SeriesCacheEntry
            {
                SeriesName = "Epsilon Cycle",
                Region = "us",
                CatalogBooks = new List<CachedSeriesCatalogBook>
                {
                    new() { Title = "Epsilon Book", SeriesNumber = "1", Narrators = new List<string> { "Narrator A" } },
                    new() { Title = "Epsilon Book", SeriesNumber = "1", Narrators = new List<string> { "Narrator B" } },
                    new() { Title = "Epsilon Book", Subtitle = "A Full Cast Dramatization", SeriesNumber = "1", Narrators = new List<string> { "Full Cast" } },
                    new() { Title = "Epsilon Sequel", SeriesNumber = "2", Narrators = new List<string> { "Narrator A" } },
                    new() { Title = "Epsilon Sequel", SeriesNumber = "2", Narrators = new List<string> { "Narrator B" } },
                    new() { Title = "Epsilon Sequel", Subtitle = "A Full Cast Dramatization", SeriesNumber = "2", Narrators = new List<string> { "Full Cast" } },
                },
                LastFetchedAt = DateTime.UtcNow
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = Assert.IsType<OkObjectResult>(await controller.GetSeriesHealth(CancellationToken.None));
            var row = RowFor(ToJson(ok.Value), "Epsilon Cycle");

            Assert.Equal(2, row.GetProperty("catalogTotal").GetInt32());
            Assert.Equal(1, row.GetProperty("owned").GetInt32());
            Assert.False(row.GetProperty("complete").GetBoolean());
            Assert.True(row.GetProperty("editions").GetInt32() >= 2);
        }
    }
}
