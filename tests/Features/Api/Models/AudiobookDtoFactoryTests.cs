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
using Microsoft.EntityFrameworkCore;
using Xunit;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Application.Mapping;

namespace Listenarr.Tests.Features.Api.Models
{
    public class AudiobookDtoFactoryTests
    {
        [Fact]
        public async Task BuildFromEntity_MapsFieldsAndFiles_AndComputesWanted()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new Audiobook
            {
                Title = "Factory Book",
                Authors = new System.Collections.Generic.List<string> { "Author One" },
                Edition = "Collector's Edition",
                Series = "Primary Series",
                SeriesNumber = "2",
                SeriesMemberships = new System.Collections.Generic.List<AudiobookSeriesMembership>
                {
                    new()
                    {
                        SeriesName = "Primary Series",
                        SeriesNumber = "2",
                        IsPrimary = true,
                        SortOrder = 0
                    },
                    new()
                    {
                        SeriesName = "Shared Universe",
                        SeriesNumber = "7",
                        IsPrimary = false,
                        SortOrder = 1
                    }
                },
                BasePath = "C:\\test\\book",
                Monitored = true
            };

            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var file = new AudiobookFile { AudiobookId = book.Id, Path = "C:\\test\\book\\file1.m4b", Size = 12345, CreatedAt = DateTime.UtcNow };
            db.AudiobookFiles.Add(file);
            await db.SaveChangesAsync();

            var updated = await db.Audiobooks
                .Include(a => a.Files)
                .Include(a => a.SeriesMemberships)
                .FirstOrDefaultAsync(a => a.Id == book.Id);

            var dto = AudiobookDtoFactory.BuildFromEntity(updated);

            Assert.Equal(book.Id, dto.Id);
            Assert.Equal(book.Title, dto.Title);
            Assert.Contains("Author One", dto.Authors ?? new string[] { });
            Assert.Equal(book.Edition, dto.Edition);
            Assert.Equal(book.BasePath, dto.BasePath);
            Assert.NotNull(dto.SeriesMemberships);
            Assert.Equal(2, dto.SeriesMemberships!.Length);
            Assert.Equal("Primary Series", dto.SeriesMemberships[0].SeriesName);
            Assert.True(dto.SeriesMemberships[0].IsPrimary);
            Assert.NotNull(dto.Files);
            Assert.Single(dto.Files);
            // With a file record present in DB, wanted should be false (has content)
            Assert.False(dto.Wanted == true, "With a file present, wanted should be false");
        }

        // Regression: prior to the natural-sort fix, BuildFromEntity projected files in
        // whatever order EF returned them — essentially scan-time insertion order, which is
        // undefined for multi-disc rips. The Files tab and the metadata-backfill modal's
        // "Preview my file" default (files[0]) both depend on a stable, human-natural order.
        [Fact]
        public async Task BuildFromEntity_OrdersMultiDiscFilesNaturallyByFilename()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new Audiobook { Title = "Multi Disc Book", Monitored = true };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            // Insert in shuffled order to defeat any "natural order" coming from row ids alone.
            var insertOrder = new[] { 8, 1, 6, 10, 7, 9, 11, 12, 3, 4, 13, 14, 2, 5 };
            foreach (var disc in insertOrder)
            {
                db.AudiobookFiles.Add(new AudiobookFile
                {
                    AudiobookId = book.Id,
                    Path = $"/library/Multi Disc Book/Tom Clancy - Command Authority Disc {disc:D2}.mp3",
                    CreatedAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();

            var updated = await db.Audiobooks.Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == book.Id);
            var dto = AudiobookDtoFactory.BuildFromEntity(updated);

            Assert.NotNull(dto.Files);
            Assert.Equal(14, dto.Files!.Length);
            var paths = dto.Files.Select(f => f.Path).ToArray();
            for (var i = 0; i < 14; i++)
            {
                var expectedDisc = i + 1;
                Assert.EndsWith($"Disc {expectedDisc:D2}.mp3", paths[i]);
            }
        }

        // Mixed naming: chapter numbers without zero-padding should still sort 1, 2, 10, 11
        // rather than the lexicographic 1, 10, 11, 2. Bonus content trailing the numbered set
        // should fall after the highest-numbered chapter, not interleave with it.
        [Fact]
        public async Task BuildFromEntity_OrdersMixedChapterAndBonusFilesNaturally()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new Audiobook { Title = "Mixed Book", Monitored = true };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var insertOrder = new[]
            {
                "/library/Mixed Book/Chapter 10.mp3",
                "/library/Mixed Book/Bonus - Afterword.mp3",
                "/library/Mixed Book/Chapter 1.mp3",
                "/library/Mixed Book/Chapter 2.mp3",
                "/library/Mixed Book/Chapter 11.mp3"
            };
            foreach (var path in insertOrder)
            {
                db.AudiobookFiles.Add(new AudiobookFile
                {
                    AudiobookId = book.Id,
                    Path = path,
                    CreatedAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();

            var updated = await db.Audiobooks.Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == book.Id);
            var dto = AudiobookDtoFactory.BuildFromEntity(updated);

            Assert.NotNull(dto.Files);
            var names = dto.Files!.Select(f => Path.GetFileName(f.Path)).ToArray();
            Assert.Equal(new[]
            {
                "Bonus - Afterword.mp3",
                "Chapter 1.mp3",
                "Chapter 2.mp3",
                "Chapter 10.mp3",
                "Chapter 11.mp3"
            }, names);
        }

        // Files with no path on disk (rare — possible if a row was created without a path)
        // should sort after files with paths, so the UI's "first audio file" defaults still
        // pick something playable.
        [Fact]
        public async Task BuildFromEntity_SortsFilesWithoutPathAfterPathedFiles()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new Audiobook { Title = "Has Pathless File", Monitored = true };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            db.AudiobookFiles.Add(new AudiobookFile { AudiobookId = book.Id, Path = null, CreatedAt = DateTime.UtcNow });
            db.AudiobookFiles.Add(new AudiobookFile { AudiobookId = book.Id, Path = "/library/Has Pathless File/Disc 02.mp3", CreatedAt = DateTime.UtcNow });
            db.AudiobookFiles.Add(new AudiobookFile { AudiobookId = book.Id, Path = "/library/Has Pathless File/Disc 01.mp3", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var updated = await db.Audiobooks.Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == book.Id);
            var dto = AudiobookDtoFactory.BuildFromEntity(updated);

            Assert.NotNull(dto.Files);
            Assert.Equal(3, dto.Files!.Length);
            Assert.EndsWith("Disc 01.mp3", dto.Files[0].Path);
            Assert.EndsWith("Disc 02.mp3", dto.Files[1].Path);
            Assert.Null(dto.Files[2].Path);
        }

        [Fact]
        public async Task BuildFromEntity_ComputesWantedWhenNoFiles()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new Audiobook
            {
                Title = "NoFiles Book",
                Monitored = true
            };

            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var updated = await db.Audiobooks.Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == book.Id);
            var dto = AudiobookDtoFactory.BuildFromEntity(updated);

            Assert.True(dto.Wanted == true);
        }
    }
}
