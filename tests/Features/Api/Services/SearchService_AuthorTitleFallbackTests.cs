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
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Listenarr.Application.Notification;
using Listenarr.Application.Search;
using Listenarr.Application.Search.Filters;
using Listenarr.Application.Search.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    // Audible's per-author catalog endpoint is incomplete in ways that bite the
    // backfill modal in practice:
    //
    //  - Some co-authored works appear only under one of the co-authors (Gwendy's
    //    Button Box is on Chizmar's author page, not Stephen King's). Narrow
    //    AUTHOR_TITLE search returns zero — needs fallback.
    //  - Some catalogs return a long list of titles but omit specific editions
    //    via attribution / pagination / featured-collection oddities (Asimov's
    //    catalog returns 97 titles but the English "Robots and Empire" /
    //    B0CSV7NJMB is not among them — only the Spanish edition makes it
    //    through the title filter). Narrow AUTHOR_TITLE returns one wrong-language
    //    result and the user sees a misleading "this is the only match" screen.
    //
    // The fix: when AUTHOR_TITLE produces fewer than NarrowResultsConfidenceThreshold
    // (5) candidates, supplement with a title-only Audible search and merge the
    // results, deduped by ASIN. Above the threshold the narrow path is trusted
    // and no extra call is made.
    public class SearchService_AuthorTitleFallbackTests
    {
        [Fact]
        public async Task IntelligentSearch_AuthorTitle_FallsBackToTitleOnly_WhenAuthorPageReturnsZero()
        {
            // Gwendy case: King's author page is missing the book entirely.
            var audibleMock = new Mock<AudibleService>(new HttpClient(), NullLogger<AudibleService>.Instance);
            audibleMock
                .Setup(s => s.SearchByAuthorAsync("Stephen King", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new() { Asin = "B0King01", Title = "The Stand", Authors = new() { new() { Name = "Stephen King" } } },
                        new() { Asin = "B0King02", Title = "It", Authors = new() { new() { Name = "Stephen King" } } }
                    },
                    TotalResults = 2
                });
            audibleMock
                .Setup(s => s.SearchByTitleAsync("Gwendy", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Asin = "B06XPLDVPC",
                            Title = "Gwendy's Button Box",
                            Authors = new() { new() { Name = "Stephen King" }, new() { Name = "Richard Chizmar" } }
                        }
                    },
                    TotalResults = 1
                });

            var service = CreateSearchService(audibleMock.Object);
            var results = await service.IntelligentSearchAsync("AUTHOR:Stephen King TITLE:Gwendy");

            Assert.NotNull(results);
            Assert.Single(results);
            Assert.Equal("B06XPLDVPC", results[0].Asin, ignoreCase: true);
            audibleMock.Verify(
                s => s.SearchByTitleAsync("Gwendy", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task IntelligentSearch_AuthorTitle_MergesNarrowAndBroadResults_WhenNarrowIsThin()
        {
            // Robots case: Asimov's author page returns 97 titles but the only one
            // that filters down to "Robots and Empire" is the Spanish edition
            // (B0BXFNTYWZ "Robots e Imperio [Robots and Empire]"). The narrow
            // result count of 1 is below the < 5 threshold, so the fallback fires
            // and the English edition (B0CSV7NJMB) found by title-only search is
            // merged into the candidate list.
            var audibleMock = new Mock<AudibleService>(new HttpClient(), NullLogger<AudibleService>.Instance);

            // Author-page narrow returns one match for "Robots and Empire" — the Spanish edition.
            audibleMock
                .Setup(s => s.SearchByAuthorAsync("Isaac Asimov", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Asin = "B0BXFNTYWZ",
                            Title = "Robots e Imperio [Robots and Empire]",
                            Authors = new() { new() { Name = "Isaac Asimov" } }
                        }
                    },
                    TotalResults = 1
                });

            // Title-only fallback returns both editions. The Spanish one (already in
            // narrow) should be deduped; the English one should be added.
            audibleMock
                .Setup(s => s.SearchByTitleAsync("Robots and Empire", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Asin = "B0BXFNTYWZ",
                            Title = "Robots e Imperio [Robots and Empire]",
                            Authors = new() { new() { Name = "Isaac Asimov" } }
                        },
                        new()
                        {
                            Asin = "B0CSV7NJMB",
                            Title = "Robots and Empire",
                            Authors = new() { new() { Name = "Isaac Asimov" } }
                        }
                    },
                    TotalResults = 2
                });

            var service = CreateSearchService(audibleMock.Object);
            var results = await service.IntelligentSearchAsync("AUTHOR:Isaac Asimov TITLE:Robots and Empire");

            Assert.NotNull(results);
            Assert.Equal(2, results.Count);
            var asins = results.Select(r => r.Asin).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("B0BXFNTYWZ", asins);
            Assert.Contains("B0CSV7NJMB", asins);
            audibleMock.Verify(
                s => s.SearchByTitleAsync("Robots and Empire", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.AtLeastOnce,
                "title-only supplement must fire when narrow returns < 5 results");
        }

        [Fact]
        public async Task IntelligentSearch_AuthorTitle_SkipsFallback_WhenNarrowHasFiveOrMoreMatches()
        {
            // When the narrow path returns >= 5 plausible matches, we trust the
            // author-page filtering and do NOT call title-only — preserves the old
            // behaviour for well-populated queries and avoids dumping unrelated
            // other-author results into the candidate list for common titles like
            // "Foundation" by Asimov.
            var audibleMock = new Mock<AudibleService>(new HttpClient(), NullLogger<AudibleService>.Instance);
            var asimov = new AudibleAuthor { Name = "Isaac Asimov" };
            audibleMock
                .Setup(s => s.SearchByAuthorAsync("Isaac Asimov", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new() { Asin = "B0Found01", Title = "Foundation", Authors = new() { asimov } },
                        new() { Asin = "B0Found02", Title = "Foundation and Empire", Authors = new() { asimov } },
                        new() { Asin = "B0Found03", Title = "Second Foundation", Authors = new() { asimov } },
                        new() { Asin = "B0Found04", Title = "Foundation's Edge", Authors = new() { asimov } },
                        new() { Asin = "B0Found05", Title = "Foundation and Earth", Authors = new() { asimov } },
                        new() { Asin = "B0Found06", Title = "Prelude to Foundation", Authors = new() { asimov } }
                    },
                    TotalResults = 6
                });

            var service = CreateSearchService(audibleMock.Object);
            var results = await service.IntelligentSearchAsync("AUTHOR:Isaac Asimov TITLE:Foundation");

            Assert.NotNull(results);
            Assert.Equal(6, results.Count);
            audibleMock.Verify(
                s => s.SearchByTitleAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.Never,
                "narrow path with >= 5 matches should not trigger the title-only supplement");
        }

        private static SearchService CreateSearchService(AudibleService audible)
        {
            var client = new HttpClient();
            var configuration = Mock.Of<IConfigurationService>();
            var logger = NullLogger<SearchService>.Instance;
            var openLibraryService = Mock.Of<IOpenLibraryService>();
            var imageCache = Mock.Of<IImageCacheService>();
            var converters = new MetadataConverters(imageCache, NullLogger<MetadataConverters>.Instance);
            var progress = new SearchProgressReporter(null, NullLogger<SearchProgressReporter>.Instance);
            var pipeline = new SearchResultFilterPipeline(Enumerable.Empty<ISearchResultFilter>(), NullLogger<SearchResultFilterPipeline>.Instance);
            var coordinator = new MetadataStrategyCoordinator(Enumerable.Empty<IMetadataStrategy>(), NullLogger<MetadataStrategyCoordinator>.Instance);
            var collector = new AsinCandidateCollector(NullLogger<AsinCandidateCollector>.Instance, openLibraryService, converters, progress);
            var enricher = new AsinEnricher(NullLogger<AsinEnricher>.Instance, coordinator, converters, pipeline, progress);
            var scorer = new SearchResultScorerService(NullLogger<SearchResultScorerService>.Instance);
            var handler = new AsinSearchHandler(NullLogger<AsinSearchHandler>.Instance, configuration, audible, Mock.Of<IAudnexusService>(), converters, progress);

            return new SearchService(
                client,
                configuration,
                logger,
                Mock.Of<IIndexerRepository>(),
                Mock.Of<IApiConfigurationRepository>(),
                audible,
                converters,
                progress,
                collector,
                enricher,
                scorer,
                handler,
                Enumerable.Empty<IIndexerSearchProvider>());
        }
    }
}
