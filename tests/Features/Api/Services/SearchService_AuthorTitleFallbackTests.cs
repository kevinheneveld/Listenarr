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
    // The AUTHOR_TITLE branch is responsible for the backfill modal's candidate
    // search. It's two cooperating behaviours:
    //
    //   * BROADENING: Audible's per-author catalog is incomplete in ways that
    //     bite users in practice. When the narrow author-page-plus-title path
    //     returns fewer than NarrowResultsConfidenceThreshold (5) candidates,
    //     the branch supplements with a title-only Audible search and merges
    //     the results deduped by ASIN. Covers co-authored works (Gwendy's
    //     Button Box on Chizmar's page only, not King's) and incomplete
    //     catalog responses (Asimov's 97-title catalog missing the English
    //     "Robots and Empire").
    //
    //   * COLLAPSE: After the broadening merge, if any candidate's normalized
    //     title is *equal* to the user's title AND its author overlaps the
    //     user's author, that candidate is unambiguously the book — the rest
    //     are noise in the picker. Collapse to only the exact matches
    //     (preserving multiple narrators / abridgements of the same title).
    //     When no candidate exactly matches, return the full merged list so
    //     the user can still disambiguate from the broader set.
    public class SearchService_AuthorTitleFallbackTests
    {
        // ──────────────────────────────────────────────────────────────────
        // BROADENING — narrow path is thin, supplement and merge
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task IntelligentSearch_AuthorTitle_FallsBackToTitleOnly_WhenAuthorPageReturnsZero()
        {
            // Gwendy case: King's author page is missing the book entirely.
            // Note the user typed "Gwendy" (one word) — the response title
            // "Gwendy's Button Box" does NOT normalize-equal "Gwendy", so the
            // exact-match collapse does not fire. The result is the single
            // candidate that came back from the title-only supplement.
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
        public async Task IntelligentSearch_AuthorTitle_MergeAndCollapse_WhenSupplementSurfacesExactMatch()
        {
            // Robots case: Asimov's author page returns one match (Spanish
            // edition). The narrow count of 1 triggers the supplement; the
            // supplement returns both editions; merge dedupes the Spanish
            // duplicate; the exact-match collapse then keeps only B0CSV7NJMB
            // because its normalized title "robots and empire" equals the
            // query while the Spanish edition "robots e imperio robots and
            // empire" does not. The Spanish entry is filtered out as noise.
            var audibleMock = new Mock<AudibleService>(new HttpClient(), NullLogger<AudibleService>.Instance);

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
            Assert.Single(results);
            Assert.Equal("B0CSV7NJMB", results[0].Asin, ignoreCase: true);
            audibleMock.Verify(
                s => s.SearchByTitleAsync("Robots and Empire", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.AtLeastOnce,
                "title-only supplement must fire when narrow returns < 5 results");
        }

        [Fact]
        public async Task IntelligentSearch_AuthorTitle_SkipsFallback_WhenNarrowHasFiveOrMoreMatches()
        {
            // When the narrow path returns >= 5 candidates we trust the
            // author-page filtering and do NOT call title-only (avoids
            // dumping unrelated other-author results into the candidate
            // list). Query is "Foundation"; the 6 candidates all contain
            // the substring (so narrow's naive IndexOf passes them all)
            // but none normalize-equal it, so the exact-match collapse
            // also does not fire — the full 6-item list is returned.
            var audibleMock = new Mock<AudibleService>(new HttpClient(), NullLogger<AudibleService>.Instance);
            var asimov = new AudibleAuthor { Name = "Isaac Asimov" };
            audibleMock
                .Setup(s => s.SearchByAuthorAsync("Isaac Asimov", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new() { Asin = "B0Found01", Title = "Foundation Trilogy", Authors = new() { asimov } },
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

        // ──────────────────────────────────────────────────────────────────
        // EXACT-MATCH COLLAPSE
        // ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task IntelligentSearch_AuthorTitle_CollapsesToExactMatch_WhenOneCandidateExactlyMatches()
        {
            // Narrow path returns 6 Foundation-related titles for "Foundation"
            // — one is an exact normalized match ("Foundation"), the others
            // are series sequels / collections that share the word. Without
            // the collapse the user would see all 6; with the collapse only
            // the exact match remains.
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
            Assert.Single(results);
            Assert.Equal("B0Found01", results[0].Asin, ignoreCase: true);
        }

        [Fact]
        public async Task IntelligentSearch_AuthorTitle_CollapseKeepsEditionVariants()
        {
            // Live regression: searching the plain title "Warbreaker" returned
            // FEWER editions than a noisy filename query, because the collapse
            // kept only the normalize-equal retail edition and dropped the
            // GraphicAudio "(2 of 3) [Dramatized Adaptation]" and Tenth
            // Anniversary entries — which are the same book in different
            // packaging. Edition-suffix variants must survive the collapse.
            var audibleMock = new Mock<AudibleService>(new HttpClient(), NullLogger<AudibleService>.Instance);
            var sanderson = new AudibleAuthor { Name = "Brandon Sanderson" };
            audibleMock
                .Setup(s => s.SearchByAuthorAsync("Brandon Sanderson", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new() { Asin = "B0Warb001", Title = "Warbreaker", Authors = new() { sanderson } },
                        new() { Asin = "B0Warb2of3", Title = "Warbreaker (2 of 3) [Dramatized Adaptation]", Authors = new() { sanderson } },
                        new() { Asin = "B0Warb1of3", Title = "Warbreaker (1 of 3) [Dramatized Adaptation]", Authors = new() { sanderson } },
                        new() { Asin = "B0WarbTAP1", Title = "Warbreaker: Tenth Anniversary Edition (Part 1 of 2) (Dramatized Adaptation)", Authors = new() { sanderson } },
                        new() { Asin = "B0WarbTAP2", Title = "Warbreaker: Tenth Anniversary Edition (Part 2 of 2) (Dramatized Adaptation)", Authors = new() { sanderson } }
                    },
                    TotalResults = 5
                });

            var service = CreateSearchService(audibleMock.Object);
            var results = await service.IntelligentSearchAsync("AUTHOR:Brandon Sanderson TITLE:Warbreaker");

            Assert.NotNull(results);
            Assert.Equal(5, results.Count);
        }

        [Fact]
        public async Task IntelligentSearch_AuthorTitle_CollapseStillDropsDifferentBooksSharingThePrefix()
        {
            // The widened collapse must stay conservative: "1634: The Baltic
            // War" shares the title prefix but its suffix is a real subtitle,
            // not edition packaging — it is a different book and must still be
            // filtered when an exact "1634" match exists.
            var audibleMock = new Mock<AudibleService>(new HttpClient(), NullLogger<AudibleService>.Instance);
            var flint = new AudibleAuthor { Name = "Eric Flint" };
            audibleMock
                .Setup(s => s.SearchByAuthorAsync("Eric Flint", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new() { Asin = "B01634000", Title = "1634", Authors = new() { flint } },
                        new() { Asin = "B01634BAL", Title = "1634: The Baltic War", Authors = new() { flint } }
                    },
                    TotalResults = 2
                });
            audibleMock
                .Setup(s => s.SearchByTitleAsync("1634", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse { Results = new List<AudibleSearchResult>(), TotalResults = 0 });

            var service = CreateSearchService(audibleMock.Object);
            var results = await service.IntelligentSearchAsync("AUTHOR:Eric Flint TITLE:1634");

            Assert.NotNull(results);
            Assert.Single(results);
            Assert.Equal("B01634000", results[0].Asin, ignoreCase: true);
        }

        [Fact]
        public async Task IntelligentSearch_AuthorTitle_KeepsAllExactMatches_WhenMultipleNarratorsExist()
        {
            // Same book, three different narrators / abridgements on Audible
            // (real pattern for popular titles). All three normalize-equal
            // "Foundation" and all are by Isaac Asimov — the user needs to
            // see all three so they can pick the narrator they own. The
            // collapse must NOT pick just one; it must keep every exact
            // match. Adjacent series-titles are still filtered out.
            var audibleMock = new Mock<AudibleService>(new HttpClient(), NullLogger<AudibleService>.Instance);
            var asimov = new AudibleAuthor { Name = "Isaac Asimov" };
            audibleMock
                .Setup(s => s.SearchByAuthorAsync("Isaac Asimov", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new() { Asin = "B0FoundN1", Title = "Foundation", Authors = new() { asimov } },
                        new() { Asin = "B0FoundN2", Title = "Foundation", Authors = new() { asimov } },
                        new() { Asin = "B0FoundN3", Title = "Foundation", Authors = new() { asimov } },
                        new() { Asin = "B0FoundEE", Title = "Foundation's Edge", Authors = new() { asimov } },
                        new() { Asin = "B0FoundFE", Title = "Foundation and Earth", Authors = new() { asimov } }
                    },
                    TotalResults = 5
                });

            var service = CreateSearchService(audibleMock.Object);
            var results = await service.IntelligentSearchAsync("AUTHOR:Isaac Asimov TITLE:Foundation");

            Assert.NotNull(results);
            Assert.Equal(3, results.Count);
            var asins = results.Select(r => r.Asin).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("B0FoundN1", asins);
            Assert.Contains("B0FoundN2", asins);
            Assert.Contains("B0FoundN3", asins);
        }

        [Fact]
        public async Task IntelligentSearch_AuthorTitle_DoesNotCollapse_WhenTitleMatchesButAuthorDiffers()
        {
            // Title exactly matches, but the candidate's author is not the
            // one the user typed. That's the wrong-book-with-shared-title
            // case (e.g. "The Stand" by King vs by a different author). The
            // collapse must NOT fire — return the full merged list so the
            // user can spot the mismatch and disambiguate.
            //
            // Narrow returns one wrong-author match (1 < 5 → supplement
            // fires), supplement adds an unrelated King book, neither passes
            // exact-match (author mismatches), full merged list returned.
            var audibleMock = new Mock<AudibleService>(new HttpClient(), NullLogger<AudibleService>.Instance);
            audibleMock
                .Setup(s => s.SearchByAuthorAsync("Stephen King", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new() { Asin = "B0WrongAuth", Title = "The Stand", Authors = new() { new() { Name = "Some Other Author" } } }
                    },
                    TotalResults = 1
                });
            audibleMock
                .Setup(s => s.SearchByTitleAsync("The Stand", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new() { Asin = "B0OtherKing", Title = "Different King Book", Authors = new() { new() { Name = "Stephen King" } } }
                    },
                    TotalResults = 1
                });

            var service = CreateSearchService(audibleMock.Object);
            var results = await service.IntelligentSearchAsync("AUTHOR:Stephen King TITLE:The Stand");

            Assert.NotNull(results);
            // Two distinct ASINs from the merge; the exact-match collapse
            // did NOT trigger because the title-exact candidate has the
            // wrong author. The full merged list is returned so the user
            // can see both and disambiguate.
            Assert.Equal(2, results.Count);
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
