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
            // "This is Audible" names nothing, and neither do file names that
            // are one bulk-renamed stem: the opening file's name is shown, but
            // it is not a label a destination may be suggested from.
            Assert.Null(groups[0].Label);
            Assert.Equal("Collection-001", groups[0].DisplayName);
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
        public void BuildGroups_NoBoundaryEvidence_OnlyTheWholeBookFileSplitsOff()
        {
            // Without spoken evidence nothing splits — except the 11h file 12,
            // which is a complete book by its duration alone.
            var shapes = CollectionShapes();
            var probes = new List<SplitAudioProbePlanner.ProbeResult>
            {
                new(1, "Chapter one. Ordinary narration."),
                new(4, "More ordinary narration without any announcement."),
                new(12, null), // failed probe
            };

            var groups = SplitAudioProbePlanner.BuildGroups(shapes, probes);

            Assert.Equal(2, groups.Count);
            Assert.Equal(Enumerable.Range(1, 11).ToArray(), groups[0].FileIds);
            Assert.Equal(new[] { 12, 13, 14, 15 }, groups[1].FileIds);
        }

        [Fact]
        public void BuildGroups_WholeBookFilesWithoutAnnouncements_EachOpensItsOwnGroup()
        {
            // Live case (a Dark-series collection): 17 files of 4-7.6h each,
            // none opening with a retail announcement — they must not run
            // together, and the file names become the labels.
            var shapes = new List<SplitAudioProbePlanner.FileShape>
            {
                File(1, 5 * 60, 91, "Dark Dream.m4b"),
                File(2, 6 * 60, 147, "Dark Legend - Part 1.m4b"),
                File(3, 5.9 * 60, 148, "Dark Legend - Part 2.m4b"),
            };
            var probes = new List<SplitAudioProbePlanner.ProbeResult>
            {
                new(1, "The jaguar moved through the dense foliage."),
                new(2, "He woke buried beneath the earth."),
                new(3, "The storm raged over the Carpathian mountains."),
            };

            var groups = SplitAudioProbePlanner.BuildGroups(shapes, probes);

            Assert.Equal(3, groups.Count);
            Assert.Equal("Dark Dream", groups[0].Label);
            Assert.Equal("Dark Legend - Part 1", groups[1].Label);
            Assert.Equal("Dark Legend - Part 2", groups[2].Label);
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

        // ── Chunked series pack (live case: record 4676) ────────────────────
        // Four books imported onto one record as 1,408 uniformly named files:
        // each original part was chopped into equal chunks (~5 min) with one
        // short tail. Scaled down here; ids are 1-based positions.

        private static SplitAudioProbePlanner.FileShape ChunkAt(int id, double seconds, int kbps)
            => new(id, $"A Storm of Swords-{id:D4}.mp3", (long)(seconds * kbps * 1000 / 8), seconds);

        private static List<SplitAudioProbePlanner.FileShape> ChunkedPack()
        {
            var shapes = new List<SplitAudioProbePlanner.FileShape>();
            void Part(int chunks, double chunkSeconds, int kbps, double tailSeconds)
            {
                for (var i = 0; i < chunks; i++) shapes.Add(ChunkAt(shapes.Count + 1, chunkSeconds, kbps));
                shapes.Add(ChunkAt(shapes.Count + 1, tailSeconds, kbps));
            }

            // Book 1 at 128 kbps: two parts → files 1-11, 12-22. Chunks sit
            // right at the old absolute 300s "small file" limit.
            Part(10, 299.98, 128, 162);
            Part(10, 299.98, 128, 123);
            // Book 2 at 16 kbps: two parts with different chunk lengths →
            // files 23-33, 34-44. The second part's last chunk is nearly
            // full length — no short tail to give the seam away.
            Part(10, 307.2, 16, 143);
            Part(10, 368.6, 16, 365.8);
            // Book 3 at 16 kbps, one part → files 45-65.
            Part(20, 307.2, 16, 197);
            // Book 4 at 64 kbps: long chapter files → files 66-70.
            foreach (var seconds in new[] { 2343.0, 2350, 2401, 379, 1711 })
            {
                shapes.Add(ChunkAt(shapes.Count + 1, seconds, 64));
            }
            return shapes;
        }

        [Fact]
        public void PickProbeCandidates_ChunkedPack_ProbesSourceSeamsNotOrdinaryChunks()
        {
            var candidates = SplitAudioProbePlanner.PickProbeCandidates(ChunkedPack());
            var ids = candidates.Select(c => c.FileId).ToList();

            // The opening of every original source file — and nothing else.
            Assert.Equal(new[] { 1, 12, 23, 34, 45, 66 }, ids);
            // File 45 has no short tail before it and no encode change: only
            // the chunk length gives it away.
            Assert.Equal("chunk length change (new source file)", candidates.Single(c => c.FileId == 45).Reason);
        }

        [Fact]
        public void PickProbeCandidates_CapKeepsStructuralSeamsOverStubs()
        {
            // 40 stub-shaped files up front, one encode change at the very
            // end: the cap must not spend itself on the stubs and drop the
            // one boundary the shape actually proves.
            var shapes = new List<SplitAudioProbePlanner.FileShape>();
            for (var i = 0; i < 40; i++)
            {
                shapes.Add(File(shapes.Count + 1, 40, 18));
                shapes.Add(File(shapes.Count + 1, 2 + i * 0.01, 1));
            }
            shapes.Add(File(shapes.Count + 1, 40, 60)); // encode shift
            shapes.Add(File(shapes.Count + 1, 40, 60));

            var ids = SplitAudioProbePlanner.PickProbeCandidates(shapes).Select(c => c.FileId).ToList();

            Assert.Equal(SplitAudioProbePlanner.MaxProbeCandidates, ids.Count);
            Assert.Contains(81, ids);
        }

        [Fact]
        public void BuildGroups_PartsReannouncingTheSameTitle_StayOneBook()
        {
            // The real transcripts: every Audible part of book 1 re-announces
            // the title (in whisper's shifting spelling and punctuation), the
            // 16 kbps book-2 parts open on plain prose, and each new book is
            // announced once.
            var probes = new List<SplitAudioProbePlanner.ProbeResult>
            {
                new(1, "this is audible looks on tape presents a game of thrones book one of a song of ice and fire by george r r martin read by roy de tress prologue we should start back"),
                new(12, "This is Audible. Booksante presents \"The Game of Thrones\" Book 1 of \"A Song of Ice and Fire\" by George R. R. Martin, read by Roy de Tries. Arya."),
                new(23, "This is audible. Books on tape presents A Clash of Kings. Book tool of a song of ice and fire by George R. R. Martin, read by Roy Dottreece. Prologue."),
                new(34, "Sir Roderick commanded the man to set aside a fifth, and questioned the steward closely."),
                new(45, "This is Audible. Books on tape presents a storm of swords, book free of a song of ice and fire, by George R. R. Martin, read by Roy DeTrice. Prolog"),
                new(66, "Random House Audio presents A Feast for Crows, Book 4 of A Song of Ice and Fire, by George R. R. Martin, read for you by John Lee."),
            };

            var groups = SplitAudioProbePlanner.BuildGroups(ChunkedPack(), probes);

            Assert.Equal(4, groups.Count);
            Assert.Equal(Enumerable.Range(1, 22), groups[0].FileIds);
            Assert.Equal(Enumerable.Range(23, 22), groups[1].FileIds);
            Assert.Equal(Enumerable.Range(45, 21), groups[2].FileIds);
            Assert.Equal(Enumerable.Range(66, 5), groups[3].FileIds);

            Assert.Equal("a game of thrones book one of a song of ice and fire", groups[0].Label);
            Assert.Equal("A Clash of Kings", groups[1].Label);
            Assert.Equal("a storm of swords", groups[2].Label);
            Assert.Equal("A Feast for Crows", groups[3].Label);
        }

        [Fact]
        public void BuildGroups_UniformNames_UnannouncedGroupGetsNoLabel()
        {
            // Bulk-renamed to one stem, a file name is the SOURCE record's
            // title: labeling an unannounced group with it would suggest a
            // same-titled duplicate record as its destination.
            var shapes = ChunkedPack();
            var probes = new List<SplitAudioProbePlanner.ProbeResult>
            {
                new(1, "This is Audible. Chapter one, in ordinary prose."),
                new(23, "This is Audible. More prose, still no title."),
            };

            var groups = SplitAudioProbePlanner.BuildGroups(shapes, probes);

            Assert.Equal(2, groups.Count);
            Assert.All(groups, g => Assert.Null(g.Label));
            Assert.Equal("A Storm of Swords-0023", groups[1].DisplayName);
        }

        [Theory]
        [InlineData("a game of thrones book one of a song of ice and fire", "The Game of Thrones", true)]
        [InlineData("A Game of Thrones", "The Game of Thrones", true)]
        [InlineData("A Clash of Kings", "a storm of swords", false)]
        [InlineData("Dune", "Dune Messiah", false)]
        [InlineData("The Book Thief", "The Book of Lost Things", false)]
        public void SameWork_IgnoresArticlesAndSeriesPlacement(string a, string b, bool expected)
        {
            Assert.Equal(expected, SplitAudioProbePlanner.SameWork(a, b));
        }

        [Theory]
        [InlineData("Penguin Audio presents Escaping Home by A. American Red by Duke Fontaine", "Escaping Home")]
        [InlineData("Penguin Audio presents \"For Saking Home\" by A. American, read for you by Duke Fontaine.", "For Saking Home")]
        [InlineData("Podium Publishing Presents, Charlie's Requiem, A Going Home Novella, written by A. American", "Charlie's Requiem")]
        [InlineData("Recording Books Romance presents an unabridged recording of Dark Lover by J.R. Ward", "Dark Lover")]
        [InlineData("Booksante presents \"The Game of Thrones\" Book 1 of \"A Song of Ice and Fire\" by George R. R. Martin", "The Game of Thrones")]
        [InlineData("This is Audible. This had been a good week.", null)]
        [InlineData("Chapter one. It was a bright cold day.", null)]
        public void TryExtractTitle_ParsesAnnouncements(string transcript, string? expected)
        {
            Assert.Equal(expected, SplitAudioProbePlanner.TryExtractTitle(transcript));
        }
    }
}
