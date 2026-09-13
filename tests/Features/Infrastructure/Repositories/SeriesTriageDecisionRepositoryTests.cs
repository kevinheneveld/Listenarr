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

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    [Trait("Area", "Persistence")]
    [Trait("Name", "SeriesTriageDecisionRepositoryTests")]
    [Trait("Category", "Series")]
    public class SeriesTriageDecisionRepositoryTests : BaseTests
    {
        private ISeriesTriageDecisionRepository Repo() => _provider.GetRequiredService<ISeriesTriageDecisionRepository>();

        private static SeriesTriageDecision Decision(string name, string? note = null) => new()
        {
            SeriesName = name,
            SeriesNameNormalized = SeriesNameNormalizer.Normalize(name),
            Note = note,
            CreatedAt = DateTime.UtcNow
        };

        [Fact]
        public async Task Upsert_IsIdempotentOnNormalizedName()
        {
            var repo = Repo();

            await repo.UpsertAsync(Decision("The Dresden Files", "maybe later"));
            await repo.UpsertAsync(Decision("the dresden files!", "not for me"));

            var all = await repo.GetAllAsync();
            var only = Assert.Single(all);
            Assert.Equal("the dresden files", only.SeriesNameNormalized);
            Assert.Equal("the dresden files!", only.SeriesName);
            Assert.Equal("not for me", only.Note);
            Assert.Equal(SeriesTriageDecision.Dismissed, only.Decision);
        }

        [Fact]
        public async Task DeleteByNormalizedName_RemovesTheDecision()
        {
            var repo = Repo();
            await repo.UpsertAsync(Decision("Jack Reacher"));

            Assert.True(await repo.DeleteByNormalizedNameAsync("jack reacher"));
            Assert.False(await repo.DeleteByNormalizedNameAsync("jack reacher"));
            Assert.Empty(await repo.GetAllAsync());
        }

        [Fact]
        public void Normalizer_StripsAccentsPunctuationAndCase()
        {
            Assert.Equal("les miserables vol 2", SeriesNameNormalizer.Normalize("  Les Misérables — Vol. 2 "));
            Assert.Equal(string.Empty, SeriesNameNormalizer.Normalize("   "));
        }
    }
}
