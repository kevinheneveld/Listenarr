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
using System.Text.Json;
using Listenarr.Application.Audiobooks.Verification;
using Listenarr.Infrastructure.HostedServices.Verification;

namespace Listenarr.Tests.Features.Application.Audiobooks.Verification
{
    public class LibraryVerificationRulesTests
    {
        private static Audiobook BookWithStatus(VerificationStatus status) => new()
        {
            Id = 1,
            Title = "Some Book",
            VerificationStatus = status
        };

        [Theory]
        // Manual states are STICKY: no agent pass — batch or explicit — may touch them.
        [InlineData(VerificationStatus.ManuallyVerified, false, false)]
        [InlineData(VerificationStatus.ManuallyVerified, true, false)]
        [InlineData(VerificationStatus.Rejected, false, false)]
        [InlineData(VerificationStatus.Rejected, true, false)]
        // Batch walks are IDEMPOTENT: agent-judged books are skipped...
        [InlineData(VerificationStatus.AgentVerified, false, false)]
        [InlineData(VerificationStatus.AgentFlagged, false, false)]
        // ...but an explicit per-book request re-verifies agent states.
        [InlineData(VerificationStatus.AgentVerified, true, true)]
        [InlineData(VerificationStatus.AgentFlagged, true, true)]
        // Unverified is always fair game.
        [InlineData(VerificationStatus.Unverified, false, true)]
        [InlineData(VerificationStatus.Unverified, true, true)]
        public void ShouldVerify_EnforcesStickyAndIdempotencyRules(VerificationStatus status, bool explicitRequest, bool expected)
        {
            var book = BookWithStatus(status);

            Assert.Equal(expected, LibraryVerificationBackgroundService.ShouldVerify(book, explicitRequest));
        }

        [Theory]
        [InlineData(VerificationOutcome.Match, VerificationStatus.AgentVerified)]
        [InlineData(VerificationOutcome.Mismatch, VerificationStatus.AgentFlagged)]
        [InlineData(VerificationOutcome.Uncertain, VerificationStatus.AgentFlagged)]
        public void StatusFor_MapsOutcomeOntoPersistedStatus(VerificationOutcome outcome, VerificationStatus expected)
        {
            Assert.Equal(expected, LibraryVerificationBackgroundService.StatusFor(outcome));
        }

        [Fact]
        public void ApplyVerdict_StampsAuditFieldsAndDetailJson()
        {
            var book = BookWithStatus(VerificationStatus.Unverified);
            var verdict = new VerificationVerdict
            {
                Outcome = VerificationOutcome.Mismatch,
                Confidence = 0.88,
                Method = "deterministic:whisper-base.en",
                TitleMatch = new VerificationFieldMatch(0.1, null),
                AuthorMatch = new VerificationFieldMatch(0.2, "some other guy"),
                Transcript = "[opening] something else entirely"
            };

            LibraryVerificationBackgroundService.ApplyVerdict(book, verdict, "base.en");

            Assert.Equal(VerificationStatus.AgentFlagged, book.VerificationStatus);
            Assert.Equal(0.88, book.VerificationConfidence);
            Assert.Equal("agent:whisper-base.en", book.VerifiedBy);
            Assert.Equal("deterministic:whisper-base.en", book.VerificationMethod);
            Assert.Equal("[opening] something else entirely", book.VerificationTranscript);
            Assert.NotNull(book.VerifiedAt);

            // Detail JSON is camelCase with string enums, per-field scores intact,
            // and the transcript NOT duplicated into it.
            Assert.NotNull(book.VerificationDetailJson);
            using var detail = JsonDocument.Parse(book.VerificationDetailJson!);
            Assert.Equal("mismatch", detail.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(0.2, detail.RootElement.GetProperty("authorMatch").GetProperty("score").GetDouble());
            Assert.False(detail.RootElement.TryGetProperty("transcript", out _));
        }
    }

    public class VerificationFileSelectionTests
    {
        private static AudiobookFile FileAt(string path) => new() { Path = path };

        private static AudiobookFile FileAt(string path, double seconds) => new() { Path = path, DurationSeconds = seconds };

        [Fact]
        public void SelectSampleFiles_TinyIdentStub_SpillsIntoFollowingFiles()
        {
            // Live case (record shape): a 2s "This is Audible." stub as file
            // 001, an 18s title announcement as 002, then chapters. The old
            // first-file-only window transcribed exactly "This is Audible."
            // and the book auto-flagged.
            var book = new Audiobook
            {
                Title = "Home Front",
                Files = new List<AudiobookFile>
                {
                    FileAt("/a/Home Front-001.mp3", 2),
                    FileAt("/a/Home Front-002.mp3", 18),
                    FileAt("/a/Home Front-003.mp3", 1520),
                    FileAt("/a/Home Front-004.mp3", 1800),
                }
            };

            var (opening, closing) = VerificationFileSelection.SelectSampleFiles(book, 90, 30);

            // 2 + 18 < 90, so the window needs the third file too.
            Assert.Equal(new[] { "/a/Home Front-001.mp3", "/a/Home Front-002.mp3", "/a/Home Front-003.mp3" }, opening);
            // The last file alone covers 30s.
            Assert.Equal(new[] { "/a/Home Front-004.mp3" }, closing);
        }

        [Fact]
        public void SelectSampleFiles_LongFirstFile_SingleFileWindows()
        {
            var book = new Audiobook
            {
                Title = "Normal Book",
                Files = new List<AudiobookFile>
                {
                    FileAt("/a/Normal Book-001.mp3", 1800),
                    FileAt("/a/Normal Book-002.mp3", 1800),
                }
            };

            var (opening, closing) = VerificationFileSelection.SelectSampleFiles(book, 90, 30);

            Assert.Equal(new[] { "/a/Normal Book-001.mp3" }, opening);
            Assert.Equal(new[] { "/a/Normal Book-002.mp3" }, closing);
        }

        [Fact]
        public void SelectSampleFiles_UnknownDurations_LegacySingleFileBehavior()
        {
            var book = new Audiobook
            {
                Title = "No Durations",
                Files = new List<AudiobookFile>
                {
                    FileAt("/a/No Durations-001.mp3"),
                    FileAt("/a/No Durations-002.mp3"),
                }
            };

            var (opening, closing) = VerificationFileSelection.SelectSampleFiles(book, 90, 30);

            Assert.Equal(new[] { "/a/No Durations-001.mp3" }, opening);
            Assert.Equal(new[] { "/a/No Durations-002.mp3" }, closing);
        }

        [Fact]
        public void SelectSampleFiles_AllTinyFiles_CapsTheWindow()
        {
            // A pathological many-tiny-files rip must not schedule dozens of
            // ffmpeg clips for one window.
            var book = new Audiobook
            {
                Title = "Tiny",
                Files = Enumerable.Range(1, 20)
                    .Select(i => FileAt($"/a/Tiny-{i:D3}.mp3", 3))
                    .ToList()
            };

            var (opening, _) = VerificationFileSelection.SelectSampleFiles(book, 90, 30);

            Assert.Equal(VerificationFileSelection.MaxWindowFiles, opening.Count);
        }

        [Fact]
        public void SelectSampleFiles_ClosingSpillsBackwardAcrossTinyTailFiles()
        {
            // Outro stub at the end: the closing window must reach back into
            // the preceding file, and come back in play order.
            var book = new Audiobook
            {
                Title = "Tail Stub",
                Files = new List<AudiobookFile>
                {
                    FileAt("/a/Tail Stub-001.mp3", 1800),
                    FileAt("/a/Tail Stub-002.mp3", 1800),
                    FileAt("/a/Tail Stub-003.mp3", 5),
                }
            };

            var (_, closing) = VerificationFileSelection.SelectSampleFiles(book, 90, 30);

            Assert.Equal(new[] { "/a/Tail Stub-002.mp3", "/a/Tail Stub-003.mp3" }, closing);
        }

        [Fact]
        public void SelectFirstAndLast_UsesNaturalOrder_NotAlphabetical()
        {
            // Alphabetically "Chapter 10" sorts before "Chapter 2"; natural sort
            // must pick "Chapter 1" first and "Chapter 10" last, or verification
            // would transcribe the middle of the book.
            var book = new Audiobook
            {
                Files = new List<AudiobookFile>
                {
                    FileAt("/books/x/Chapter 10.mp3"),
                    FileAt("/books/x/Chapter 2.mp3"),
                    FileAt("/books/x/Chapter 1.mp3"),
                }
            };

            var (first, last) = VerificationFileSelection.SelectFirstAndLast(book);

            Assert.Equal("/books/x/Chapter 1.mp3", first);
            Assert.Equal("/books/x/Chapter 10.mp3", last);
        }

        [Fact]
        public void SelectFirstAndLast_BackMatterFile_NeverPicksItAsOpening()
        {
            // The live 1984 case: "Appendix" sorts before "Chapter" alphabetically,
            // so natural order alone samples the Newspeak appendix instead of the
            // opening credits. Back matter must rank after the content — and since
            // back matter plays last, it also becomes the closing pick.
            var book = new Audiobook
            {
                Title = "1984",
                Files = new List<AudiobookFile>
                {
                    FileAt("/books/1984/1984 - Appendix.mp3"),
                    FileAt("/books/1984/1984 - Chapter 1.mp3"),
                    FileAt("/books/1984/1984 - Chapter 2.mp3"),
                }
            };

            var (first, last) = VerificationFileSelection.SelectFirstAndLast(book);

            Assert.Equal("/books/1984/1984 - Chapter 1.mp3", first);
            Assert.Equal("/books/1984/1984 - Appendix.mp3", last);
        }

        [Fact]
        public void SelectFirstAndLast_FrontMatterFile_BecomesTheOpeningPick()
        {
            // "Foreword" sorts after "Chapter" alphabetically, but its audio (and
            // the spoken credits) open the book.
            var book = new Audiobook
            {
                Title = "1984",
                Files = new List<AudiobookFile>
                {
                    FileAt("/books/1984/1984 - Chapter 1.mp3"),
                    FileAt("/books/1984/1984 - Chapter 2.mp3"),
                    FileAt("/books/1984/1984 - Foreword.mp3"),
                }
            };

            var (first, last) = VerificationFileSelection.SelectFirstAndLast(book);

            Assert.Equal("/books/1984/1984 - Foreword.mp3", first);
            Assert.Equal("/books/1984/1984 - Chapter 2.mp3", last);
        }

        [Fact]
        public void SelectFirstAndLast_TitleContainingKeyword_DoesNotPoisonClassification()
        {
            // The book's own title is stripped before keyword matching, so a
            // title like "The Epilogue" can't demote every file to back matter.
            var book = new Audiobook
            {
                Title = "The Epilogue",
                Files = new List<AudiobookFile>
                {
                    FileAt("/books/e/The Epilogue - Part 1.mp3"),
                    FileAt("/books/e/The Epilogue - Part 2.mp3"),
                }
            };

            var (first, last) = VerificationFileSelection.SelectFirstAndLast(book);

            Assert.Equal("/books/e/The Epilogue - Part 1.mp3", first);
            Assert.Equal("/books/e/The Epilogue - Part 2.mp3", last);
        }

        [Fact]
        public void SelectFirstAndLast_SingleFileBook_ReturnsSamePathTwice()
        {
            var book = new Audiobook { Files = new List<AudiobookFile> { FileAt("/books/y/book.m4b") } };

            var (first, last) = VerificationFileSelection.SelectFirstAndLast(book);

            Assert.Equal(first, last);
            Assert.Equal("/books/y/book.m4b", first);
        }

        [Fact]
        public void SelectFirstAndLast_FallsBackToLegacyFilePath()
        {
            var book = new Audiobook { FilePath = "/books/z/legacy.mp3", Files = null };

            var (first, last) = VerificationFileSelection.SelectFirstAndLast(book);

            Assert.Equal("/books/z/legacy.mp3", first);
            Assert.Equal("/books/z/legacy.mp3", last);
        }

        [Fact]
        public void SelectFirstAndLast_NoAudioAtAll_ReturnsNulls()
        {
            var book = new Audiobook();

            var (first, last) = VerificationFileSelection.SelectFirstAndLast(book);

            Assert.Null(first);
            Assert.Null(last);
        }
    }
}
