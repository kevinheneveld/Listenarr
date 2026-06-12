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
using Listenarr.Application.Audiobooks;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    /// <summary>
    /// Fixtures are real filename shapes from the live 773-file Heinlein
    /// collection record that motivated the Split workflow.
    /// </summary>
    public class FileClusteringTests
    {
        private const string Base = "/audiobooks/Robert A. Heinlein/Let There Be Light/Peter Coates";

        private static AudiobookFile F(int id, string rel) => new() { Id = id, Path = $"{Base}/{rel}" };

        [Fact]
        public void Cluster_TrackNumberedSiblings_FormOneCluster()
        {
            var clusters = FileClustering.Cluster(new[]
            {
                F(1, "(heinlein_robert)-friday-01_77.mp3"),
                F(2, "(heinlein_robert)-friday-02_77.mp3"),
                F(3, "(heinlein_robert)-friday-44_77.mp3"),
                F(4, "Robert A. Heinlein - Expanded Universe Part 01 of 26.mp3"),
                F(5, "Robert A. Heinlein - Expanded Universe Part 26 of 26.mp3"),
            }, Base);

            Assert.Equal(2, clusters.Count);
            Assert.Equal(3, clusters[0].Files.Count); // friday
            Assert.Equal(2, clusters[1].Files.Count); // expanded universe
        }

        [Fact]
        public void Cluster_SubdirectoryWins_OverFilenames()
        {
            var clusters = FileClustering.Cluster(new[]
            {
                F(1, "Future History/Revolt in 2100/01 Intro.mp3"),
                F(2, "Future History/Revolt in 2100/02 Introduction; The Innocent Eye.mp3"),
                F(3, "All You Zombies/Spider Robinson/All You Zombies.mp3"),
            }, Base);

            Assert.Equal(2, clusters.Count);
            var dirCluster = clusters.First(c => c.DisplayName == "Future History");
            Assert.Equal(2, dirCluster.Files.Count);
        }

        [Fact]
        public void Cluster_SingleFiles_StaySeparate()
        {
            var clusters = FileClustering.Cluster(new[]
            {
                F(1, "Robert A. Heinlein - The Moon Is a Harsh Mistress.mp3"),
                F(2, "Robert A. Heinlein - Stranger in a Strange Land.mp3"),
            }, Base);

            Assert.Equal(2, clusters.Count);
            Assert.All(clusters, c => Assert.Single(c.Files));
        }

        [Theory]
        [InlineData("(heinlein_robert)-friday-01_77", "(heinlein_robert)-friday")]
        [InlineData("Robert A. Heinlein - Expanded Universe Part 01 of 26", "Robert A. Heinlein - Expanded Universe")]
        [InlineData("TunnelintheSkyUnabridged_51", "TunnelintheSkyUnabridged")]
        [InlineData("1 The Year of the Jackpot", "The Year of the Jackpot")]
        [InlineData("HEINLEIN - Door 12_41", "HEINLEIN - Door")]
        [InlineData("Robert A. Heinlein - Starship Troopers Part 61 of 61", "Robert A. Heinlein - Starship Troopers")]
        public void CleanStem_StripsNumberingNoise(string input, string expected)
        {
            Assert.Equal(expected, FileClustering.CleanStem(input));
        }

        [Fact]
        public void CleanStem_NeverReturnsEmpty()
        {
            // A name that is nothing but numbering must fall back to itself.
            Assert.Equal("01_77", FileClustering.CleanStem("01_77"));
        }
    }

    public class SplitDestinationSuggesterTests
    {
        private static readonly List<(int, string)> Library = new()
        {
            (398, "Friday"),
            (421, "Tunnel in the Sky"),
            (395, "Expanded Universe, Vol. 1"),
            (1335, "Expanded Universe, Vol. 2"),
            (415, "The Moon Is a Harsh Mistress"),
        };

        [Fact]
        public void Suggest_PrefersLongestTitle()
        {
            // "Vol. 2" must beat the shorter "Expanded Universe, Vol. 1"… both
            // contain "expanded universe", but only Vol. 2's full title is in
            // the cluster name.
            Assert.Equal(1335, SplitDestinationSuggester.Suggest("Expanded Universe, Vol. 2", Library));
        }

        [Fact]
        public void Suggest_SpaceInsensitive()
        {
            Assert.Equal(421, SplitDestinationSuggester.Suggest("TunnelintheSkyUnabridged", Library));
        }

        [Fact]
        public void Suggest_NoContainment_ReturnsNull()
        {
            Assert.Null(SplitDestinationSuggester.Suggest("Robert A. Heinlein - 8 Books", Library));
        }

        [Fact]
        public void Suggest_FullSentenceTitle_Matches()
        {
            Assert.Equal(415, SplitDestinationSuggester.Suggest(
                "Robert A. Heinlein - The Moon Is a Harsh Mistress", Library));
        }
    }
}
