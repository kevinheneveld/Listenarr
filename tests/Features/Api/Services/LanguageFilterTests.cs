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
using Listenarr.Api.Services.Scoring;

namespace Listenarr.Tests.Features.Api.Services
{
    public class LanguageFilterTests
    {
        // ── DetectLanguage ───────────────────────────────────────────────────

        [Theory]
        [InlineData("Michael Crichton Micro [Audiobook PL] [mp3@64] [POLISH]", "Polish")]
        [InlineData("Michael Crichton - Timeline Rejsen Til Fortiden-AUDiOBOOK-WEB-DK-2016-CRAViNGS", "Danish")]
        [InlineData("El bazar de los malos sueños - Stephen King [Audiolibro]", "Spanish")]
        [InlineData("Der Schwarm - Frank Schätzing (Hörbuch)", "German")]
        [InlineData("Harry Potter - tome 1 [FR] livre audio", "French")]
        public void DetectLanguage_FlagsExplicitForeignMarkers(string title, string expected)
        {
            Assert.Equal(expected, LanguageFilter.DetectLanguage(title));
        }

        [Theory]
        // An explicit English marker — including foreign words for "English" —
        // resolves to "English", even amid foreign text (foreign-tracker listings).
        // "[Английский]" literally means "[English]": the audiobook IS English.
        [InlineData("[Английский] Child Lee / Чайлд Ли - No Plan B (Jack Reacher 27) [Scott Brick, 2022, MP3]")]
        [InlineData("Lee Child - Jack Reacher 10 - The Hard Way [English] m4b")]
        [InlineData("Michael Crichton - Prey [ENG] m4b")]
        public void DetectLanguage_ResolvesExplicitEnglishMarkers(string title)
        {
            Assert.Equal("English", LanguageFilter.DetectLanguage(title));
        }

        [Theory]
        // No confident marker at all → null (unknown). The scorer leaves these
        // alone rather than guessing.
        [InlineData("Michael Crichton - Eaters of the Dead [audio]")]
        [InlineData("David Copperfield by Charles Dickens, narrated by Richard Armitage")]
        // Language NAMES appearing inside legitimate English titles must NOT
        // be flagged — these are the false positives the retroactive audit caught.
        [InlineData("The Russian - James Patterson")]
        [InlineData("French Kiss - Author Name [m4b]")]
        [InlineData("French Twist")]
        [InlineData("10 Masterpieces of Ancient Greek Literature")]
        public void DetectLanguage_ReturnsNullForUntaggedTitles(string title)
        {
            Assert.Null(LanguageFilter.DetectLanguage(title));
        }

        [Theory]
        // Language names DO count when bracketed or in an "X Edition" construction.
        [InlineData("Cujo (Spanish Edition) - Stephen King", "Spanish")]
        [InlineData("The Other Emily (German edition)", "German")]
        [InlineData("En bøn til Odd (Danish Edition) - Dean Koontz", "Danish")]
        [InlineData("Some Title [Spanish] m4b", "Spanish")]
        public void DetectLanguage_FlagsBracketedAndEditionConstructions(string title, string expected)
        {
            Assert.Equal(expected, LanguageFilter.DetectLanguage(title));
        }

        [Fact]
        public void DetectLanguage_IgnoresBareUndelimitedCodes()
        {
            // "it" / "de" appear constantly in English titles — must not match
            // unless delimited like [it] or -de-.
            Assert.Null(LanguageFilter.DetectLanguage("It Ends with Us - Colleen Hoover"));
            Assert.Null(LanguageFilter.DetectLanguage("The Order of the Phoenix"));
        }

        // ── ShouldReject ─────────────────────────────────────────────────────

        [Fact]
        public void ShouldReject_RejectsForeignWhenProfileIsEnglishOnly()
        {
            var prefs = new List<string> { "English" };
            Assert.True(LanguageFilter.ShouldReject(
                "Michael Crichton Micro [POLISH]", prefs, out var detected));
            Assert.Equal("Polish", detected);
        }

        [Fact]
        public void ShouldReject_KeepsForeignWhenProfileWantsThatLanguage()
        {
            var prefs = new List<string> { "English", "Spanish" };
            Assert.False(LanguageFilter.ShouldReject(
                "El bazar de los malos sueños [Audiolibro]", prefs, out _));
        }

        [Fact]
        public void ShouldReject_KeepsEnglishContent()
        {
            var prefs = new List<string> { "English" };
            Assert.False(LanguageFilter.ShouldReject(
                "Lee Child - The Hard Way [English] m4b", prefs, out _));
        }

        [Fact]
        public void ShouldReject_NonEnglishProfile_KeepsThatLanguageAndRejectsOthers()
        {
            // Profile prefers Spanish only: keep Spanish, reject English and German.
            var prefs = new List<string> { "Spanish" };
            Assert.False(LanguageFilter.ShouldReject(
                "El bazar de los malos sueños [Audiolibro]", prefs, out _));

            Assert.True(LanguageFilter.ShouldReject(
                "Lee Child - The Hard Way [English] m4b", prefs, out var d1));
            Assert.Equal("English", d1);

            Assert.True(LanguageFilter.ShouldReject(
                "Der Schwarm - Frank Schätzing (Hörbuch)", prefs, out var d2));
            Assert.Equal("German", d2);
        }

        [Fact]
        public void ShouldReject_UntaggedTitle_IsAlwaysKept()
        {
            // No marker → null → fail open, regardless of profile.
            Assert.False(LanguageFilter.ShouldReject(
                "David Copperfield by Charles Dickens", new List<string> { "English" }, out _));
            Assert.False(LanguageFilter.ShouldReject(
                "David Copperfield by Charles Dickens", new List<string> { "Spanish" }, out _));
        }

        [Fact]
        public void ShouldReject_NoPreferenceConfigured_KeepsEverything()
        {
            Assert.False(LanguageFilter.ShouldReject(
                "Michael Crichton Micro [POLISH]", new List<string>(), out _));
            Assert.False(LanguageFilter.ShouldReject(
                "Michael Crichton Micro [POLISH]", null, out _));
        }
    }
}
