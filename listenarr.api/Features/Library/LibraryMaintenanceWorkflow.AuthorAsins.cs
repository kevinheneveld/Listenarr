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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    // Author-ASIN audit/repair, split out of LibraryMaintenanceWorkflow.cs to stay
    // under the architecture size cap.
    public sealed partial class LibraryMaintenanceWorkflow
    {
        private static readonly TimeSpan AuthorLookupPacing = TimeSpan.FromMilliseconds(400);

        public sealed record StaleAuthorAsin(string Asin, string ResolvedName);
        public sealed record StaleAuthorAsinBook(int Id, string Title, List<string> Authors, List<StaleAuthorAsin> StaleAsins);
        public sealed record AuthorAsinAuditResponse(
            int BooksChecked,
            int DistinctAsins,
            int ResolvedAsins,
            int UnresolvedAsins,
            List<StaleAuthorAsinBook> StaleBooks,
            int Repaired);

        /// <summary>
        /// Finds books whose stored author ASIN resolves to a name that shares nothing
        /// with the book's Authors — the leftover of relabelling a wrong grab, which
        /// rewrote Authors and kept the old author's ASIN. Resolution goes through the
        /// author cache first, then Audible (paced, capped per call, cached for next
        /// time). With <paramref name="repair"/> the stale ASINs are dropped and the
        /// book's authors re-resolved by name.
        /// </summary>
        public async Task<IActionResult> AuditAuthorAsinsAsync(bool repair, int maxLookups, CancellationToken ct)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            var region = string.IsNullOrWhiteSpace(settings?.DefaultSearchRegion) ? "us" : settings!.DefaultSearchRegion;
            var lookupBudget = Math.Clamp(maxLookups, 0, 2000);

            var books = await _repo.GetAllAsync();
            var distinctAsins = books
                .SelectMany(b => b.AuthorAsins ?? new List<string>())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var namesByAsin = await ResolveAuthorNamesAsync(distinctAsins, region, lookupBudget, ct);

            var stale = new List<StaleAuthorAsinBook>();
            foreach (var book in books)
            {
                ct.ThrowIfCancellationRequested();
                if (book.AuthorAsins == null || book.AuthorAsins.Count == 0)
                {
                    continue;
                }

                var authors = (book.Authors ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
                if (authors.Count == 0)
                {
                    continue;
                }

                var staleAsins = new List<StaleAuthorAsin>();
                foreach (var asin in book.AuthorAsins.Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    if (namesByAsin.TryGetValue(asin.Trim(), out var resolvedName)
                        && !string.IsNullOrWhiteSpace(resolvedName)
                        && !AuthorNameMatcher.SharesAnyName(resolvedName, authors))
                    {
                        staleAsins.Add(new StaleAuthorAsin(asin.Trim(), resolvedName!));
                    }
                }

                if (staleAsins.Count > 0)
                {
                    stale.Add(new StaleAuthorAsinBook(book.Id, book.Title ?? string.Empty, authors, staleAsins));
                }
            }

            var repaired = 0;
            if (repair)
            {
                repaired = await RepairStaleAuthorAsinsAsync(stale, ct);
            }

            _logger.LogInformation(
                "Author-ASIN audit: {Books} books, {Distinct} distinct ASINs, {Resolved} resolved, {Unresolved} unresolved, {Stale} stale book(s), {Repaired} repaired",
                books.Count, distinctAsins.Count, namesByAsin.Count, distinctAsins.Count - namesByAsin.Count, stale.Count, repaired);

            return new OkObjectResult(new AuthorAsinAuditResponse(
                books.Count,
                distinctAsins.Count,
                namesByAsin.Count,
                distinctAsins.Count - namesByAsin.Count,
                stale.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                repaired));
        }

        private async Task<Dictionary<string, string>> ResolveAuthorNamesAsync(
            List<string> asins,
            string region,
            int lookupBudget,
            CancellationToken ct)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<string>();
            foreach (var asin in asins)
            {
                ct.ThrowIfCancellationRequested();
                var cached = await _repo.GetCachedAuthorByAsinAsync(asin, region);
                if (cached != null && !string.IsNullOrWhiteSpace(cached.AuthorName))
                {
                    names[asin] = cached.AuthorName;
                }
                else
                {
                    pending.Add(asin);
                }
            }

            if (pending.Count == 0 || lookupBudget == 0 || _scopeFactory == null)
            {
                return names;
            }

            using var scope = _scopeFactory.CreateScope();
            var audible = scope.ServiceProvider.GetService<AudibleService>();
            if (audible == null)
            {
                return names;
            }

            var lookups = 0;
            foreach (var asin in pending)
            {
                ct.ThrowIfCancellationRequested();
                if (lookups >= lookupBudget)
                {
                    break;
                }

                lookups++;
                AuthorLookupItem? info = null;
                try
                {
                    info = await audible.GetAuthorByAsinAsync(asin, region);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Author-ASIN audit: lookup failed for {Asin}", asin);
                }

                if (info != null && !string.IsNullOrWhiteSpace(info.Name))
                {
                    names[asin] = info.Name.Trim();
                    await CacheResolvedAuthorAsync(asin, info, region);
                }

                if (lookups < pending.Count)
                {
                    await Task.Delay(AuthorLookupPacing, ct);
                }
            }

            return names;
        }

        private async Task CacheResolvedAuthorAsync(string asin, AuthorLookupItem info, string region)
        {
            try
            {
                var now = DateTime.UtcNow;
                await _repo.UpsertCachedAuthorAsync(new AuthorCacheEntry
                {
                    AuthorName = info.Name!.Trim(),
                    AuthorNameNormalized = AuthorNameMatcher.Normalize(info.Name),
                    AuthorAsin = asin,
                    Region = region,
                    ImageUrl = info.Image,
                    Description = info.Description,
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastFetchedAt = now
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Author-ASIN audit: could not cache author {Asin}", asin);
            }
        }

        private async Task<int> RepairStaleAuthorAsinsAsync(List<StaleAuthorAsinBook> stale, CancellationToken ct)
        {
            if (stale.Count == 0)
            {
                return 0;
            }

            var resolver = _authorAsinResolver
                ?? (_scopeFactory == null
                    ? null
                    : new AuthorAsinResolver(_scopeFactory, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthorAsinResolver>.Instance));

            var repaired = 0;
            foreach (var entry in stale)
            {
                ct.ThrowIfCancellationRequested();
                var book = await _repo.GetByIdAsync(entry.Id);
                if (book == null)
                {
                    continue;
                }

                var staleSet = entry.StaleAsins.Select(s => s.Asin).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var kept = (book.AuthorAsins ?? new List<string>())
                    .Where(a => !string.IsNullOrWhiteSpace(a) && !staleSet.Contains(a.Trim()))
                    .ToList();

                if (resolver != null && kept.Count < (book.Authors?.Count ?? 0))
                {
                    foreach (var asin in await resolver.ResolveAsync(book.Authors, ct))
                    {
                        if (!kept.Contains(asin, StringComparer.OrdinalIgnoreCase))
                        {
                            kept.Add(asin);
                        }
                    }
                }

                book.AuthorAsins = kept;
                if (await _repo.UpdateAsync(book))
                {
                    repaired++;
                    _logger.LogInformation(
                        "Author-ASIN audit: repaired audiobook {Id} '{Title}' — dropped {Stale}, now {Now}",
                        book.Id, LogRedaction.SanitizeText(book.Title), string.Join(",", staleSet),
                        kept.Count > 0 ? string.Join(",", kept) : "none");
                }
            }

            return repaired;
        }
    }
}
