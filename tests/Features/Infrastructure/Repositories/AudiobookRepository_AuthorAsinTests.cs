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
using System.Collections.Generic;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    [Trait("Category", "AuthorAsinResolution")]
    public class AudiobookRepository_AuthorAsinTests
    {
        private static ListenArrDbContext NewDb() =>
            new(new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(System.Guid.NewGuid().ToString())
                .Options);

        [Fact(DisplayName = "Single-author book resolves that author's ASIN")]
        public async Task SingleAuthor_ReturnsAsin()
        {
            using var db = NewDb();
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Foundation",
                Authors = new List<string> { "Isaac Asimov" },
                AuthorAsins = new List<string> { "B003RY2ISS" },
            });
            await db.SaveChangesAsync();

            var repo = new AudiobookRepository(db);
            Assert.Equal("B003RY2ISS", await repo.GetAuthorAsinByNameAsync("Isaac Asimov"));
        }

        [Fact(DisplayName = "Multi-author anthology with a compacted ASIN subset does NOT leak a co-author's ASIN")]
        public async Task MultiAuthor_MisalignedAsins_DoesNotLeakCoAuthorAsin()
        {
            using var db = NewDb();
            // The live regression: Jeremiah Adelson's only book is an anthology that
            // also credits Asimov et al.; AuthorAsins is a 3-entry subset led by
            // Asimov's ASIN. Resolving "Jeremiah Adelson" must NOT return Asimov's.
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Retro Sci-Fi Compilations (Annotated): Volume 1",
                Authors = new List<string>
                {
                    "Jeremiah Adelson", "Isaac Asimov", "Fritz Leiber", "Poul Anderson", "Harry Bates",
                },
                AuthorAsins = new List<string> { "B003RY2ISS", "B000APW3UA", "B00456UFBO" },
            });
            await db.SaveChangesAsync();

            var repo = new AudiobookRepository(db);
            Assert.Null(await repo.GetAuthorAsinByNameAsync("Jeremiah Adelson"));
            // And it must not resolve Asimov from this misaligned array either.
            Assert.Null(await repo.GetAuthorAsinByNameAsync("Isaac Asimov"));
        }

        [Fact(DisplayName = "Position-aligned multi-author book maps each name to its own ASIN")]
        public async Task MultiAuthor_AlignedAsins_MapsByPosition()
        {
            using var db = NewDb();
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Co-Written",
                Authors = new List<string> { "James Patterson", "James O. Born" },
                AuthorAsins = new List<string> { "PATTERSON1", "BORN2" },
            });
            await db.SaveChangesAsync();

            var repo = new AudiobookRepository(db);
            Assert.Equal("PATTERSON1", await repo.GetAuthorAsinByNameAsync("James Patterson"));
            Assert.Equal("BORN2", await repo.GetAuthorAsinByNameAsync("James O. Born"));
        }

        [Fact(DisplayName = "A single-author book elsewhere still resolves even if an anthology also lists the author")]
        public async Task PrefersReliableSingleAuthorBook_OverMisalignedAnthology()
        {
            using var db = NewDb();
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Anthology",
                Authors = new List<string> { "Isaac Asimov", "Someone Else", "Third" },
                AuthorAsins = new List<string> { "WRONG_LEAD", "X" }, // misaligned (3 vs 2)
            });
            db.Audiobooks.Add(new Audiobook
            {
                Title = "I, Robot",
                Authors = new List<string> { "Isaac Asimov" },
                AuthorAsins = new List<string> { "B003RY2ISS" },
            });
            await db.SaveChangesAsync();

            var repo = new AudiobookRepository(db);
            Assert.Equal("B003RY2ISS", await repo.GetAuthorAsinByNameAsync("Isaac Asimov"));
        }
    }
}
