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
using Listenarr.Domain.Common;
using Xunit;

namespace Listenarr.Tests.Features.Domain.Common
{
    public class PhoneticsTests
    {
        [Theory]
        // Classic STT confusions on proper nouns
        [InlineData("weir", "ware")]
        [InlineData("smith", "smyth")]
        [InlineData("porter", "porder")]
        [InlineData("kowal", "cowell")]
        public void SoundAlike_SttConfusablePairs_AreEqual(string a, string b)
        {
            Assert.True(Phonetics.SoundAlike(a, b), $"'{a}' vs '{b}' should sound alike");
        }

        [Theory]
        [InlineData("weir", "sanderson")]
        [InlineData("porter", "gaiman")]
        public void SoundAlike_UnrelatedNames_AreNot(string a, string b)
        {
            Assert.False(Phonetics.SoundAlike(a, b));
        }

        [Fact]
        public void Key_EmptyOrNonAlpha_IsEmpty()
        {
            Assert.Equal(string.Empty, Phonetics.Key(null));
            Assert.Equal(string.Empty, Phonetics.Key("   "));
            Assert.Equal(string.Empty, Phonetics.Key("123"));
        }

        [Fact]
        public void SoundAlike_EmptyKeys_NeverMatch()
        {
            // Two empty keys must not be treated as "sounding alike".
            Assert.False(Phonetics.SoundAlike("123", "456"));
        }

        [Fact]
        public void Key_LongerNamesKeepDistinguishingConsonants()
        {
            // Full-length keys (no Soundex 4-char truncation): "sanderson" and
            // "sanders" must differ.
            Assert.NotEqual(Phonetics.Key("sanderson"), Phonetics.Key("sanders"));
        }
    }
}
