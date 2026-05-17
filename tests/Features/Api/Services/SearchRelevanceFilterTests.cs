using Listenarr.Application.Search.Filters;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "SearchRelevanceFilterTests")]
    [Trait("Category", "Search")]
    public class SearchRelevanceFilterTests
    {
        [Fact]
        public void ComputeRelevance_PattersonHoodConcert_VsJamesPattersonBook_IsBelowThreshold()
        {
            // Real bug report: a Patterson Hood concert recording matched James Patterson's
            // "Dog Diaries" on the single shared "Patterson" surname.
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "Patterson Hood - Live at Largo (2007) [FLAC]",
                audiobookTitle: "Dog Diaries: A Middle School Story",
                authors: new[] { "James Patterson", "Steven Butler" });

            Assert.True(relevance < RelevanceFilter.DefaultMinRelevance,
                $"Relevance {relevance:P0} should be below the {RelevanceFilter.DefaultMinRelevance:P0} threshold.");
        }

        [Fact]
        public void ComputeRelevance_DavidCopperfield_TitleFirstNaming_PassesThreshold()
        {
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "David Copperfield - Charles Dickens (Unabridged) [MP3]",
                audiobookTitle: "David Copperfield",
                authors: new[] { "Charles Dickens" });

            Assert.True(relevance >= RelevanceFilter.DefaultMinRelevance);
        }

        [Fact]
        public void ComputeRelevance_DavidCopperfield_AuthorFirstNaming_PassesThreshold()
        {
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "Charles Dickens - David Copperfield (Audiobook, 2020)",
                audiobookTitle: "David Copperfield",
                authors: new[] { "Charles Dickens" });

            Assert.True(relevance >= RelevanceFilter.DefaultMinRelevance);
        }

        [Fact]
        public void ComputeRelevance_PunctuationAndCaseInsensitive()
        {
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "MISTBORN: THE FINAL EMPIRE — Brandon Sanderson",
                audiobookTitle: "Mistborn - The Final Empire",
                authors: new[] { "Brandon Sanderson" });

            Assert.Equal(1.0, relevance);
        }

        [Fact]
        public void ComputeRelevance_NoSignificantAudiobookTokens_FailsOpen()
        {
            // Stop-word-only audiobook side returns 1.0 — cannot judge, do not reject.
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "Some Random Result",
                audiobookTitle: "The And",
                authors: null);

            Assert.Equal(1.0, relevance);
        }

        [Fact]
        public void ShouldFilter_NoAudiobookContext_FailsOpen()
        {
            var filter = new RelevanceFilter();
            var result = new SearchResult { Title = "Something Unrelated" };

            // Both overloads must return false without context — manual search must
            // keep showing everything.
            Assert.False(filter.ShouldFilter(result));
            Assert.False(filter.ShouldFilter(result, audiobook: null));
        }

        [Fact]
        public void ShouldFilter_BelowThreshold_RejectsResult()
        {
            var filter = new RelevanceFilter();
            var result = new SearchResult { Title = "Patterson Hood - Live at Largo" };
            var ab = new Audiobook { Title = "Dog Diaries", Authors = new List<string> { "James Patterson" } };

            Assert.True(filter.ShouldFilter(result, ab));
        }

        [Fact]
        public void ShouldFilter_AboveThreshold_KeepsResult()
        {
            var filter = new RelevanceFilter();
            var result = new SearchResult { Title = "Charles Dickens - David Copperfield (Audiobook)" };
            var ab = new Audiobook { Title = "David Copperfield", Authors = new List<string> { "Charles Dickens" } };

            Assert.False(filter.ShouldFilter(result, ab));
        }

        [Fact]
        public void SignificantTokens_DropsStopWordsAndShortTokens()
        {
            var tokens = SignificantTokens.From("The Lord of the Rings: The Fellowship of a Book").ToList();

            // "the", "of", "a", "book" are stop words; "lord", "rings", "fellowship" survive.
            Assert.DoesNotContain("the", tokens);
            Assert.DoesNotContain("of", tokens);
            Assert.DoesNotContain("a", tokens);
            Assert.DoesNotContain("book", tokens);
            Assert.Contains("lord", tokens);
            Assert.Contains("rings", tokens);
            Assert.Contains("fellowship", tokens);
        }
    }
}
