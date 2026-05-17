using Listenarr.Application.Search;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class TitleMatcherTests
    {
        // The exact repro from live: searching "1634 - The Baltic War" against
        // Audible's "1634: The Baltic War" used to return zero matches because
        // the naive IndexOf substring check couldn't bridge the hyphen/colon
        // difference. All of the following must now match.
        [Theory]
        [InlineData("1634 - The Baltic War", "1634: The Baltic War")]    // hyphen -> colon (the repro)
        [InlineData("1634: The Baltic War", "1634 - The Baltic War")]    // reverse
        [InlineData("1634 – The Baltic War", "1634: The Baltic War")] // en-dash
        [InlineData("1634 — The Baltic War", "1634: The Baltic War")] // em-dash
        [InlineData("1634  The  Baltic  War", "1634: The Baltic War")]   // doubled whitespace
        [InlineData("1634, The Baltic War.", "1634: The Baltic War")]    // commas / trailing period
        [InlineData("1634 “Baltic War”", "1634: The Baltic War")] // smart quotes
        [InlineData("the BALTIC war", "1634: The Baltic War")]            // case + leading token missing
        public void Matches_PunctuationVariants_AcceptedForBalticWar(string query, string audibleTitle)
        {
            Assert.True(
                TitleMatcher.Matches(audibleTitle, subtitle: null, query: query),
                $"Expected query '{query}' to match Audible title '{audibleTitle}'");
        }

        [Fact]
        public void Matches_FindsQueryInSubtitleField()
        {
            Assert.True(TitleMatcher.Matches(
                title: "1634",
                subtitle: "The Baltic War",
                query: "Baltic - War"));
        }

        [Fact]
        public void Matches_RejectsUnrelatedTitleSharingNoSignificantTokens()
        {
            Assert.False(TitleMatcher.Matches(
                title: "1632: A Novel of Alternate History",
                subtitle: null,
                query: "1634 - The Baltic War"));
        }

        [Fact]
        public void Matches_SingleTokenQueryDoesNotFallBackToTokenOverlap()
        {
            // A bare "1634" query should still match "1634: The Baltic War"
            // via the normalized substring check (the lead token is present).
            Assert.True(TitleMatcher.Matches("1634: The Baltic War", null, "1634"));
            // But it must NOT match an unrelated title that just happens to
            // share no tokens — single-token queries skip the overlap fallback.
            Assert.False(TitleMatcher.Matches("Pride and Prejudice", null, "1634"));
        }

        [Fact]
        public void Matches_TokenOverlapFallbackAcceptsReorderedWords()
        {
            // Query words appear in the candidate, just reordered / with extra noise.
            Assert.True(TitleMatcher.Matches(
                title: "The Baltic War (1634 Series, Book 3)",
                subtitle: null,
                query: "1634 Baltic War"));
        }

        [Fact]
        public void Matches_EmptyQueryReturnsTrue()
        {
            // Empty/whitespace query means "no title filter" — don't drop everything.
            Assert.True(TitleMatcher.Matches("Anything", null, ""));
            Assert.True(TitleMatcher.Matches("Anything", null, "   "));
            Assert.True(TitleMatcher.Matches("Anything", null, null));
        }

        [Theory]
        [InlineData("1634 - The Baltic War", "1634 the baltic war")]
        [InlineData("DICKENS, charles -- david-copperfield.m4b", "dickens charles david copperfield m4b")]
        [InlineData("  multiple   spaces  ", "multiple spaces")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void Normalize_ProducesExpectedCanonicalForm(string? input, string expected)
        {
            Assert.Equal(expected, TitleMatcher.Normalize(input));
        }
    }
}
