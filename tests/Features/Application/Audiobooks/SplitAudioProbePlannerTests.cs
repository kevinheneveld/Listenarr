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

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    /// <summary>
    /// Fixtures are the live case that shaped the planner: record 1023, a
    /// whole 8-book series (83.8h, 158 files) renamed to "Going Home-NNN.mp3"
    /// — names useless, but boundaries visible as small stub files, encode
    /// shifts, and whole-book-length files, then confirmed by whisper probes.
    /// </summary>
    public class SplitAudioProbePlannerTests
    {
        private static SplitAudioProbePlanner.FileShape File(int id, double minutes, double mb, string? name = null)
            => new(id, name ?? $"Collection-{id:D3}.mp3", (long)(mb * 1024 * 1024), minutes * 60);

        /// <summary>
        /// A compressed model of record 1023: [book1: 3 big files][small
        /// intro][book2: 2 files][small epilogue][announced book3: 2 files]
        /// [encode-shift book4: 2 files at half rate][whole-book file]
        /// [small novella intro][novella: 2 files].
        /// Ids are 1-based positions for readability.
        /// </summary>
        private static List<SplitAudioProbePlanner.FileShape> CollectionShapes() => new()
        {
            File(1, 60, 27), File(2, 55, 25), File(3, 50, 23),          // book 1 (63 kbps-ish)
            File(4, 3.8, 1.8),                                          // small intro stub
            File(5, 45, 20), File(6, 40, 18),                           // book 2
            File(7, 3.1, 1.5),                                          // small epilogue stub
            File(8, 40, 18), File(9, 35, 16),                           // book 3 (announced)
            File(10, 40, 9), File(11, 35, 8),                           // book 4 — encode shift (half rate)
            File(12, 660, 300),                                         // whole-book single file
            File(13, 1.6, 1.4),                                         // novella intro stub
            File(14, 15, 12), File(15, 12, 10),                         // novella files
        };

        [Fact]
        public void PickProbeCandidates_FindsStubsShiftsAndWholeBooks()
        {
            var candidates = SplitAudioProbePlanner.PickProbeCandidates(CollectionShapes());
            var ids = candidates.Select(c => c.FileId).ToList();

            Assert.Contains(1, ids);   // first file, always
            Assert.Contains(4, ids);   // small stub
            Assert.Contains(5, ids);   // follows a stub
            Assert.Contains(7, ids);   // epilogue stub
            Assert.Contains(8, ids);   // follows a stub
            Assert.Contains(10, ids);  // encode shift (27MB/60min run → 9MB/40min)
            Assert.Contains(12, ids);  // whole-book-length file
            Assert.Contains(13, ids);  // follows whole-book file (also a stub)
            Assert.Contains(14, ids);  // follows a stub

            // Ordinary mid-book chapters are NOT probed — that's the point.
            Assert.DoesNotContain(2, ids);
            Assert.DoesNotContain(6, ids);
            Assert.DoesNotContain(9, ids);
            Assert.DoesNotContain(15, ids);
        }

        [Fact]
        public void PickProbeCandidates_UniformAudiobook_ProbesOnlyFirstFile()
        {
            // A normal chaptered book: same encode, no stubs — nothing to
            // probe beyond the opening file.
            var uniform = Enumerable.Range(1, 20)
                .Select(i => File(i, 25, 11))
                .ToList();

            var candidates = SplitAudioProbePlanner.PickProbeCandidates(uniform);

            var only = Assert.Single(candidates);
            Assert.Equal(1, only.FileId);
        }

        [Fact]
        public void PickProbeCandidates_IsCapped()
        {
            // 200 tiny files (a music-album dump) must not schedule 200
            // whisper runs.
            var many = Enumerable.Range(1, 200).Select(i => File(i, 3, 1.4)).ToList();

            var candidates = SplitAudioProbePlanner.PickProbeCandidates(many);

            Assert.True(candidates.Count <= SplitAudioProbePlanner.MaxProbeCandidates);
        }

        [Fact]
        public void PickProbeCandidates_UnknownDurations_FallBackToSize()
        {
            var shapes = new List<SplitAudioProbePlanner.FileShape>
            {
                new(1, "a.mp3", 30 * 1024 * 1024, null),
                new(2, "b.mp3", 1 * 1024 * 1024, null),   // small by size
                new(3, "c.mp3", 30 * 1024 * 1024, null),
            };

            var ids = SplitAudioProbePlanner.PickProbeCandidates(shapes).Select(c => c.FileId).ToList();

            Assert.Contains(2, ids);
            Assert.Contains(3, ids); // follows the small file
        }

        [Fact]
        public void BuildGroups_LiveTranscripts_ProduceLabeledBoundaries()
        {
            // The real probe transcripts (abbreviated) from the live split.
            var probes = new List<SplitAudioProbePlanner.ProbeResult>
            {
                new(1, "This is Audible. This had been a good week. I worked from home all week."),
                new(4, "This is Audible. When it all went wrong, I was 200 miles from home."),
                new(5, "Pretty soon, I knew it wasn't just me. Everyone in the road was stuck."),
                new(7, "\"Epilogue.\" Jess grounded the tip of her shovel in the dirt."),
                new(8, "Penguin Audio presents Escaping Home by A. American Red by Duke Fontaine. Prolog. It took weeks to walk home."),
                new(10, "Immersed in total darkness, deprived of human contact, chained and afraid."),
                new(12, "This is Audible. The addition of Miss Kay to our group was profound."),
                new(13, "Podium Publishing Presents, Charlie's Requiem, A Going Home Novella, written by A. American and Walt Browning."),
                new(14, "Prolog by Angry American. Going Home set a high bar."),
            };

            var groups = SplitAudioProbePlanner.BuildGroups(CollectionShapes(), probes);

            // Expected boundaries: 1 (start), 4 (retail opener), 8 (announced,
            // after the epilogue at 7), 12 (retail opener), 13 (announced).
            // File 10's probe is plain prose → NO boundary: the encode shift
            // alone must not split without spoken evidence.
            Assert.Equal(5, groups.Count);
            Assert.Equal(new[] { 1, 2, 3 }, groups[0].FileIds);
            Assert.Equal(new[] { 4, 5, 6, 7 }, groups[1].FileIds);
            Assert.Equal(new[] { 8, 9, 10, 11 }, groups[2].FileIds);
            Assert.Equal(new[] { 12 }, groups[3].FileIds);
            Assert.Equal(new[] { 13, 14, 15 }, groups[4].FileIds);

            Assert.Equal("Escaping Home", groups[2].Label);
            Assert.Equal("Charlie's Requiem", groups[4].Label);
            Assert.Null(groups[0].Label); // "This is Audible" names nothing
            Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.DisplayName)));
        }

        [Fact]
        public void BuildGroups_EpilogueOnly_OpensGroupAfterIt()
        {
            var shapes = new List<SplitAudioProbePlanner.FileShape>
            {
                File(1, 60, 27), File(2, 55, 25),
                File(3, 4, 1.8),                    // spoken epilogue
                File(4, 50, 23), File(5, 45, 20),   // next book, no announcement heard
            };
            var probes = new List<SplitAudioProbePlanner.ProbeResult>
            {
                new(1, "Chapter one. It was a bright cold day in April."),
                new(3, "Epilogue. The war was over at last."),
                new(4, "The rain had not stopped for three days."),
            };

            var groups = SplitAudioProbePlanner.BuildGroups(shapes, probes);

            Assert.Equal(2, groups.Count);
            Assert.Equal(new[] { 1, 2, 3 }, groups[0].FileIds);
            Assert.Equal(new[] { 4, 5 }, groups[1].FileIds);
        }

        [Fact]
        public void BuildGroups_NoBoundaryEvidence_SingleGroup()
        {
            var shapes = CollectionShapes();
            var probes = new List<SplitAudioProbePlanner.ProbeResult>
            {
                new(1, "Chapter one. Ordinary narration."),
                new(4, "More ordinary narration without any announcement."),
                new(12, null), // failed probe
            };

            var groups = SplitAudioProbePlanner.BuildGroups(shapes, probes);

            var single = Assert.Single(groups);
            Assert.Equal(shapes.Count, single.FileIds.Count);
        }

        [Fact]
        public void BuildGroups_AuthorsNotes_DoNotOpenABogusTailGroup()
        {
            // Live case: files 157-158 were the novella's author's notes and a
            // bonus chapter — "Authors' Notes by Walt Browning" must not open
            // a new group ("by" alone is not an announcement... it is matched
            // by the announcement regex only via "written by"/"read by" etc.).
            var shapes = new List<SplitAudioProbePlanner.FileShape>
            {
                File(1, 60, 27), File(2, 55, 25),
                File(3, 2.4, 2.2),
                File(4, 6.7, 6.1),
            };
            var probes = new List<SplitAudioProbePlanner.ProbeResult>
            {
                new(1, "Podium Publishing presents Charlie's Requiem by A. American."),
                new(3, "Authors' Notes by Walt Browning. If the response to this novella is positive."),
                new(4, "The story of a young retired marine who was drawn back to Iraq."),
            };

            var groups = SplitAudioProbePlanner.BuildGroups(shapes, probes);

            var single = Assert.Single(groups);
            Assert.Equal(new[] { 1, 2, 3, 4 }, single.FileIds);
            Assert.Equal("Charlie's Requiem", single.Label);
        }

        [Theory]
        [InlineData("Penguin Audio presents Escaping Home by A. American Red by Duke Fontaine", "Escaping Home")]
        [InlineData("Penguin Audio presents \"For Saking Home\" by A. American, read for you by Duke Fontaine.", "For Saking Home")]
        [InlineData("Podium Publishing Presents, Charlie's Requiem, A Going Home Novella, written by A. American", "Charlie's Requiem")]
        [InlineData("Recording Books Romance presents an unabridged recording of Dark Lover by J.R. Ward", "Dark Lover")]
        [InlineData("This is Audible. This had been a good week.", null)]
        [InlineData("Chapter one. It was a bright cold day.", null)]
        public void TryExtractTitle_ParsesAnnouncements(string transcript, string? expected)
        {
            Assert.Equal(expected, SplitAudioProbePlanner.TryExtractTitle(transcript));
        }
    }
}
