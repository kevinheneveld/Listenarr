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
    [Trait("Name", "RecordingEditionClassifierTests")]
    public class RecordingEditionClassifierTests
    {
        private sealed record Entry(string Title, string? Subtitle, string[] Narrators, string? Publisher, string? Date);

        private static List<RecordingRun<Entry>> Group(params Entry[] entries) =>
            RecordingEditionClassifier.GroupIntoRuns(
                entries,
                e => RecordingEditionClassifier.Classify(e.Title, e.Subtitle, e.Narrators, e.Publisher, e.Date));

        [Fact]
        public void BrayTrilogy_IsOneRun()
        {
            var runs = Group(
                new Entry("Book One", null, new[] { "R.C. Bray" }, "Podium", "2014-01-01"),
                new Entry("Book Two", null, new[] { "R.C. Bray" }, "Podium", "2015-01-01"),
                new Entry("Book Three", null, new[] { "R.C. Bray" }, "Podium", "2016-01-01"));

            var run = Assert.Single(runs);
            Assert.Equal(RecordingEditionKind.Standard, run.Kind);
            Assert.Equal(3, run.Entries.Count);
            Assert.Contains("R.C. Bray", run.Label);
        }

        [Fact]
        public void FullCastVersions_OfSameWorks_AreASeparateRun()
        {
            var runs = Group(
                new Entry("Book One", null, new[] { "R.C. Bray" }, "Podium", "2014-01-01"),
                new Entry("Book Two", null, new[] { "R.C. Bray" }, "Podium", "2015-01-01"),
                new Entry("Book One", "A Full Cast Dramatization", new[] { "Full Cast" }, "Theater Works", "2018-01-01"),
                new Entry("Book Two", "A Full Cast Dramatization", new[] { "Full Cast" }, "Theater Works", "2018-06-01"));

            Assert.Equal(2, runs.Count);
            Assert.Contains(runs, r => r.Kind == RecordingEditionKind.Standard);
            Assert.Contains(runs, r => r.Kind == RecordingEditionKind.Dramatized);
        }

        [Fact]
        public void NarratorSwitch_MidSeries_SamePublisherAndEra_IsSameRun()
        {
            // Kevin's explicit case: series switch narrators legitimately.
            var runs = Group(
                new Entry("Book One", null, new[] { "Narrator A" }, "Audible Studios", "2014-01-01"),
                new Entry("Book Two", null, new[] { "Narrator A" }, "Audible Studios", "2015-01-01"),
                new Entry("Book Three", null, new[] { "Narrator B" }, "Audible Studios", "2016-06-01"));

            var run = Assert.Single(runs);
            Assert.Equal(3, run.Entries.Count);
            Assert.Contains("Narrator A", run.Narrators);
            Assert.Contains("Narrator B", run.Narrators);
        }

        [Fact]
        public void NarratorSwitch_DifferentPublisher_StaysSeparate()
        {
            var runs = Group(
                new Entry("Book One", null, new[] { "Narrator A" }, "Podium", "2014-01-01"),
                new Entry("Book One", null, new[] { "Narrator B" }, "Recorded Books", "2014-06-01"));

            Assert.Equal(2, runs.Count);
        }

        [Fact]
        public void NarratorSwitch_DistantEra_StaysSeparate()
        {
            // Same publisher re-recording a decade later is a new production run.
            var runs = Group(
                new Entry("Book One", null, new[] { "Narrator A" }, "Audible Studios", "2005-01-01"),
                new Entry("Book One", null, new[] { "Narrator B" }, "Audible Studios", "2019-01-01"));

            Assert.Equal(2, runs.Count);
        }

        [Fact]
        public void GraphicAudio_IsItsOwnKind()
        {
            var runs = Group(
                new Entry("Book One", "GraphicAudio Edition", new[] { "Cast" }, "GraphicAudio", "2016-01-01"),
                new Entry("Book One", null, new[] { "R.C. Bray" }, "Podium", "2014-01-01"));

            Assert.Equal(2, runs.Count);
            Assert.Contains(runs, r => r.Kind == RecordingEditionKind.GraphicAudio);
            Assert.Contains(runs, r => r.Label.Contains("GraphicAudio"));
        }

        [Fact]
        public void Abridged_SeparatesFromUnabridged()
        {
            var runs = Group(
                new Entry("Book One", "Unabridged", new[] { "Narrator A" }, "Pub", "2014-01-01"),
                new Entry("Book One", "Abridged", new[] { "Narrator A" }, "Pub", "2014-01-01"));

            Assert.Equal(2, runs.Count);
            Assert.Contains(runs, r => r.Kind == RecordingEditionKind.Abridged);
            Assert.Contains(runs, r => r.Kind == RecordingEditionKind.Standard);
        }

        [Fact]
        public void MissingDates_NeverMergeAcrossNarrators()
        {
            // Conservative: without era evidence, different narrator sets stay apart.
            var runs = Group(
                new Entry("Book One", null, new[] { "Narrator A" }, "Pub", null),
                new Entry("Book Two", null, new[] { "Narrator B" }, "Pub", null));

            Assert.Equal(2, runs.Count);
        }
    }
}
